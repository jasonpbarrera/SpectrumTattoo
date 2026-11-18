using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SpectrumTattoo.Client.Services;

public sealed class LocalBookingRecord
{
    public string Id { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string TimeSlot { get; set; } = string.Empty;
    public string ServiceType { get; set; } = "Custom Tattoo";
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Placement { get; set; } = string.Empty;
    public string IdeaDescription { get; set; } = string.Empty;
    public string Status { get; set; } = "PendingApproval"; // PendingApproval | ApprovedAwaitingDeposit | Confirmed | Declined
    public int DepositAmountCents { get; set; } = 5000;
    public bool DepositPaid { get; set; }
    public string? ArtistNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record SlotAvailability(string Time, bool Available);

public sealed class LocalBookingService(HttpClient http)
{
    private const string ApiBase = "https://spectrum-tattoo-bookings.jbdigitalservices.workers.dev/bookings";

    private sealed record AvailabilityApiResponse(bool Ok, bool IsOpen, string? Note, List<SlotAvailability>? Slots);
    private sealed record SingleBookingApiResponse(bool Ok, string? Error, LocalBookingRecord? Booking);
    private sealed record ListBookingsApiResponse(bool Ok, string? Error, List<LocalBookingRecord>? Bookings);

    public async Task<(bool IsOpen, string Note, List<SlotAvailability> Slots, bool Reachable)> GetAvailabilityAsync(string artist, string dateStr)
    {
        try
        {
            var url = $"{ApiBase}?op=availability&artist={Uri.EscapeDataString(artist)}&date={Uri.EscapeDataString(dateStr)}";
            var resp = await http.GetFromJsonAsync<AvailabilityApiResponse>(url);
            if (resp is not null && resp.Ok)
            {
                return (resp.IsOpen, resp.Note ?? string.Empty, resp.Slots ?? [], true);
            }
        }
        catch
        {
            // Fall through to unreachable state
        }

        return (false, "Shared booking calendar is temporarily unreachable. Please call (423) 299-9111 to book.", [], false);
    }

    public async Task<LocalBookingRecord?> GetAsync(string id)
    {
        var resp = await http.GetAsync($"{ApiBase}?id={Uri.EscapeDataString(id.Trim())}");
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadFromJsonAsync<SingleBookingApiResponse>();
        return body?.Ok == true ? body.Booking : null;
    }

    public async Task<LocalBookingRecord> CreateAsync(LocalBookingRecord record)
    {
        var resp = await http.PostAsJsonAsync(ApiBase, new
        {
            artist = record.Artist,
            date = record.Date,
            timeSlot = record.TimeSlot,
            serviceType = record.ServiceType,
            name = record.Name,
            phone = record.Phone,
            email = record.Email,
            placement = record.Placement,
            ideaDescription = record.IdeaDescription
        });

        var body = await resp.Content.ReadFromJsonAsync<SingleBookingApiResponse>();
        if (resp.IsSuccessStatusCode && body?.Ok == true && body.Booking is not null)
        {
            return body.Booking;
        }

        throw new InvalidOperationException(body?.Error ?? "Could not submit booking to the studio queue. Please call (423) 299-9111.");
    }

    public async Task<List<LocalBookingRecord>> ListAsync(string pin)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase);
        req.Headers.Add("X-Studio-Pin", pin.Trim());
        var resp = await http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Invalid studio PIN.");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ListBookingsApiResponse>();
        return body?.Bookings ?? [];
    }

    public async Task SeedSampleAsync(string pin)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase)
        {
            Content = JsonContent.Create(new { op = "seed" })
        };
        req.Headers.Add("X-Studio-Pin", pin.Trim());
        var resp = await http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Invalid studio PIN.");
        resp.EnsureSuccessStatusCode();
    }

    public async Task UpdateStatusAsync(string id, string op, string? pin = null, string? artistNote = null, int? depositCents = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase)
        {
            Content = JsonContent.Create(new
            {
                id,
                op,
                artistNote,
                depositAmountCents = depositCents
            })
        };
        if (!string.IsNullOrWhiteSpace(pin))
            req.Headers.Add("X-Studio-Pin", pin.Trim());

        var resp = await http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Invalid studio PIN.");
        resp.EnsureSuccessStatusCode();
    }

    public static string BuildIcsDataUri(LocalBookingRecord b)
    {
        DateTime.TryParseExact($"{b.Date} {b.TimeSlot}", "yyyy-MM-dd h:mm tt",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var startLocal);
        if (startLocal == default) startLocal = DateTime.Now.AddDays(1);
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

        return "data:text/calendar;charset=utf-8," + Uri.EscapeDataString(ics);
    }
}
