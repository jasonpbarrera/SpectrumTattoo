using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace SpectrumTattoo.Api;

public class BookingsFunction(
    IHttpClientFactory httpClientFactory,
    ILogger<BookingsFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private BookingStore CreateStore() => new(httpClientFactory, logger);

    public sealed record BookingPostPayload(
        string? Op,
        string? Id,
        string? ArtistNote,
        int? DepositAmountCents,
        string? Artist,
        string? Date,
        string? TimeSlot,
        string? EndTime,
        string? ServiceType,
        string? Name,
        string? Phone,
        string? Email,
        string? Placement,
        string? IdeaDescription,
        List<string>? ReferenceImages,
        bool? AllDay,
        string? StartTime,
        string? Reason,
        Dictionary<string, DaySchedule>? Days,
        string? StudioNotes);

    private static List<string> SanitizeImages(List<string>? raw)
    {
        if (raw is null || raw.Count == 0) return [];
        var valid = new List<string>(3);
        foreach (var img in raw)
        {
            if (valid.Count >= 3) break;
            if (string.IsNullOrWhiteSpace(img) || img.Length > 350_000) continue;
            if (img.StartsWith("data:image/webp;base64,", StringComparison.OrdinalIgnoreCase)
                || img.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase)
                || img.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase))
            {
                valid.Add(img);
            }
        }
        return valid;
    }

    private static string GetAdminPin() =>
        Environment.GetEnvironmentVariable("STUDIO_ADMIN_PIN") ?? "4110";

    private static int GetDefaultDepositCents() =>
        int.TryParse(Environment.GetEnvironmentVariable("DEPOSIT_AMOUNT_CENTS"), out var cents) && cents > 0
            ? cents
            : 5000;

    private static bool IsAuthorized(HttpRequestData req)
    {
        if (!req.Headers.TryGetValues("X-Studio-Pin", out var values))
            return false;

        var provided = values.FirstOrDefault()?.Trim();
        return string.Equals(provided, GetAdminPin(), StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ParseQuery(string rawQuery)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rawQuery)) return dict;
        foreach (var pair in rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0].Replace('+', ' '));
            var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : string.Empty;
            dict[key] = val;
        }
        return dict;
    }

    private static string NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits.StartsWith('1'))
            digits = digits[1..];
        return digits;
    }

    private static async Task<HttpResponseData> JsonResponse(HttpRequestData req, HttpStatusCode status, object body)
    {
        var resp = req.CreateResponse(status);
        await resp.WriteAsJsonAsync(body);
        return resp;
    }

    [Function("Bookings")]
    public async Task<HttpResponseData> HandleBookings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "bookings")] HttpRequestData req)
    {
        var store = CreateStore();
        var query = ParseQuery(req.Url.Query);

        if (string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            var op = query.TryGetValue("op", out var opVal) ? opVal.Trim().ToLowerInvariant() : string.Empty;

            if (op == "availability")
            {
                var reqArtist = (query.TryGetValue("artist", out var a) ? a : "First Available Artist").Trim();
                var dateStr = (query.TryGetValue("date", out var d) ? d : string.Empty).Trim();

                if (!DateOnly.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "Invalid date format. Expected yyyy-MM-dd." });

                var bookingsTask = store.ListAsync();
                var blocksTask = store.ListBlocksAsync();
                var schedulesTask = store.ListSchedulesAsync();
                await Task.WhenAll(bookingsTask, blocksTask, schedulesTask);

                var (isOpen, note, slots) = BookingStore.ComputeAvailability(
                    reqArtist, date, bookingsTask.Result, blocksTask.Result, schedulesTask.Result);

                var slotPayload = slots.Select(s => new { time = s.Time, available = s.Available }).ToArray();
                return await JsonResponse(req, HttpStatusCode.OK, new { ok = true, isOpen, note, slots = slotPayload });
            }

            if (op == "ics")
            {
                if (!query.TryGetValue("id", out var icsId) || string.IsNullOrWhiteSpace(icsId))
                    return req.CreateResponse(HttpStatusCode.BadRequest);

                var b = await store.GetAsync(icsId.Trim());
                if (b is null)
                    return req.CreateResponse(HttpStatusCode.NotFound);

                DateTime.TryParseExact($"{b.Date} {b.TimeSlot}", "yyyy-MM-dd h:mm tt",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var startLocal);
                if (startLocal == default)
                    startLocal = DateTime.UtcNow.AddDays(1);

                DateTime endLocal = startLocal.AddMinutes(90);
                if (!string.IsNullOrWhiteSpace(b.EndTime)
                    && DateTime.TryParseExact($"{b.Date} {b.EndTime}", "yyyy-MM-dd h:mm tt",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd)
                    && parsedEnd > startLocal)
                {
                    endLocal = parsedEnd;
                }

                var ics = new StringBuilder()
                    .AppendLine("BEGIN:VCALENDAR")
                    .AppendLine("VERSION:2.0")
                    .AppendLine("PRODID:-//Spectrum Tattoo//Booking//EN")
                    .AppendLine("BEGIN:VEVENT")
                    .AppendLine($"UID:{b.Id}@spectrumtattoo")
                    .AppendLine($"DTSTAMP:{DateTime.UtcNow:yyyyMMddTHHmmssZ}")
                    .AppendLine($"DTSTART:{startLocal:yyyyMMddTHHmmss}")
                    .AppendLine($"DTEND:{endLocal:yyyyMMddTHHmmss}")
                    .AppendLine($"SUMMARY:Spectrum Tattoo - {b.ServiceType} ({b.Artist})")
                    .AppendLine($"DESCRIPTION:Booking {b.Id} | Status: {b.Status} | (423) 299-9111")
                    .AppendLine("LOCATION:6165 US-411\\, Benton\\, TN 37307")
                    .AppendLine("END:VEVENT")
                    .AppendLine("END:VCALENDAR")
                    .ToString();

                var icsResp = req.CreateResponse(HttpStatusCode.OK);
                icsResp.Headers.Add("Content-Type", "text/calendar; charset=utf-8");
                icsResp.Headers.Add("Content-Disposition", $"attachment; filename=\"spectrum-tattoo-{b.Id}.ics\"");
                await icsResp.WriteStringAsync(ics);
                return icsResp;
            }

            if (op == "client-prefill")
            {
                var phoneParam = query.TryGetValue("phone", out var pVal) ? pVal : string.Empty;
                var digits = NormalizePhone(phoneParam);
                if (digits.Length < 7)
                    return await JsonResponse(req, HttpStatusCode.OK, new { ok = true, found = false });

                var allBookings = await store.ListAsync();
                var profile = await store.GetClientProfileByPhoneAsync(digits, allBookings);
                if (profile is null)
                    return await JsonResponse(req, HttpStatusCode.OK, new { ok = true, found = false });

                // Intentionally exclude StudioNotes so artist notes remain private to studio staff
                return await JsonResponse(req, HttpStatusCode.OK, new
                {
                    ok = true,
                    found = true,
                    name = profile.Name,
                    email = profile.Email,
                    preferredArtist = profile.PreferredArtist
                });
            }

            if (query.TryGetValue("lookup", out var lookupTerm) && !string.IsNullOrWhiteSpace(lookupTerm))
            {
                var term = lookupTerm.Trim();
                var digits = NormalizePhone(term);
                var all = await store.ListAsync();

                var matches = all.Where(b =>
                    string.Equals(b.Id, term, StringComparison.OrdinalIgnoreCase)
                    || (digits.Length >= 7 && NormalizePhone(b.Phone) == digits))
                    .Select(b => new
                    {
                        b.Id,
                        b.Artist,
                        b.Date,
                        b.TimeSlot,
                        b.EndTime,
                        b.ServiceType,
                        b.Name,
                        b.Status,
                        b.DepositAmountCents,
                        b.DepositPaid,
                        b.ArtistNote,
                        b.CreatedAt
                    })
                    .ToList();

                if (matches.Count == 0)
                    return await JsonResponse(req, HttpStatusCode.NotFound, new { ok = false, error = "No bookings found for that phone number or code." });

                return await JsonResponse(req, HttpStatusCode.OK, new { ok = true, booking = matches[0], bookings = matches });
            }

            if (query.TryGetValue("phone", out var phoneQuery) && !string.IsNullOrWhiteSpace(phoneQuery))
            {
                var digits = NormalizePhone(phoneQuery);
                if (digits.Length < 7)
                    return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "Please enter a valid phone number." });

                var all = await store.ListAsync();
                var matches = all.Where(b => NormalizePhone(b.Phone) == digits)
                    .Select(b => new
                    {
                        b.Id,
                        b.Artist,
                        b.Date,
                        b.TimeSlot,
                        b.EndTime,
                        b.ServiceType,
                        b.Name,
                        b.Status,
                        b.DepositAmountCents,
                        b.DepositPaid,
                        b.ArtistNote,
                        b.CreatedAt
                    })
                    .ToList();

                if (matches.Count == 0)
                    return await JsonResponse(req, HttpStatusCode.NotFound, new { ok = false, error = "No bookings found for that phone number." });

                return await JsonResponse(req, HttpStatusCode.OK, new { ok = true, booking = matches[0], bookings = matches });
            }

            if (query.TryGetValue("id", out var lookupId) && !string.IsNullOrWhiteSpace(lookupId))
            {
                var single = await store.GetAsync(lookupId.Trim());
                if (single is null)
                    return await JsonResponse(req, HttpStatusCode.NotFound, new { ok = false, error = "Booking not found." });

                return await JsonResponse(req, HttpStatusCode.OK, new { ok = true, booking = single, bookings = new[] { single } });
            }

            if (!IsAuthorized(req))
                return await JsonResponse(req, HttpStatusCode.Unauthorized, new { ok = false, error = "Invalid studio PIN." });

            var bookingsTaskAdmin = store.ListAsync();
            var blocksTaskAdmin = store.ListBlocksAsync();
            var schedulesTaskAdmin = store.ListSchedulesAsync();
            await Task.WhenAll(bookingsTaskAdmin, blocksTaskAdmin, schedulesTaskAdmin);

            var bookings = bookingsTaskAdmin.Result;
            await store.CleanupExpiredPhotosAsync(bookings);
            var clients = await store.ListClientProfilesAsync(bookings);

            return await JsonResponse(req, HttpStatusCode.OK, new
            {
                ok = true,
                storage = store.IsCosmosConfigured ? "cosmos" : "memory",
                bookings,
                blocks = blocksTaskAdmin.Result,
                schedules = schedulesTaskAdmin.Result,
                clients
            });
        }

        // POST: admin actions, schedules, blocks, simulated deposit, or new booking request
        BookingPostPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<BookingPostPayload>(req.Body, JsonOpts);
        }
        catch
        {
            return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "Invalid booking payload." });
        }

        if (payload is null)
            return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "Empty payload." });

        var postOp = (payload.Op ?? string.Empty).Trim().ToLowerInvariant();

        if (postOp == "add-block")
        {
            if (!IsAuthorized(req))
                return await JsonResponse(req, HttpStatusCode.Unauthorized, new { ok = false, error = "Invalid studio PIN." });

            var blkDate = (payload.Date ?? string.Empty).Trim();
            if (!DateOnly.TryParseExact(blkDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "Please select a valid date." });

            bool allDay = payload.AllDay ?? false;
            string? startT = allDay ? null : (payload.StartTime ?? "10:00 AM").Trim();
            string? endT = allDay ? null : (payload.EndTime ?? "6:00 PM").Trim();

            if (!allDay)
            {
                if (!BookingStore.TryParseMinutes(startT, out var sm) || !BookingStore.TryParseMinutes(endT, out var em) || em <= sm)
                    return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "End time must be after start time." });
            }

            var block = new TimeBlockRecord
            {
                Artist = string.IsNullOrWhiteSpace(payload.Artist) ? "All Artists" : payload.Artist.Trim(),
                Date = blkDate,
                AllDay = allDay,
                StartTime = startT,
                EndTime = endT,
                Reason = string.IsNullOrWhiteSpace(payload.Reason) ? null : payload.Reason.Trim(),
                CreatedAt = DateTimeOffset.UtcNow
            };

            var (savedBlk, blkErr) = await store.UpsertBlockAsync(block);
            if (!savedBlk)
                return await JsonResponse(req, HttpStatusCode.BadGateway, new { ok = false, error = blkErr });

            return await JsonResponse(req, HttpStatusCode.Created, new { ok = true, block });
        }

        if (postOp == "delete-block")
        {
            if (!IsAuthorized(req))
                return await JsonResponse(req, HttpStatusCode.Unauthorized, new { ok = false, error = "Invalid studio PIN." });

            if (string.IsNullOrWhiteSpace(payload.Id))
                return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "Block id is required." });

            var (delBlk, delBlkErr) = await store.DeleteBlockAsync(payload.Id);
            if (!delBlk)
                return await JsonResponse(req, HttpStatusCode.BadGateway, new { ok = false, error = delBlkErr });

            return await JsonResponse(req, HttpStatusCode.OK, new { ok = true, deletedId = payload.Id.Trim() });
        }

        if (postOp == "save-schedule")
        {
            if (!IsAuthorized(req))
                return await JsonResponse(req, HttpStatusCode.Unauthorized, new { ok = false, error = "Invalid studio PIN." });

            if (string.IsNullOrWhiteSpace(payload.Artist) || payload.Days is null || payload.Days.Count == 0)
                return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "Artist and weekly schedule days are required." });

            var sched = new ArtistScheduleRecord
            {
                Artist = payload.Artist.Trim(),
                Days = payload.Days
            };

            var (savedSched, schedErr) = await store.UpsertScheduleAsync(sched);
            if (!savedSched)
                return await JsonResponse(req, HttpStatusCode.BadGateway, new { ok = false, error = schedErr });

            return await JsonResponse(req, HttpStatusCode.OK, new { ok = true, schedule = sched });
        }

        if (postOp == "save-client-profile")
        {
            if (!IsAuthorized(req))
                return await JsonResponse(req, HttpStatusCode.Unauthorized, new { ok = false, error = "Invalid studio PIN." });

            var phoneKey = NormalizePhone(payload.Phone);
            if (phoneKey.Length < 7)
                return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "A valid phone number is required." });

            var existingProfile = await store.GetClientProfileByPhoneAsync(phoneKey);
            var profile = existingProfile ?? new ClientProfileRecord { PhoneKey = phoneKey };

            if (!string.IsNullOrWhiteSpace(payload.Name)) profile.Name = payload.Name.Trim();
            if (!string.IsNullOrWhiteSpace(payload.Phone)) profile.Phone = payload.Phone.Trim();
            if (!string.IsNullOrWhiteSpace(payload.Email)) profile.Email = payload.Email.Trim();
            if (!string.IsNullOrWhiteSpace(payload.Artist)) profile.PreferredArtist = payload.Artist.Trim();
            if (payload.StudioNotes is not null) profile.StudioNotes = payload.StudioNotes.Trim();

            var (savedProf, profErr) = await store.UpsertClientProfileAsync(profile);
            if (!savedProf)
                return await JsonResponse(req, HttpStatusCode.BadGateway, new { ok = false, error = profErr });

            return await JsonResponse(req, HttpStatusCode.OK, new { ok = true, client = profile });
        }

        if (postOp == "delete")
        {
            if (!IsAuthorized(req))
                return await JsonResponse(req, HttpStatusCode.Unauthorized, new { ok = false, error = "Invalid studio PIN." });

            if (string.IsNullOrWhiteSpace(payload.Id))
                return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "Booking id is required." });

            var (deleted, delErr) = await store.DeleteAsync(payload.Id);
            if (!deleted)
                return await JsonResponse(req, HttpStatusCode.BadGateway, new { ok = false, error = delErr });

            return await JsonResponse(req, HttpStatusCode.OK, new { ok = true, deletedId = payload.Id.Trim() });
        }

        if (postOp is "update-booking" or "approve" or "confirm" or "confirm-manual" or "decline" or "cancel" or "complete" or "clear-photos" or "simulate-deposit")
        {
            if (postOp is not "simulate-deposit" and not "cancel" && !IsAuthorized(req))
                return await JsonResponse(req, HttpStatusCode.Unauthorized, new { ok = false, error = "Invalid studio PIN." });

            if (string.IsNullOrWhiteSpace(payload.Id))
                return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "Booking id is required." });

            var item = await store.GetAsync(payload.Id);
            if (item is null)
                return await JsonResponse(req, HttpStatusCode.NotFound, new { ok = false, error = "Booking not found." });

            // Allow artists/owners to edit Date, Start Time, End Time, and Artist
            if (postOp is "update-booking" or "approve" or "confirm" or "confirm-manual")
            {
                if (!string.IsNullOrWhiteSpace(payload.Date)
                    && DateOnly.TryParseExact(payload.Date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    item.Date = payload.Date.Trim();
                }

                if (!string.IsNullOrWhiteSpace(payload.Artist))
                    item.Artist = payload.Artist.Trim();

                if (!string.IsNullOrWhiteSpace(payload.TimeSlot) && BookingStore.TryParseMinutes(payload.TimeSlot, out var newStart))
                {
                    item.TimeSlot = BookingStore.FormatMinutes(newStart);
                }

                if (!string.IsNullOrWhiteSpace(payload.EndTime) && BookingStore.TryParseMinutes(payload.EndTime, out var newEnd))
                {
                    if (BookingStore.TryParseMinutes(item.TimeSlot, out var curStart) && newEnd <= curStart)
                        return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "End time must be after start time." });
                    item.EndTime = BookingStore.FormatMinutes(newEnd);
                }
            }

            if (postOp == "approve")
            {
                item.Status = "ApprovedAwaitingDeposit";
                item.DepositPaid = false;
            }
            else if (postOp is "confirm" or "confirm-manual" or "simulate-deposit")
            {
                item.Status = "Confirmed";
                item.DepositPaid = true;
            }
            else if (postOp == "decline")
            {
                item.Status = "Declined";
                item.DepositPaid = false;
                item.ReferenceImages.Clear();
            }
            else if (postOp == "cancel")
            {
                item.Status = "Cancelled";
                item.ReferenceImages.Clear();
            }
            else if (postOp == "complete")
            {
                item.Status = "Completed";
                item.ReferenceImages.Clear();
            }
            else if (postOp == "clear-photos")
            {
                item.ReferenceImages.Clear();
            }

            if (payload.ArtistNote is not null) item.ArtistNote = payload.ArtistNote;
            if (payload.DepositAmountCents is >= 1000) item.DepositAmountCents = payload.DepositAmountCents.Value;

            var (saved, saveErr) = await store.UpsertAsync(item);
            if (!saved)
                return await JsonResponse(req, HttpStatusCode.BadGateway, new { ok = false, error = saveErr });

            return await JsonResponse(req, HttpStatusCode.OK, new { ok = true, booking = item });
        }

        // Create new booking
        var artist = (payload.Artist ?? "First Available Artist").Trim();
        var dateStrNew = (payload.Date ?? string.Empty).Trim();
        var timeSlot = (payload.TimeSlot ?? string.Empty).Trim();

        if (!DateOnly.TryParseExact(dateStrNew, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "Please select a valid date." });

        var allBookingsTask = store.ListAsync();
        var allBlocksTask = store.ListBlocksAsync();
        var allSchedulesTask = store.ListSchedulesAsync();
        await Task.WhenAll(allBookingsTask, allBlocksTask, allSchedulesTask);

        var (studioOpen, _, computedSlots) = BookingStore.ComputeAvailability(
            artist, parsedDate, allBookingsTask.Result, allBlocksTask.Result, allSchedulesTask.Result);

        var matchedSlot = computedSlots.FirstOrDefault(s => string.Equals(s.Time, timeSlot, StringComparison.OrdinalIgnoreCase));
        if (!studioOpen || string.IsNullOrEmpty(matchedSlot.Time))
            return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "Selected date or time slot is not open." });

        if (!matchedSlot.Available)
            return await JsonResponse(req, HttpStatusCode.Conflict, new { ok = false, error = "That time slot is no longer available. Please pick another slot." });

        var record = new BookingRecord
        {
            Id = $"ST-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            Artist = artist,
            Date = dateStrNew,
            TimeSlot = matchedSlot.Time,
            EndTime = BookingStore.ComputeDefaultEndTime(matchedSlot.Time),
            ServiceType = (payload.ServiceType ?? "Custom Tattoo").Trim(),
            Name = (payload.Name ?? string.Empty).Trim(),
            Phone = (payload.Phone ?? string.Empty).Trim(),
            Email = (payload.Email ?? string.Empty).Trim(),
            Placement = (payload.Placement ?? string.Empty).Trim(),
            IdeaDescription = (payload.IdeaDescription ?? string.Empty).Trim(),
            ReferenceImages = SanitizeImages(payload.ReferenceImages),
            Status = "PendingApproval",
            DepositAmountCents = GetDefaultDepositCents(),
            DepositPaid = false,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var (ok, err) = await store.UpsertAsync(record);
        if (!ok)
            return await JsonResponse(req, HttpStatusCode.BadGateway, new { ok = false, error = err });

        await store.SyncClientProfileFromBookingAsync(record);
        return await JsonResponse(req, HttpStatusCode.Created, new { ok = true, booking = record });
    }
}
