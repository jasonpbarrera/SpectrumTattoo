using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace SpectrumTattoo.Client.Services;

public sealed class LocalBookingRecord
{
    public string Id { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string TimeSlot { get; set; } = string.Empty;
    public string? EndTime { get; set; }
    public string ServiceType { get; set; } = "Custom Tattoo";
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Placement { get; set; } = string.Empty;
    public string IdeaDescription { get; set; } = string.Empty;
    public string Status { get; set; } = "PendingApproval";
    public int DepositAmountCents { get; set; } = 5000;
    public bool DepositPaid { get; set; }
    public string? ArtistNote { get; set; }
    public List<string> ReferenceImages { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TimeBlockRecord
{
    public string Id { get; set; } = string.Empty;
    public string Artist { get; set; } = "All Artists";
    public string Date { get; set; } = string.Empty;
    public bool AllDay { get; set; }
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public string? Reason { get; set; }
}

public sealed class DaySchedule
{
    public bool Enabled { get; set; }
    public string StartTime { get; set; } = "10:00 AM";
    public string EndTime { get; set; } = "6:00 PM";
}

public sealed class ArtistScheduleRecord
{
    public string Id { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public Dictionary<string, DaySchedule> Days { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ClientProfileRecord
{
    public string Id { get; set; } = string.Empty;
    public string PhoneKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PreferredArtist { get; set; }
    public string StudioNotes { get; set; } = string.Empty;
    public List<string> CompletedPhotos { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record ClientPrefillResult(bool Found, string? Name, string? Email, string? PreferredArtist);

public sealed record AdminDashboardData(
    List<LocalBookingRecord> Bookings,
    List<TimeBlockRecord> Blocks,
    List<ArtistScheduleRecord> Schedules,
    List<ClientProfileRecord> Clients);

public sealed record SlotAvailability(string Time, bool Available);

public sealed class LocalBookingService(HttpClient http)
{
    private const string ApiBase = "api/bookings";

    private sealed record AvailabilityApiResponse(bool Ok, bool IsOpen, string? Note, List<SlotAvailability>? Slots);
    private sealed record SingleBookingApiResponse(bool Ok, string? Error, LocalBookingRecord? Booking);
    private sealed record ListBookingsApiResponse(
        bool Ok,
        string? Error,
        List<LocalBookingRecord>? Bookings,
        List<TimeBlockRecord>? Blocks,
        List<ArtistScheduleRecord>? Schedules,
        List<ClientProfileRecord>? Clients);
    private sealed record PrefillApiResponse(bool Ok, bool Found, string? Name, string? Email, string? PreferredArtist);
    private sealed record BasicApiResponse(bool Ok, string? Error);

    public static string NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits.StartsWith('1'))
            digits = digits[1..];
        return digits;
    }

    public async Task<ClientPrefillResult> PrefillClientByPhoneAsync(string phone)
    {
        var digits = NormalizePhone(phone);
        if (digits.Length < 7) return new ClientPrefillResult(false, null, null, null);
        try
        {
            var resp = await http.GetFromJsonAsync<PrefillApiResponse>($"{ApiBase}?op=client-prefill&phone={Uri.EscapeDataString(digits)}");
            if (resp is not null && resp.Ok && resp.Found)
                return new ClientPrefillResult(true, resp.Name, resp.Email, resp.PreferredArtist);
        }
        catch
        {
            // Ignore prefill errors silently
        }
        return new ClientPrefillResult(false, null, null, null);
    }

    public async Task<(bool IsOpen, string Note, List<SlotAvailability> Slots, bool Reachable)> GetAvailabilityAsync(string artist, string dateStr)
    {
        try
        {
            var url = $"{ApiBase}?op=availability&artist={Uri.EscapeDataString(artist)}&date={Uri.EscapeDataString(dateStr)}";
            var resp = await http.GetFromJsonAsync<AvailabilityApiResponse>(url);
            if (resp is not null && resp.Ok)
                return (resp.IsOpen, resp.Note ?? string.Empty, resp.Slots ?? [], true);
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

    public async Task<List<LocalBookingRecord>> LookupAsync(string phoneOrCode)
    {
        var resp = await http.GetAsync($"{ApiBase}?lookup={Uri.EscapeDataString(phoneOrCode.Trim())}");
        if (!resp.IsSuccessStatusCode) return [];
        var body = await resp.Content.ReadFromJsonAsync<ListBookingsApiResponse>();
        return body?.Ok == true ? (body.Bookings ?? []) : [];
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
            ideaDescription = record.IdeaDescription,
            referenceImages = record.ReferenceImages
        });

        var body = await resp.Content.ReadFromJsonAsync<SingleBookingApiResponse>();
        if (resp.IsSuccessStatusCode && body?.Ok == true && body.Booking is not null)
            return body.Booking;

        throw new InvalidOperationException(body?.Error ?? "Could not submit booking to the studio queue. Please call (423) 299-9111.");
    }

    public async Task<AdminDashboardData> LoadAdminDataAsync(string pin)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase);
        req.Headers.Add("X-Studio-Pin", pin.Trim());
        var resp = await http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Invalid studio PIN.");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ListBookingsApiResponse>();
        return new AdminDashboardData(
            body?.Bookings ?? [],
            body?.Blocks ?? [],
            body?.Schedules ?? [],
            body?.Clients ?? []);
    }

    public async Task SaveClientProfileAsync(string pin, string phone, string name, string email, string studioNotes, List<string>? completedPhotos = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase)
        {
            Content = JsonContent.Create(new
            {
                op = "save-client-profile",
                phone,
                name,
                email,
                studioNotes,
                completedPhotos
            })
        };
        req.Headers.Add("X-Studio-Pin", pin.Trim());
        var resp = await http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Invalid studio PIN.");

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadFromJsonAsync<BasicApiResponse>();
            throw new InvalidOperationException(errBody?.Error ?? "Could not save client profile.");
        }
    }

    public async Task UpdateStatusAsync(
        string id,
        string op,
        string? pin = null,
        string? artistNote = null,
        int? depositCents = null,
        string? date = null,
        string? timeSlot = null,
        string? endTime = null,
        string? artist = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase)
        {
            Content = JsonContent.Create(new
            {
                id,
                op,
                artistNote,
                depositAmountCents = depositCents,
                date,
                timeSlot,
                endTime,
                artist
            })
        };
        if (!string.IsNullOrWhiteSpace(pin))
            req.Headers.Add("X-Studio-Pin", pin.Trim());

        var resp = await http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Invalid studio PIN.");

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadFromJsonAsync<BasicApiResponse>();
            throw new InvalidOperationException(errBody?.Error ?? "Action failed.");
        }
    }

