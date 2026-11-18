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
        string? ServiceType,
        string? Name,
        string? Phone,
        string? Email,
        string? Placement,
        string? IdeaDescription);

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

                var (isOpen, note, slots) = BookingStore.GetStudioSchedule(date);
                if (!isOpen)
                    return await JsonResponse(req, HttpStatusCode.OK, new { ok = true, isOpen = false, note, slots = Array.Empty<object>() });

                var allBookings = await store.ListAsync();
                var takenSlots = allBookings
                    .Where(b => string.Equals(b.Date, dateStr, StringComparison.OrdinalIgnoreCase)
                                && (string.Equals(b.Artist, reqArtist, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(reqArtist, "First Available Artist", StringComparison.OrdinalIgnoreCase))
                                && b.Status is "Confirmed" or "ApprovedAwaitingDeposit")
                    .Select(b => b.TimeSlot)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var slotList = slots.Select(s => new { time = s, available = !takenSlots.Contains(s) }).ToArray();
                return await JsonResponse(req, HttpStatusCode.OK, new { ok = true, isOpen = true, note, slots = slotList });
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

                var endLocal = startLocal.AddMinutes(90);
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

            if (query.TryGetValue("id", out var lookupId) && !string.IsNullOrWhiteSpace(lookupId))
            {
                var single = await store.GetAsync(lookupId.Trim());
                if (single is null)
                    return await JsonResponse(req, HttpStatusCode.NotFound, new { ok = false, error = "Booking not found." });

                return await JsonResponse(req, HttpStatusCode.OK, new { ok = true, booking = single });
            }

            if (!IsAuthorized(req))
                return await JsonResponse(req, HttpStatusCode.Unauthorized, new { ok = false, error = "Invalid studio PIN." });

            var bookings = await store.ListAsync();
            return await JsonResponse(req, HttpStatusCode.OK, new { ok = true, bookings });
        }

        // POST: admin actions, simulated deposit, or new booking request
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

        if (postOp == "seed")
        {
            if (!IsAuthorized(req))
                return await JsonResponse(req, HttpStatusCode.Unauthorized, new { ok = false, error = "Invalid studio PIN." });

            var existing = await store.ListAsync();
            if (existing.Count == 0)
            {
                var nextOpen = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
                while (nextOpen.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Tuesday)
                    nextOpen = nextOpen.AddDays(1);

                var demo = new BookingRecord
                {
                    Id = "ST-DEMO01",
                    Artist = "Dawn Yermasek",
                    Date = nextOpen.ToString("yyyy-MM-dd"),
                    TimeSlot = "11:30 AM",
                    ServiceType = "Custom Tattoo",
                    Name = "Sample Client",
                    Phone = "(423) 555-0142",
                    Email = "client@example.com",
                    Placement = "Outer forearm, ~5 in",
                    IdeaDescription = "Watercolor luna moth with fine-line fern botanicals.",
                    Status = "PendingApproval",
                    DepositAmountCents = GetDefaultDepositCents(),
                    DepositPaid = false,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                await store.UpsertAsync(demo);
            }

            return await JsonResponse(req, HttpStatusCode.OK, new { ok = true });
        }

        if (postOp is "approve" or "confirm" or "confirm-manual" or "decline" or "simulate-deposit")
        {
            if (postOp != "simulate-deposit" && !IsAuthorized(req))
                return await JsonResponse(req, HttpStatusCode.Unauthorized, new { ok = false, error = "Invalid studio PIN." });

            if (string.IsNullOrWhiteSpace(payload.Id))
                return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "Booking id is required." });

            var item = await store.GetAsync(payload.Id);
            if (item is null)
                return await JsonResponse(req, HttpStatusCode.NotFound, new { ok = false, error = "Booking not found." });

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

        var (studioOpen, _, validSlots) = BookingStore.GetStudioSchedule(parsedDate);
        if (!studioOpen || !validSlots.Contains(timeSlot, StringComparer.OrdinalIgnoreCase))
            return await JsonResponse(req, HttpStatusCode.BadRequest, new { ok = false, error = "Selected date or time slot is not open." });

        var currentBookings = await store.ListAsync();
        var conflict = currentBookings.Any(b =>
            string.Equals(b.Date, dateStrNew, StringComparison.OrdinalIgnoreCase)
            && string.Equals(b.TimeSlot, timeSlot, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(b.Artist, artist, StringComparison.OrdinalIgnoreCase)
                || string.Equals(artist, "First Available Artist", StringComparison.OrdinalIgnoreCase))
            && b.Status is "Confirmed" or "ApprovedAwaitingDeposit");

        if (conflict)
            return await JsonResponse(req, HttpStatusCode.Conflict, new { ok = false, error = "That time slot was just booked. Please pick another slot." });

        var record = new BookingRecord
        {
            Id = $"ST-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            Artist = artist,
            Date = dateStrNew,
            TimeSlot = timeSlot,
            ServiceType = (payload.ServiceType ?? "Custom Tattoo").Trim(),
            Name = (payload.Name ?? string.Empty).Trim(),
            Phone = (payload.Phone ?? string.Empty).Trim(),
            Email = (payload.Email ?? string.Empty).Trim(),
            Placement = (payload.Placement ?? string.Empty).Trim(),
            IdeaDescription = (payload.IdeaDescription ?? string.Empty).Trim(),
            Status = "PendingApproval",
            DepositAmountCents = GetDefaultDepositCents(),
            DepositPaid = false,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var (ok, err) = await store.UpsertAsync(record);
        if (!ok)
            return await JsonResponse(req, HttpStatusCode.BadGateway, new { ok = false, error = err });

        return await JsonResponse(req, HttpStatusCode.Created, new { ok = true, booking = record });
    }
}
