using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SpectrumTattoo.Api;

public sealed class BookingRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("businessId")]
    public string BusinessId { get; set; } = BookingStore.PartitionKey;

    [JsonPropertyName("docType")]
    public string DocType { get; set; } = "booking";

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty; // yyyy-MM-dd

    [JsonPropertyName("timeSlot")]
    public string TimeSlot { get; set; } = string.Empty; // Start time e.g. "10:00 AM"

    [JsonPropertyName("endTime")]
    public string? EndTime { get; set; } // Optional custom end time e.g. "1:00 PM"

    [JsonPropertyName("serviceType")]
    public string ServiceType { get; set; } = "Custom Tattoo";

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("placement")]
    public string Placement { get; set; } = string.Empty;

    [JsonPropertyName("ideaDescription")]
    public string IdeaDescription { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "PendingApproval"; // PendingApproval | ApprovedAwaitingDeposit | Confirmed | Completed | Cancelled | Declined

    [JsonPropertyName("depositAmountCents")]
    public int DepositAmountCents { get; set; } = 5000;

    [JsonPropertyName("depositPaid")]
    public bool DepositPaid { get; set; }

    [JsonPropertyName("artistNote")]
    public string? ArtistNote { get; set; }

    [JsonPropertyName("referenceImages")]
    public List<string> ReferenceImages { get; set; } = [];

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TimeBlockRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("businessId")]
    public string BusinessId { get; set; } = BookingStore.PartitionKey;

    [JsonPropertyName("docType")]
    public string DocType { get; set; } = "time-block";

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = "All Artists"; // Artist name or "All Artists"

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty; // yyyy-MM-dd

    [JsonPropertyName("allDay")]
    public bool AllDay { get; set; }

    [JsonPropertyName("startTime")]
    public string? StartTime { get; set; } // e.g. "11:00 AM"

    [JsonPropertyName("endTime")]
    public string? EndTime { get; set; } // e.g. "2:00 PM"

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DaySchedule
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = "10:00 AM";

    [JsonPropertyName("endTime")]
    public string EndTime { get; set; } = "6:00 PM";
}