    public async Task AddBlockAsync(string pin, string artist, string date, bool allDay, string? startTime, string? endTime, string? reason)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase)
        {
            Content = JsonContent.Create(new
            {
                op = "add-block",
                artist,
                date,
                allDay,
                startTime,
                endTime,
                reason
            })
        };
        req.Headers.Add("X-Studio-Pin", pin.Trim());
        var resp = await http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Invalid studio PIN.");

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadFromJsonAsync<BasicApiResponse>();
            throw new InvalidOperationException(errBody?.Error ?? "Could not add time block.");
        }
    }

    public async Task DeleteBlockAsync(string pin, string blockId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase)
        {
            Content = JsonContent.Create(new { op = "delete-block", id = blockId })
        };
        req.Headers.Add("X-Studio-Pin", pin.Trim());
        var resp = await http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Invalid studio PIN.");
        resp.EnsureSuccessStatusCode();
    }

    public async Task SaveScheduleAsync(string pin, string artist, Dictionary<string, DaySchedule> days)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase)
        {
            Content = JsonContent.Create(new { op = "save-schedule", artist, days })
        };
        req.Headers.Add("X-Studio-Pin", pin.Trim());
        var resp = await http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Invalid studio PIN.");

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadFromJsonAsync<BasicApiResponse>();
            throw new InvalidOperationException(errBody?.Error ?? "Could not save schedule.");
        }
    }

    public static string BuildIcsDataUri(LocalBookingRecord b)
    {
        DateTime.TryParseExact($"{b.Date} {b.TimeSlot}", "yyyy-MM-dd h:mm tt",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var startLocal);
        if (startLocal == default) startLocal = DateTime.Now.AddDays(1);

        var endLocal = startLocal.AddMinutes(90);
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

        return "data:text/calendar;charset=utf-8," + Uri.EscapeDataString(ics);
    }
}
