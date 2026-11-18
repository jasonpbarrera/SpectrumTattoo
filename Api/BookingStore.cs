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

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty; // yyyy-MM-dd

    [JsonPropertyName("timeSlot")]
    public string TimeSlot { get; set; } = string.Empty; // e.g. "10:00 AM"

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
    public string Status { get; set; } = "PendingApproval"; // PendingApproval | ApprovedAwaitingDeposit | Confirmed | Declined

    [JsonPropertyName("depositAmountCents")]
    public int DepositAmountCents { get; set; } = 5000;

    [JsonPropertyName("depositPaid")]
    public bool DepositPaid { get; set; }

    [JsonPropertyName("artistNote")]
    public string? ArtistNote { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BookingStore
{
    public const string PartitionKey = "spectrum-tattoo";
    private readonly string _containerId =
        Environment.GetEnvironmentVariable("COSMOS_BOOKINGS_CONTAINER") is { Length: > 0 } c
            ? c.Trim()
            : "spectrum-tattoo-bookings";

    private static readonly ConcurrentDictionary<string, BookingRecord> MemoryFallback = new(StringComparer.OrdinalIgnoreCase);

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

    public async Task<List<BookingRecord>> ListAsync()
    {
        if (!_useCosmos)
            return MemoryFallback.Values.OrderByDescending(b => b.CreatedAt).ToList();

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

        string bodyJson = JsonSerializer.Serialize(new
        {
            query = $"SELECT * FROM c WHERE c.businessId = '{PartitionKey}'",
            parameters = Array.Empty<object>()
        });
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
        {
            var list = JsonSerializer.Deserialize<List<BookingRecord>>(docsElem.GetRawText(), JsonOpts) ?? [];
            return list.OrderByDescending(b => b.CreatedAt).ToList();
        }

        return [];
    }

    public async Task<BookingRecord?> GetAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var cleanId = id.Trim();

        if (!_useCosmos)
            return MemoryFallback.TryGetValue(cleanId, out var mem) ? mem : null;

        var all = await ListAsync();
        return all.FirstOrDefault(b => string.Equals(b.Id, cleanId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<(bool Ok, string Error)> UpsertAsync(BookingRecord record)
    {
        record.BusinessId = PartitionKey;
        record.UpdatedAt = DateTimeOffset.UtcNow;

        if (!_useCosmos)
        {
            MemoryFallback[record.Id] = record;
            return (true, string.Empty);
        }

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
        request.Content = new StringContent(JsonSerializer.Serialize(record, JsonOpts), Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        if (response.IsSuccessStatusCode)
            return (true, string.Empty);

        string err = await response.Content.ReadAsStringAsync();
        _logger.LogError("Cosmos upsert failed with {Status}: {Error}", response.StatusCode, err);
        return (false, $"Database write failed ({(int)response.StatusCode}).");
    }

    public async Task<(bool Ok, string Error)> DeleteAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return (false, "Booking id is required.");
        var cleanId = id.Trim();

        if (!_useCosmos)
        {
            MemoryFallback.TryRemove(cleanId, out _);
            return (true, string.Empty);
        }

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
