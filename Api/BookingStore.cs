using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SpectrumTattoo.Api;

public sealed class BookingRecord
{
    public string Id { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty; // yyyy-MM-dd
    public string TimeSlot { get; set; } = string.Empty; // e.g. "10:00 AM"
    public string ServiceType { get; set; } = "Custom Tattoo";
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Placement { get; set; } = string.Empty;
    public string IdeaDescription { get; set; } = string.Empty;
    public string Status { get; set; } = "PendingApproval"; // PendingApproval | ApprovedAwaitingDeposit | Confirmed | Declined
    public int DepositAmountCents { get; set; } = 5000;
    public bool DepositPaid { get; set; }
    public string? StripeSessionId { get; set; }
    public string? StripeCheckoutUrl { get; set; }
    public string? ArtistNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BookingStore
{
    private readonly ConcurrentDictionary<string, BookingRecord> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly string? _accountName;
    private readonly byte[]? _accountKey;
    private readonly string? _tableEndpoint;
    private bool _tableEnsured;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public BookingStore(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        var conn = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
                   ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage");

        if (!string.IsNullOrWhiteSpace(conn) &&
            !conn.Contains("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase))
        {
            var parts = conn.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Where(kv => kv.Length == 2)
                .ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim(), StringComparer.OrdinalIgnoreCase);

            if (parts.TryGetValue("AccountName", out var acct) &&
                parts.TryGetValue("AccountKey", out var key))
            {
                _accountName = acct;
                _accountKey = Convert.FromBase64String(key);
                var suffix = parts.TryGetValue("EndpointSuffix", out var s) ? s : "core.windows.net";
                _tableEndpoint = $"https://{acct}.table.{suffix}";
            }
        }
    }

    private bool HasTableStorage => _accountName is not null && _accountKey is not null && _tableEndpoint is not null;

    private HttpRequestMessage CreateTableRequest(HttpMethod method, string pathAndQuery, string? jsonBody = null)
    {
        var dateHeader = DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture);
        var resourcePath = pathAndQuery.Split('?')[0];
        var stringToSign = $"{dateHeader}\n/{_accountName}/{resourcePath.TrimStart('/')}";

        using var hmac = new HMACSHA256(_accountKey!);
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        var req = new HttpRequestMessage(method, $"{_tableEndpoint}{pathAndQuery}");
        req.Headers.Add("x-ms-date", dateHeader);
        req.Headers.Add("x-ms-version", "2020-12-06");
        req.Headers.Add("Accept", "application/json;odata=nometadata");
        req.Headers.Authorization = new AuthenticationHeaderValue("SharedKeyLite", $"{_accountName}:{signature}");

        if (jsonBody is not null)
        {
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        return req;
    }

    private async Task EnsureTableAsync()
    {
        if (!HasTableStorage || _tableEnsured) return;
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var req = CreateTableRequest(HttpMethod.Post, "/Tables", "{\"TableName\":\"SpectrumBookings\"}");
            using var resp = await client.SendAsync(req);
            _tableEnsured = resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.Conflict;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure SpectrumBookings table; using in-memory store.");
        }
    }

    public async Task SaveAsync(BookingRecord record)
    {
        record.UpdatedAt = DateTimeOffset.UtcNow;
        _memory[record.Id] = record;

        if (!HasTableStorage) return;
        await EnsureTableAsync();

        try
        {
            var client = _httpClientFactory.CreateClient();
            var payloadJson = JsonSerializer.Serialize(new
            {
                PartitionKey = "BOOKING",
                RowKey = record.Id,
                Payload = JsonSerializer.Serialize(record, JsonOpts)
            });
            var path = $"/SpectrumBookings(PartitionKey='BOOKING',RowKey='{Uri.EscapeDataString(record.Id)}')";
            using var req = CreateTableRequest(HttpMethod.Put, path, payloadJson);
            await client.SendAsync(req);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist booking {Id} to Table Storage.", record.Id);
        }
    }

    public async Task<BookingRecord?> GetAsync(string id)
    {
        if (HasTableStorage)
        {
            await EnsureTableAsync();
            try
            {
                var client = _httpClientFactory.CreateClient();
                var path = $"/SpectrumBookings(PartitionKey='BOOKING',RowKey='{Uri.EscapeDataString(id)}')";
                using var req = CreateTableRequest(HttpMethod.Get, path);
                using var resp = await client.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("Payload", out var p))
                    {
                        var rec = JsonSerializer.Deserialize<BookingRecord>(p.GetString() ?? "{}", JsonOpts);
                        if (rec is not null)
                        {
                            _memory[rec.Id] = rec;
                            return rec;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read booking {Id} from Table Storage.", id);
            }
        }

        return _memory.TryGetValue(id, out var mem) ? mem : null;
    }

    public async Task<List<BookingRecord>> ListAsync()
    {
        if (HasTableStorage)
        {
            await EnsureTableAsync();
            try
            {
                var client = _httpClientFactory.CreateClient();
                using var req = CreateTableRequest(HttpMethod.Get, "/SpectrumBookings()?$filter=PartitionKey%20eq%20'BOOKING'");
                using var resp = await client.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<BookingRecord>();
                        foreach (var item in arr.EnumerateArray())
                        {
                            if (item.TryGetProperty("Payload", out var p))
                            {
                                var rec = JsonSerializer.Deserialize<BookingRecord>(p.GetString() ?? "{}", JsonOpts);
                                if (rec is not null)
                                {
                                    _memory[rec.Id] = rec;
                                    list.Add(rec);
                                }
                            }
                        }
                        return list.OrderByDescending(r => r.CreatedAt).ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list bookings from Table Storage.");
            }
        }

        return _memory.Values.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public static (bool IsOpen, string Note, string[] Slots) GetStudioSchedule(DateOnly date)
    {
        return date.DayOfWeek switch
        {
            DayOfWeek.Monday or DayOfWeek.Tuesday =>
                (false, "Studio is closed Mondays & Tuesdays. Please select Wednesday–Sunday.", []),
            DayOfWeek.Saturday =>
                (true, "Saturday Hours: 10:00 AM – 4:00 PM", ["10:00 AM", "11:30 AM", "1:00 PM", "2:30 PM"]),
            DayOfWeek.Sunday =>
                (true, "Sunday Hours: 10:00 AM – 3:00 PM", ["10:00 AM", "11:30 AM", "1:00 PM"]),
            _ =>
                (true, "Studio Hours: 10:00 AM – 6:00 PM", ["10:00 AM", "11:30 AM", "1:00 PM", "2:30 PM", "4:00 PM"])
        };
    }
}
