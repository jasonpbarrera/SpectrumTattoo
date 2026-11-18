using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;

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

public sealed class LocalBookingService(IJSRuntime js)
{
    private const string StorageKey = "spectrum_tattoo_bookings_v1";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<List<LocalBookingRecord>> ListAsync()
    {
        try
        {
            var raw = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (string.IsNullOrWhiteSpace(raw)) return [];
            var items = JsonSerializer.Deserialize<List<LocalBookingRecord>>(raw, JsonOpts);
            return items?.OrderByDescending(b => b.CreatedAt).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task SaveAllAsync(List<LocalBookingRecord> items)
    {
        var json = JsonSerializer.Serialize(items, JsonOpts);
        await js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
    }

    public async Task<LocalBookingRecord?> GetAsync(string id)
    {
        var all = await ListAsync();
        return all.FirstOrDefault(b => string.Equals(b.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<LocalBookingRecord> CreateAsync(LocalBookingRecord record)
    {
        var all = await ListAsync();
        if (string.IsNullOrWhiteSpace(record.Id))
            record.Id = $"ST-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        record.CreatedAt = DateTimeOffset.UtcNow;
        all.Insert(0, record);
        await SaveAllAsync(all);
        return record;
    }

    public async Task UpdateStatusAsync(string id, string status, bool depositPaid, string? artistNote = null, int? depositCents = null)
    {
        var all = await ListAsync();
        var item = all.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;

        item.Status = status;
        item.DepositPaid = depositPaid;
        if (artistNote is not null) item.ArtistNote = artistNote;
        if (depositCents is > 0) item.DepositAmountCents = depositCents.Value;
        await SaveAllAsync(all);
    }

    public async Task SeedSampleAsync()
    {
        var all = await ListAsync();
        if (all.Count > 0) return;

        var nextOpen = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
        while (nextOpen.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Tuesday)
            nextOpen = nextOpen.AddDays(1);

        all.Add(new LocalBookingRecord
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
            DepositAmountCents = 5000,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await SaveAllAsync(all);
    }

    public async Task<(bool IsOpen, string Note, List<SlotAvailability> Slots)> GetAvailabilityAsync(string artist, string dateStr)
    {
        if (!DateOnly.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return (false, "Select a valid date.", []);

        if (date.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Tuesday)
            return (false, "Studio is closed Mondays & Tuesdays. Please select Wednesday–Sunday.", []);

        string[] times = date.DayOfWeek switch
        {
            DayOfWeek.Saturday => ["10:00 AM", "11:30 AM", "1:00 PM", "2:30 PM"],
            DayOfWeek.Sunday => ["10:00 AM", "11:30 AM", "1:00 PM"],
            _ => ["10:00 AM", "11:30 AM", "1:00 PM", "2:30 PM", "4:00 PM"]
        };

        var note = date.DayOfWeek == DayOfWeek.Saturday
            ? "Saturday Hours: 10:00 AM – 4:00 PM"
            : date.DayOfWeek == DayOfWeek.Sunday
                ? "Sunday Hours: 10:00 AM – 3:00 PM"
                : "Studio Hours: 10:00 AM – 6:00 PM";

        var all = await ListAsync();
        var taken = all
            .Where(b => string.Equals(b.Date, dateStr, StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(b.Artist, artist, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(artist, "First Available Artist", StringComparison.OrdinalIgnoreCase))
                        && b.Status is "Confirmed" or "ApprovedAwaitingDeposit")
            .Select(b => b.TimeSlot)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var slots = times.Select(t => new SlotAvailability(t, !taken.Contains(t))).ToList();
        return (true, note, slots);
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
