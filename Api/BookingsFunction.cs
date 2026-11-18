using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace SpectrumTattoo.Api;

public sealed class BookingsFunction(
    IHttpClientFactory httpClientFactory,
    ILogger<BookingsFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private BookingStore CreateStore() => new(httpClientFactory, logger);

    public sealed record CreateBookingRequest(
        string Artist,
        string Date,
        string TimeSlot,
        string ServiceType,
        string Name,
        string Phone,
        string Email,
        string Placement,
        string IdeaDescription);

    public sealed record BookingActionRequest(
        string Id,
        string Action,
        string? ArtistNote,
        int? DepositAmountCents);

    private static string GetAdminPin() =>
        Environment.GetEnvironmentVariable("STUDIO_ADMIN_PIN") ?? "4110";

    private static int GetDefaultDepositCents() =>
        int.TryParse(Environment.GetEnvironmentVariable("DEPOSIT_AMOUNT_CENTS"), out var cents) && cents > 0
            ? cents
            : 5000;

    private static bool IsStripeConfigured() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY"));

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

    [Function("GetAvailability")]
    public async Task<HttpResponseData> GetAvailability(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "availability")] HttpRequestData req)
    {
        var query = ParseQuery(req.Url.Query);
        var artist = (query.TryGetValue("artist", out var a) ? a : "First Available Artist").Trim();
        var dateStr = (query.TryGetValue("date", out var d) ? d : string.Empty).Trim();

        if (!DateOnly.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { ok = false, error = "Invalid date format. Expected yyyy-MM-dd." });
            return bad;
        }

        var store = CreateStore();
        var (isOpen, note, slots) = BookingStore.GetStudioSchedule(date);
        var allBookings = await store.ListAsync();

        var takenSlots = allBookings
            .Where(b => string.Equals(b.Date, dateStr, StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(b.Artist, artist, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(artist, "First Available Artist", StringComparison.OrdinalIgnoreCase))
                        && (b.Status == "Confirmed" || b.Status == "ApprovedAwaitingDeposit"))
            .ToDictionary(b => b.TimeSlot, b => b.Status, StringComparer.OrdinalIgnoreCase);

        var pendingCounts = allBookings
            .Where(b => string.Equals(b.Date, dateStr, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(b.Artist, artist, StringComparison.OrdinalIgnoreCase)
                        && b.Status == "PendingApproval")
            .GroupBy(b => b.TimeSlot, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var slotList = slots.Select(s => new
        {
            time = s,
            available = !takenSlots.ContainsKey(s),
            pendingRequests = pendingCounts.TryGetValue(s, out var c) ? c : 0
        });

        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(new
        {
            ok = true,
            date = dateStr,
            artist,
            isOpen,
            note,
            depositAmountCents = GetDefaultDepositCents(),
            stripeConfigured = IsStripeConfigured(),
            slots = slotList
        });
        return ok;
    }

    [Function("BookingsEndpoint")]
    public async Task<HttpResponseData> BookingsCollection(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "bookings")] HttpRequestData req)
    {
        var store = CreateStore();

        if (string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            var query = ParseQuery(req.Url.Query);
            if (query.TryGetValue("id", out var lookupId) && !string.IsNullOrWhiteSpace(lookupId))
            {
                var single = await store.GetAsync(lookupId.Trim());
                if (single is null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new { ok = false, error = "Booking not found." });
                    return notFound;
                }

                var singleOk = req.CreateResponse(HttpStatusCode.OK);
                await singleOk.WriteAsJsonAsync(new
                {
                    ok = true,
                    booking = new
                    {
                        single.Id,
                        single.Artist,
                        single.Date,
                        single.TimeSlot,
                        single.ServiceType,
                        single.Name,
                        single.Status,
                        single.DepositAmountCents,
                        single.DepositPaid,
                        single.StripeCheckoutUrl,
                        single.ArtistNote,
                        icsUrl = $"/api/booking-ics?id={single.Id}"
                    },
                    stripeConfigured = IsStripeConfigured()
                });
                return singleOk;
            }

            if (!IsAuthorized(req))
            {
                var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauth.WriteAsJsonAsync(new { ok = false, error = "Invalid studio PIN." });
                return unauth;
            }

            var bookings = await store.ListAsync();
            var listOk = req.CreateResponse(HttpStatusCode.OK);
            await listOk.WriteAsJsonAsync(new
            {
                ok = true,
                stripeConfigured = IsStripeConfigured(),
                defaultDepositCents = GetDefaultDepositCents(),
                bookings
            });
            return listOk;
        }

        CreateBookingRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<CreateBookingRequest>(req.Body, JsonOpts);
        }
        catch
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { ok = false, error = "Invalid booking payload." });
            return bad;
        }

        if (payload is null
            || string.IsNullOrWhiteSpace(payload.Name)
            || string.IsNullOrWhiteSpace(payload.Phone)
            || string.IsNullOrWhiteSpace(payload.Email)
            || string.IsNullOrWhiteSpace(payload.Date)
            || string.IsNullOrWhiteSpace(payload.TimeSlot))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { ok = false, error = "Name, phone, email, date, and time slot are required." });
            return bad;
        }

        if (!DateOnly.TryParseExact(payload.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { ok = false, error = "Invalid date." });
            return bad;
        }

        var (isOpen, note, validSlots) = BookingStore.GetStudioSchedule(parsedDate);
        if (!isOpen || !validSlots.Contains(payload.TimeSlot, StringComparer.OrdinalIgnoreCase))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { ok = false, error = note });
            return bad;
        }

        var artist = string.IsNullOrWhiteSpace(payload.Artist) ? "First Available Artist" : payload.Artist.Trim();
        var existing = await store.ListAsync();
        var conflict = existing.Any(b =>
            string.Equals(b.Date, payload.Date, StringComparison.OrdinalIgnoreCase)
            && string.Equals(b.TimeSlot, payload.TimeSlot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(b.Artist, artist, StringComparison.OrdinalIgnoreCase)
            && (b.Status == "Confirmed" || b.Status == "ApprovedAwaitingDeposit"));

        if (conflict)
        {
            var conflictResp = req.CreateResponse(HttpStatusCode.Conflict);
            await conflictResp.WriteAsJsonAsync(new { ok = false, error = "That time slot was just reserved. Please select another slot." });
            return conflictResp;
        }

        var shortCode = $"ST-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        var record = new BookingRecord
        {
            Id = shortCode,
            Artist = artist,
            Date = payload.Date,
            TimeSlot = payload.TimeSlot,
            ServiceType = string.IsNullOrWhiteSpace(payload.ServiceType) ? "Custom Tattoo" : payload.ServiceType.Trim(),
            Name = payload.Name.Trim(),
            Phone = payload.Phone.Trim(),
            Email = payload.Email.Trim(),
            Placement = payload.Placement?.Trim() ?? string.Empty,
            IdeaDescription = payload.IdeaDescription?.Trim() ?? string.Empty,
            Status = "PendingApproval",
            DepositAmountCents = GetDefaultDepositCents(),
            DepositPaid = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await store.SaveAsync(record);
        logger.LogInformation("Created booking request {Id} for {Artist} on {Date} at {Slot}.",
            record.Id, record.Artist, record.Date, record.TimeSlot);

        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(new
        {
            ok = true,
            booking = record,
            icsUrl = $"/api/booking-ics?id={record.Id}"
        });
        return ok;
    }

    [Function("BookingAction")]
    public async Task<HttpResponseData> BookingAction(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "booking-action")] HttpRequestData req)
    {
        if (!IsAuthorized(req))
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteAsJsonAsync(new { ok = false, error = "Invalid studio PIN." });
            return unauth;
        }

        BookingActionRequest? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<BookingActionRequest>(req.Body, JsonOpts);
        }
        catch
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { ok = false, error = "Invalid action payload." });
            return bad;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Id) || string.IsNullOrWhiteSpace(payload.Action))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { ok = false, error = "Booking id and action are required." });
            return bad;
        }

        var store = CreateStore();
        var booking = await store.GetAsync(payload.Id.Trim());
        if (booking is null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { ok = false, error = "Booking not found." });
            return notFound;
        }

        if (!string.IsNullOrWhiteSpace(payload.ArtistNote))
            booking.ArtistNote = payload.ArtistNote.Trim();

        switch (payload.Action.Trim().ToLowerInvariant())
        {
            case "approve":
                if (payload.DepositAmountCents is > 0)
                    booking.DepositAmountCents = payload.DepositAmountCents.Value;

                booking.Status = "ApprovedAwaitingDeposit";
                var stripeSecret = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");
                if (!string.IsNullOrWhiteSpace(stripeSecret))
                {
                    var baseUrl = $"{req.Url.Scheme}://{req.Url.Authority}";
                    var session = await CreateStripeCheckoutSessionAsync(stripeSecret, booking, baseUrl);
                    if (session is not null)
                    {
                        booking.StripeSessionId = session.Value.SessionId;
                        booking.StripeCheckoutUrl = session.Value.CheckoutUrl;
                    }
                }
                break;

            case "confirm-manual":
                booking.DepositPaid = true;
                booking.Status = "Confirmed";
                break;

            case "decline":
                booking.Status = "Declined";
                break;

            default:
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { ok = false, error = "Unsupported action." });
                return bad;
        }

        await store.SaveAsync(booking);
        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(new { ok = true, stripeConfigured = IsStripeConfigured(), booking });
        return ok;
    }

    [Function("StripeWebhook")]
    public async Task<HttpResponseData> StripeWebhook(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "stripe-webhook")] HttpRequestData req)
    {
        using var reader = new StreamReader(req.Body, Encoding.UTF8);
        var rawBody = await reader.ReadToEndAsync();

        var webhookSecret = Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET");
        if (!string.IsNullOrWhiteSpace(webhookSecret))
        {
            if (!req.Headers.TryGetValues("Stripe-Signature", out var sigHeaders) ||
                !VerifyStripeSignature(rawBody, sigHeaders.FirstOrDefault(), webhookSecret))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { ok = false, error = "Invalid Stripe signature." });
                return bad;
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            var eventType = root.GetProperty("type").GetString();

            if (eventType == "checkout.session.completed")
            {
                var session = root.GetProperty("data").GetProperty("object");
                if (session.TryGetProperty("metadata", out var metadata) &&
                    metadata.TryGetProperty("bookingId", out var bookingIdProp))
                {
                    var bookingId = bookingIdProp.GetString();
                    if (!string.IsNullOrWhiteSpace(bookingId))
                    {
                        var store = CreateStore();
                        var booking = await store.GetAsync(bookingId);
                        if (booking is not null)
                        {
                            booking.DepositPaid = true;
                            booking.Status = "Confirmed";
                            await store.SaveAsync(booking);
                            logger.LogInformation("Stripe deposit confirmed for booking {Id}.", bookingId);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to process Stripe webhook.");
        }

        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(new { received = true });
        return ok;
    }

    [Function("DownloadBookingIcs")]
    public async Task<HttpResponseData> DownloadIcs(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "booking-ics")] HttpRequestData req)
    {
        var query = ParseQuery(req.Url.Query);
        if (!query.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        var store = CreateStore();
        var booking = await store.GetAsync(id.Trim());
        if (booking is null)
            return req.CreateResponse(HttpStatusCode.NotFound);

        DateTime.TryParseExact($"{booking.Date} {booking.TimeSlot}", "yyyy-MM-dd h:mm tt",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var startLocal);
        if (startLocal == default)
            startLocal = DateTime.UtcNow.AddDays(1);

        var endLocal = startLocal.AddMinutes(90);
        var ics = new StringBuilder()
            .AppendLine("BEGIN:VCALENDAR")
            .AppendLine("VERSION:2.0")
            .AppendLine("PRODID:-//Spectrum Tattoo//Booking//EN")
            .AppendLine("BEGIN:VEVENT")
            .AppendLine($"UID:{booking.Id}@spectrumtattoo")
            .AppendLine($"DTSTAMP:{DateTime.UtcNow:yyyyMMddTHHmmssZ}")
            .AppendLine($"DTSTART:{startLocal:yyyyMMddTHHmmss}")
            .AppendLine($"DTEND:{endLocal:yyyyMMddTHHmmss}")
            .AppendLine($"SUMMARY:Spectrum Tattoo - {booking.ServiceType} ({booking.Artist})")
            .AppendLine($"DESCRIPTION:Booking {booking.Id} | Status: {booking.Status} | (423) 299-9111")
            .AppendLine("LOCATION:6165 US-411\\, Benton\\, TN 37307")
            .AppendLine("END:VEVENT")
            .AppendLine("END:VCALENDAR")
            .ToString();

        var resp = req.CreateResponse(HttpStatusCode.OK);
        resp.Headers.Add("Content-Type", "text/calendar; charset=utf-8");
        resp.Headers.Add("Content-Disposition", $"attachment; filename=\"spectrum-tattoo-{booking.Id}.ics\"");
        await resp.WriteStringAsync(ics);
        return resp;
    }

    private async Task<(string SessionId, string CheckoutUrl)?> CreateStripeCheckoutSessionAsync(
        string stripeSecret,
        BookingRecord booking,
        string baseUrl)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", stripeSecret);

            var form = new Dictionary<string, string>
            {
                ["mode"] = "payment",
                ["success_url"] = $"{baseUrl}/book?bookingId={booking.Id}&payment=success",
                ["cancel_url"] = $"{baseUrl}/book?bookingId={booking.Id}&payment=cancelled",
                ["customer_email"] = booking.Email,
                ["line_items[0][quantity]"] = "1",
                ["line_items[0][price_data][currency]"] = "usd",
                ["line_items[0][price_data][unit_amount]"] = booking.DepositAmountCents.ToString(CultureInfo.InvariantCulture),
                ["line_items[0][price_data][product_data][name]"] =
                    $"Spectrum Tattoo Non-Refundable Deposit ({booking.Artist} · {booking.Date} {booking.TimeSlot})",
                ["metadata[bookingId]"] = booking.Id,
                ["metadata[artist]"] = booking.Artist
            };

            using var content = new FormUrlEncodedContent(form);
            var response = await client.PostAsync("https://api.stripe.com/v1/checkout/sessions", content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Stripe Checkout creation failed ({Status}): {Body}", response.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var sessionId = doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
            var url = doc.RootElement.GetProperty("url").GetString() ?? string.Empty;
            return (sessionId, url);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exception creating Stripe Checkout session.");
            return null;
        }
    }

    private static bool VerifyStripeSignature(string payload, string? sigHeader, string secret)
    {
        if (string.IsNullOrWhiteSpace(sigHeader)) return false;

        string? timestamp = null;
        string? signature = null;
        foreach (var part in sigHeader.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Trim() == "t") timestamp = kv[1].Trim();
            else if (kv[0].Trim() == "v1") signature = kv[1].Trim();
        }

        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature)) return false;

        var signedPayload = $"{timestamp}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
        var computed = Convert.ToHexString(hash).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(signature.ToLowerInvariant()));
    }
}