public sealed class ArtistScheduleRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("businessId")]
    public string BusinessId { get; set; } = BookingStore.PartitionKey;

    [JsonPropertyName("docType")]
    public string DocType { get; set; } = "artist-schedule";

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = string.Empty;

    // Keys: "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"
    [JsonPropertyName("days")]
    public Dictionary<string, DaySchedule> Days { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ClientProfileRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("businessId")]
    public string BusinessId { get; set; } = BookingStore.PartitionKey;

    [JsonPropertyName("docType")]
    public string DocType { get; set; } = "client-profile";

    [JsonPropertyName("phoneKey")]
    public string PhoneKey { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("preferredArtist")]
    public string? PreferredArtist { get; set; }

    [JsonPropertyName("studioNotes")]
    public string StudioNotes { get; set; } = string.Empty;

    [JsonPropertyName("completedPhotos")]
    public List<string> CompletedPhotos { get; set; } = [];

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BookingStore
{
    public const string PartitionKey = "spectrum-tattoo";

    public static readonly string[] ResidentArtists =
    [
        "Dawn Yermasek",
        "Tae",
        "Kota",
        "Kindra",
        "LaCinda",
        "Heavenly Huckaby"
    ];

    private readonly string _containerId =
        Environment.GetEnvironmentVariable("COSMOS_BOOKINGS_CONTAINER") is { Length: > 0 } c
            ? c.Trim()
            : "spectrum-tattoo-bookings";

    private static readonly ConcurrentDictionary<string, BookingRecord> MemoryBookings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, TimeBlockRecord> MemoryBlocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ArtistScheduleRecord> MemorySchedules = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ClientProfileRecord> MemoryClients = new(StringComparer.OrdinalIgnoreCase);

    public static string NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits.StartsWith('1'))
            digits = digits[1..];
        return digits;
    }

    public static string MakeClientProfileId(string phoneKey) => $"client-{phoneKey}";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _endpoint;
    private readonly string _key;
    private readonly string _databaseId;
    private readonly bool _useCosmos;

    public BookingStore(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _http = httpClientFactory.CreateClient();
        _logger = logger;
        _useCosmos = TryGetCosmosCredentials(out _endpoint, out _key, out _databaseId);
    }

    public bool IsCosmosConfigured => _useCosmos;

    private static bool TryGetCosmosCredentials(out string endpoint, out string key, out string databaseId)
    {
        endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")?.TrimEnd('/') ?? "";
        key = Environment.GetEnvironmentVariable("COSMOS_KEY") ?? "";
        databaseId = Environment.GetEnvironmentVariable("COSMOS_DATABASE_NAME") ?? "SmallBusinessPlatform";

        string connString = Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING") ?? "";
        if (!string.IsNullOrWhiteSpace(connString))
        {
            foreach (var part in connString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int idx = part.IndexOf('=');
                if (idx <= 0) continue;
                string k = part[..idx].Trim();
                string v = part[(idx + 1)..].Trim();
                if (k.Equals("AccountEndpoint", StringComparison.OrdinalIgnoreCase))
                    endpoint = v.TrimEnd('/');
                else if (k.Equals("AccountKey", StringComparison.OrdinalIgnoreCase))
                    key = v;
            }
        }

        return !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(key);
    }

    public static (bool IsOpen, string Note, string[] Slots) GetStudioSchedule(DateOnly date)
    {
        if (date.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Tuesday)
            return (false, "Studio is closed Mondays & Tuesdays. Please select Wednesday–Sunday.", []);

        return date.DayOfWeek switch
        {
            DayOfWeek.Saturday => (true, "Saturday Hours: 10:00 AM – 4:00 PM", ["10:00 AM", "11:30 AM", "1:00 PM", "2:30 PM"]),
            DayOfWeek.Sunday => (true, "Sunday Hours: 10:00 AM – 3:00 PM", ["10:00 AM", "11:30 AM", "1:00 PM"]),
            _ => (true, "Studio Hours: 10:00 AM – 6:00 PM", ["10:00 AM", "11:30 AM", "1:00 PM", "2:30 PM", "4:00 PM"])
        };
    }

    public static DaySchedule GetDefaultDaySchedule(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Monday or DayOfWeek.Tuesday => new DaySchedule { Enabled = false, StartTime = "10:00 AM", EndTime = "6:00 PM" },
        DayOfWeek.Saturday => new DaySchedule { Enabled = true, StartTime = "10:00 AM", EndTime = "4:00 PM" },
        DayOfWeek.Sunday => new DaySchedule { Enabled = true, StartTime = "10:00 AM", EndTime = "3:00 PM" },
        _ => new DaySchedule { Enabled = true, StartTime = "10:00 AM", EndTime = "6:00 PM" }
    };

    public static ArtistScheduleRecord CreateDefaultSchedule(string artist)
    {
        var rec = new ArtistScheduleRecord
        {
            Id = MakeScheduleId(artist),
            Artist = artist
        };
        foreach (DayOfWeek dow in Enum.GetValues<DayOfWeek>())
            rec.Days[dow.ToString()] = GetDefaultDaySchedule(dow);
        return rec;
    }

    public static string MakeScheduleId(string artist)
    {
        var slug = new string(artist.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return $"sched-{slug}";
    }

    public static bool TryParseMinutes(string? timeStr, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(timeStr)) return false;
        string[] formats = ["h:mm tt", "hh:mm tt", "H:mm", "HH:mm"];
        if (DateTime.TryParseExact(timeStr.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            minutes = dt.Hour * 60 + dt.Minute;
            return true;
        }
        return false;
    }

    public static string FormatMinutes(int totalMinutes)
    {
        totalMinutes = Math.Clamp(totalMinutes, 0, 23 * 60 + 59);
        var dt = DateTime.Today.AddMinutes(totalMinutes);
        return dt.ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    public static string ComputeDefaultEndTime(string startTime, int durationMinutes = 90)
    {
        return TryParseMinutes(startTime, out var start)
            ? FormatMinutes(start + durationMinutes)
            : "11:30 AM";
    }

    public static (int Start, int End) GetBookingRange(BookingRecord b)
    {
        int start = TryParseMinutes(b.TimeSlot, out var s) ? s : 600;
        int end = TryParseMinutes(b.EndTime, out var e) && e > start ? e : start + 90;
        return (start, end);
    }

    public static (bool IsOpen, string Note, List<(string Time, bool Available)> Slots) ComputeAvailability(
        string requestedArtist,
        DateOnly date,
        List<BookingRecord> bookings,
        List<TimeBlockRecord> blocks,
        List<ArtistScheduleRecord> schedules)
    {
        var dateStr = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dowName = date.DayOfWeek.ToString();

        // Check studio-wide all-day block
        var studioAllDay = blocks.FirstOrDefault(bl =>
            string.Equals(bl.Date, dateStr, StringComparison.OrdinalIgnoreCase)
            && string.Equals(bl.Artist, "All Artists", StringComparison.OrdinalIgnoreCase)
            && bl.AllDay);
        if (studioAllDay is not null)
        {
            var reason = string.IsNullOrWhiteSpace(studioAllDay.Reason) ? "Studio is blocked off on this date." : $"Studio closed: {studioAllDay.Reason}";
            return (false, reason, []);
        }

        bool isFirstAvailable = string.Equals(requestedArtist, "First Available Artist", StringComparison.OrdinalIgnoreCase);

        if (!isFirstAvailable)
        {
            var sched = schedules.FirstOrDefault(s => string.Equals(s.Artist, requestedArtist, StringComparison.OrdinalIgnoreCase));
            var day = sched is not null && sched.Days.TryGetValue(dowName, out var d) ? d : GetDefaultDaySchedule(date.DayOfWeek);

            if (!day.Enabled)
                return (false, $"{requestedArtist} is not scheduled on {dowName}s. Please choose another day or artist.", []);

            var artistAllDay = blocks.FirstOrDefault(bl =>
                string.Equals(bl.Date, dateStr, StringComparison.OrdinalIgnoreCase)
                && string.Equals(bl.Artist, requestedArtist, StringComparison.OrdinalIgnoreCase)
                && bl.AllDay);
            if (artistAllDay is not null)
            {
                var reason = string.IsNullOrWhiteSpace(artistAllDay.Reason) ? $"{requestedArtist} is unavailable on this date." : $"{requestedArtist} unavailable: {artistAllDay.Reason}";
                return (false, reason, []);
            }

            if (!TryParseMinutes(day.StartTime, out var workStart) || !TryParseMinutes(day.EndTime, out var workEnd) || workEnd <= workStart)
                return (false, $"{requestedArtist} has no open hours on {dowName}.", []);

            var slotList = BuildSlotsInWindow(workStart, workEnd);
            var result = slotList.Select(slot =>
            {
                bool avail = IsArtistFreeForSlot(requestedArtist, dateStr, slot.Start, slot.End, bookings, blocks);
                return (slot.Label, avail);
            }).ToList();

            var note = $"{requestedArtist} Hours: {FormatMinutes(workStart)} – {FormatMinutes(workEnd)}";
            return (result.Count > 0, note, result);
        }
        else
        {
            // First Available Artist: union of working hours across all resident artists; slot is available if >= 1 artist is free
            int minStart = int.MaxValue;
            int maxEnd = int.MinValue;
            var activeArtists = new List<(string Name, int Start, int End)>();

            foreach (var artist in ResidentArtists)
            {
                var sched = schedules.FirstOrDefault(s => string.Equals(s.Artist, artist, StringComparison.OrdinalIgnoreCase));
                var day = sched is not null && sched.Days.TryGetValue(dowName, out var d) ? d : GetDefaultDaySchedule(date.DayOfWeek);
                if (!day.Enabled) continue;

                bool artistAllDay = blocks.Any(bl =>
                    string.Equals(bl.Date, dateStr, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(bl.Artist, artist, StringComparison.OrdinalIgnoreCase)
                    && bl.AllDay);
                if (artistAllDay) continue;

                if (TryParseMinutes(day.StartTime, out var ws) && TryParseMinutes(day.EndTime, out var we) && we > ws)
                {
                    activeArtists.Add((artist, ws, we));
                    if (ws < minStart) minStart = ws;
                    if (we > maxEnd) maxEnd = we;
                }
            }

            if (activeArtists.Count == 0)
                return (false, "Studio is closed on this date. Please select another day.", []);

            var slotList = BuildSlotsInWindow(minStart, maxEnd);
            var result = slotList.Select(slot =>
            {
                bool anyFree = activeArtists.Any(a =>
                    slot.Start >= a.Start
                    && slot.End <= a.End
                    && IsArtistFreeForSlot(a.Name, dateStr, slot.Start, slot.End, bookings, blocks));
                return (slot.Label, anyFree);
            }).ToList();

            var note = $"Studio Hours: {FormatMinutes(minStart)} – {FormatMinutes(maxEnd)}";
            return (result.Count > 0, note, result);
        }
    }

    private static List<(string Label, int Start, int End)> BuildSlotsInWindow(int workStart, int workEnd)
    {
        var slots = new List<(string, int, int)>();
        const int slotLength = 90;
        for (int cursor = workStart; cursor + slotLength <= workEnd; cursor += slotLength)
        {
            slots.Add((FormatMinutes(cursor), cursor, cursor + slotLength));
        }
        return slots;
    }

    private static bool IsArtistFreeForSlot(
        string artist,
        string dateStr,
        int slotStart,
        int slotEnd,
        List<BookingRecord> bookings,
        List<TimeBlockRecord> blocks)
    {
        // Check time blocks for this artist or All Artists
        foreach (var bl in blocks)
        {
            if (!string.Equals(bl.Date, dateStr, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(bl.Artist, artist, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(bl.Artist, "All Artists", StringComparison.OrdinalIgnoreCase))
                continue;

            if (bl.AllDay) return false;
            if (TryParseMinutes(bl.StartTime, out var bs) && TryParseMinutes(bl.EndTime, out var be) && be > bs)
            {
                if (slotStart < be && slotEnd > bs)
                    return false;
            }
        }

        // Check confirmed / approved bookings for this artist
        foreach (var b in bookings)
        {
            if (!string.Equals(b.Date, dateStr, StringComparison.OrdinalIgnoreCase)) continue;
            if (b.Status is not "Confirmed" and not "ApprovedAwaitingDeposit") continue;
            if (!string.Equals(b.Artist, artist, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(b.Artist, "First Available Artist", StringComparison.OrdinalIgnoreCase))
                continue;

            var (bStart, bEnd) = GetBookingRange(b);
            if (slotStart < bEnd && slotEnd > bStart)
                return false;
        }

        return true;
    }

    private async Task<List<T>> QueryAsync<T>(string sql)
    {
        string resourceLink = $"dbs/{_databaseId}/colls/{_containerId}";
        string requestUri = $"{_endpoint}/{resourceLink}/docs";
        string utcDate = DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture);
        string authHeader = GenerateCosmosAuthToken("post", "docs", resourceLink, utcDate, _key);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Add("Authorization", authHeader);
        request.Headers.Add("x-ms-date", utcDate);
        request.Headers.Add("x-ms-version", "2018-12-31");
        request.Headers.Add("x-ms-documentdb-isquery", "True");
        request.Headers.Add("x-ms-documentdb-query-enablecrosspartition", "True");

        string bodyJson = JsonSerializer.Serialize(new { query = sql, parameters = Array.Empty<object>() });
        request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/query+json");
        request.Content.Headers.ContentType!.CharSet = null;

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            string err = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Cosmos query returned {Status}: {Error}", response.StatusCode, err);
            return [];
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        if (doc.RootElement.TryGetProperty("Documents", out var docsElem))
            return JsonSerializer.Deserialize<List<T>>(docsElem.GetRawText(), JsonOpts) ?? [];

        return [];
    }

    private async Task<(bool Ok, string Error)> UpsertDocAsync<T>(T document)
    {
        string resourceLink = $"dbs/{_databaseId}/colls/{_containerId}";
        string requestUri = $"{_endpoint}/{resourceLink}/docs";
        string utcDate = DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture);
        string authHeader = GenerateCosmosAuthToken("post", "docs", resourceLink, utcDate, _key);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Add("Authorization", authHeader);
        request.Headers.Add("x-ms-date", utcDate);
        request.Headers.Add("x-ms-version", "2018-12-31");
        request.Headers.Add("x-ms-documentdb-is-upsert", "True");
        request.Headers.Add("x-ms-documentdb-partitionkey", JsonSerializer.Serialize(new[] { PartitionKey }));
        request.Content = new StringContent(JsonSerializer.Serialize(document, JsonOpts), Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        if (response.IsSuccessStatusCode)
            return (true, string.Empty);

        string err = await response.Content.ReadAsStringAsync();
        _logger.LogError("Cosmos upsert failed with {Status}: {Error}", response.StatusCode, err);
        return (false, $"Database write failed ({(int)response.StatusCode}).");
    }

    private async Task<(bool Ok, string Error)> DeleteDocAsync(string id)
    {
        var cleanId = id.Trim();
        string resourceLink = $"dbs/{_databaseId}/colls/{_containerId}/docs/{Uri.EscapeDataString(cleanId)}";
        string requestUri = $"{_endpoint}/{resourceLink}";
        string utcDate = DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture);
        string authHeader = GenerateCosmosAuthToken("delete", "docs", resourceLink, utcDate, _key);

        using var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
        request.Headers.Add("Authorization", authHeader);
        request.Headers.Add("x-ms-date", utcDate);
        request.Headers.Add("x-ms-version", "2018-12-31");
        request.Headers.Add("x-ms-documentdb-partitionkey", JsonSerializer.Serialize(new[] { PartitionKey }));

        var response = await _http.SendAsync(request);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
            return (true, string.Empty);

        string err = await response.Content.ReadAsStringAsync();
        _logger.LogError("Cosmos delete failed with {Status}: {Error}", response.StatusCode, err);
        return (false, $"Database delete failed ({(int)response.StatusCode}).");
    }

    public async Task<List<BookingRecord>> ListAsync()
    {
        if (!_useCosmos)
            return MemoryBookings.Values.OrderByDescending(b => b.CreatedAt).ToList();

        var list = await QueryAsync<BookingRecord>(
            $"SELECT * FROM c WHERE c.businessId = '{PartitionKey}' AND (NOT IS_DEFINED(c.docType) OR c.docType = 'booking')");
        return list.OrderByDescending(b => b.CreatedAt).ToList();
    }

    public async Task<BookingRecord?> GetAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var cleanId = id.Trim();

        if (!_useCosmos)
            return MemoryBookings.TryGetValue(cleanId, out var mem) ? mem : null;

        var all = await ListAsync();
        return all.FirstOrDefault(b => string.Equals(b.Id, cleanId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<(bool Ok, string Error)> UpsertAsync(BookingRecord record)
    {
        record.BusinessId = PartitionKey;
        record.DocType = "booking";
        if (string.IsNullOrWhiteSpace(record.EndTime))
            record.EndTime = ComputeDefaultEndTime(record.TimeSlot);
        record.UpdatedAt = DateTimeOffset.UtcNow;

        if (!_useCosmos)
        {
            MemoryBookings[record.Id] = record;
            return (true, string.Empty);
        }

        return await UpsertDocAsync(record);
    }

    public async Task<(bool Ok, string Error)> DeleteAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return (false, "Booking id is required.");
        if (!_useCosmos)
        {
            MemoryBookings.TryRemove(id.Trim(), out _);
            return (true, string.Empty);
        }
        return await DeleteDocAsync(id);
    }

    public async Task<List<TimeBlockRecord>> ListBlocksAsync()
    {
        if (!_useCosmos)
            return MemoryBlocks.Values.OrderBy(b => b.Date).ThenBy(b => b.StartTime).ToList();

        var list = await QueryAsync<TimeBlockRecord>(
            $"SELECT * FROM c WHERE c.businessId = '{PartitionKey}' AND c.docType = 'time-block'");
        return list.OrderBy(b => b.Date).ThenBy(b => b.StartTime).ToList();
    }

    public async Task<(bool Ok, string Error)> UpsertBlockAsync(TimeBlockRecord block)
    {
        block.BusinessId = PartitionKey;
        block.DocType = "time-block";
        if (string.IsNullOrWhiteSpace(block.Id))
            block.Id = $"BLK-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";

        if (!_useCosmos)
        {
            MemoryBlocks[block.Id] = block;
            return (true, string.Empty);
        }

        return await UpsertDocAsync(block);
    }

    public async Task<(bool Ok, string Error)> DeleteBlockAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return (false, "Block id is required.");
        if (!_useCosmos)
        {
            MemoryBlocks.TryRemove(id.Trim(), out _);
            return (true, string.Empty);
        }
        return await DeleteDocAsync(id);
    }

    public async Task<List<ArtistScheduleRecord>> ListSchedulesAsync()
    {
        List<ArtistScheduleRecord> stored;
        if (!_useCosmos)
            stored = MemorySchedules.Values.ToList();
        else
            stored = await QueryAsync<ArtistScheduleRecord>(
                $"SELECT * FROM c WHERE c.businessId = '{PartitionKey}' AND c.docType = 'artist-schedule'");

        // Ensure every resident artist has a schedule entry returned
        var result = new List<ArtistScheduleRecord>();
        foreach (var artist in ResidentArtists)
        {
            var found = stored.FirstOrDefault(s => string.Equals(s.Artist, artist, StringComparison.OrdinalIgnoreCase));
            result.Add(found ?? CreateDefaultSchedule(artist));
        }
        return result;
    }

    public async Task<(bool Ok, string Error)> UpsertScheduleAsync(ArtistScheduleRecord schedule)
    {
        schedule.BusinessId = PartitionKey;
        schedule.DocType = "artist-schedule";
        schedule.Id = MakeScheduleId(schedule.Artist);
        schedule.UpdatedAt = DateTimeOffset.UtcNow;

        if (!_useCosmos)
        {
            MemorySchedules[schedule.Id] = schedule;
            return (true, string.Empty);
        }

        return await UpsertDocAsync(schedule);
    }

    public async Task<List<ClientProfileRecord>> ListClientProfilesAsync(List<BookingRecord>? bookingsForBackfill = null)
    {
        List<ClientProfileRecord> stored;
        if (!_useCosmos)
            stored = MemoryClients.Values.ToList();
        else
            stored = await QueryAsync<ClientProfileRecord>(
                $"SELECT * FROM c WHERE c.businessId = '{PartitionKey}' AND c.docType = 'client-profile'");

        var byPhone = new Dictionary<string, ClientProfileRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in stored)
        {
            var key = NormalizePhone(string.IsNullOrWhiteSpace(p.PhoneKey) ? p.Phone : p.PhoneKey);
            if (key.Length >= 7)
            {
                p.PhoneKey = key;
                byPhone[key] = p;
            }
        }

        // Backfill profiles from existing bookings so repeat/prior clients appear automatically
        if (bookingsForBackfill is not null)
        {
            foreach (var b in bookingsForBackfill.OrderByDescending(x => x.CreatedAt))
            {
                var key = NormalizePhone(b.Phone);
                if (key.Length < 7) continue;
                if (!byPhone.ContainsKey(key))
                {
                    byPhone[key] = new ClientProfileRecord
                    {
                        Id = MakeClientProfileId(key),
                        PhoneKey = key,
                        Name = b.Name,
                        Phone = b.Phone,
                        Email = b.Email,
                        PreferredArtist = b.Artist,
                        StudioNotes = string.Empty,
                        UpdatedAt = b.CreatedAt
                    };
                }
            }
        }

        return byPhone.Values.OrderByDescending(p => p.UpdatedAt).ToList();
    }

    public async Task<ClientProfileRecord?> GetClientProfileByPhoneAsync(string phone, List<BookingRecord>? bookingsForBackfill = null)
    {
        var key = NormalizePhone(phone);
        if (key.Length < 7) return null;
        var all = await ListClientProfilesAsync(bookingsForBackfill);
        return all.FirstOrDefault(p => string.Equals(p.PhoneKey, key, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<(bool Ok, string Error)> UpsertClientProfileAsync(ClientProfileRecord profile)
    {
        var key = NormalizePhone(string.IsNullOrWhiteSpace(profile.PhoneKey) ? profile.Phone : profile.PhoneKey);
        if (key.Length < 7)
            return (false, "A valid phone number is required.");

        profile.PhoneKey = key;
        profile.Id = MakeClientProfileId(key);
        profile.BusinessId = PartitionKey;
        profile.DocType = "client-profile";
        profile.UpdatedAt = DateTimeOffset.UtcNow;

        if (!_useCosmos)
        {
            MemoryClients[profile.Id] = profile;
            return (true, string.Empty);
        }

        return await UpsertDocAsync(profile);
    }

    public async Task SyncClientProfileFromBookingAsync(BookingRecord booking)
    {
        var key = NormalizePhone(booking.Phone);
        if (key.Length < 7) return;

        var existing = await GetClientProfileByPhoneAsync(key);
        var profile = existing ?? new ClientProfileRecord
        {
            Id = MakeClientProfileId(key),
            PhoneKey = key,
            StudioNotes = string.Empty
        };

        if (!string.IsNullOrWhiteSpace(booking.Name)) profile.Name = booking.Name;
        if (!string.IsNullOrWhiteSpace(booking.Phone)) profile.Phone = booking.Phone;
        if (!string.IsNullOrWhiteSpace(booking.Email)) profile.Email = booking.Email;
        if (!string.IsNullOrWhiteSpace(booking.Artist) && !string.Equals(booking.Artist, "First Available Artist", StringComparison.OrdinalIgnoreCase))
            profile.PreferredArtist = booking.Artist;

        await UpsertClientProfileAsync(profile);
    }

    public async Task CleanupExpiredPhotosAsync(List<BookingRecord> bookings)
    {
        var studioToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

        foreach (var b in bookings)
        {
            if (b.ReferenceImages is null || b.ReferenceImages.Count == 0)
                continue;

            bool isPastAppointment = DateOnly.TryParseExact(b.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var apptDate)
                                     && apptDate < studioToday;
            bool isClosedStatus = b.Status is "Completed" or "Cancelled" or "Declined";

            if (isPastAppointment || isClosedStatus)
            {
                b.ReferenceImages.Clear();
                await UpsertAsync(b);
            }
        }
    }

    private static string GenerateCosmosAuthToken(
        string verb,
        string resourceType,
        string resourceId,
        string utcDate,
        string masterKeyBase64)
    {
        byte[] keyBytes = Convert.FromBase64String(masterKeyBase64);
        string payload = $"{verb.ToLowerInvariant()}\n{resourceType.ToLowerInvariant()}\n{resourceId}\n{utcDate.ToLowerInvariant()}\n\n";
        using var hmac = new HMACSHA256(keyBytes);
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        string signature = Convert.ToBase64String(hash);
        return Uri.EscapeDataString($"type=master&ver=1.0&sig={signature}");
    }
}
