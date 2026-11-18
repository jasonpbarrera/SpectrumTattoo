using System.Collections.Concurrent;
using Azure;
using Azure.Data.Tables;
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

public sealed class BookingEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "BOOKING";
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Artist { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string TimeSlot { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Placement { get; set; } = string.Empty;
    public string IdeaDescription { get; set; } = string.Empty;
    public string Status { get; set; } = "PendingApproval";
    public int DepositAmountCents { get; set; } = 5000;
    public bool DepositPaid { get; set; }
    public string? StripeSessionId { get; set; }
    public string? StripeCheckoutUrl { get; set; }
    public string? ArtistNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static BookingEntity FromRecord(BookingRecord r) => new()
    {
        PartitionKey = "BOOKING",
        RowKey = r.Id,
        Artist = r.Artist,
        Date = r.Date,
        TimeSlot = r.TimeSlot,
        ServiceType = r.ServiceType,
        Name = r.Name,
        Phone = r.Phone,
        Email = r.Email,
        Placement = r.Placement,
        IdeaDescription = r.IdeaDescription,
        Status = r.Status,
        DepositAmountCents = r.DepositAmountCents,
        DepositPaid = r.DepositPaid,
        StripeSessionId = r.StripeSessionId,
        StripeCheckoutUrl = r.StripeCheckoutUrl,
        ArtistNote = r.ArtistNote,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt
    };

    public BookingRecord ToRecord() => new()
    {
        Id = RowKey,
        Artist = Artist,
        Date = Date,
        TimeSlot = TimeSlot,
        ServiceType = ServiceType,
        Name = Name,
        Phone = Phone,
        Email = Email,
        Placement = Placement,
        IdeaDescription = IdeaDescription,
        Status = Status,
        DepositAmountCents = DepositAmountCents,
        DepositPaid = DepositPaid,
        StripeSessionId = StripeSessionId,
        StripeCheckoutUrl = StripeCheckoutUrl,
        ArtistNote = ArtistNote,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt
    };
}

public sealed class BookingStore
{
    private readonly ConcurrentDictionary<string, BookingRecord> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly TableClient? _tableClient;
    private readonly ILogger<BookingStore> _logger;
    private bool _tableInitialized;

    public BookingStore(ILogger<BookingStore> logger)
    {
        _logger = logger;
        var conn = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")
                   ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage");

        if (!string.IsNullOrWhiteSpace(conn) &&
            !conn.Contains("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _tableClient = new TableClient(conn, "SpectrumBookings");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to configure Azure Table Storage; using in-memory booking store.");
            }
        }
    }

    private async Task<TableClient?> GetTableAsync()
    {
        if (_tableClient is null) return null;
        if (!_tableInitialized)
        {
            try
            {
                await _tableClient.CreateIfNotExistsAsync();
                _tableInitialized = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Azure Table Storage unavailable; falling back to in-memory store.");
                return null;
            }
        }
        return _tableClient;
    }

    public async Task SaveAsync(BookingRecord record)
    {
        record.UpdatedAt = DateTimeOffset.UtcNow;
        _memory[record.Id] = record;

        var table = await GetTableAsync();
        if (table is not null)
        {
            await table.UpsertEntityAsync(BookingEntity.FromRecord(record), TableUpdateMode.Replace);
        }
    }

    public async Task<BookingRecord?> GetAsync(string id)
    {
        var table = await GetTableAsync();
        if (table is not null)
        {
            try
            {
                var resp = await table.GetEntityIfExistsAsync<BookingEntity>("BOOKING", id);
                if (resp.HasValue && resp.Value is not null)
                {
                    var rec = resp.Value.ToRecord();
                    _memory[rec.Id] = rec;
                    return rec;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading booking {Id} from Table Storage.", id);
            }
        }

        return _memory.TryGetValue(id, out var mem) ? mem : null;
    }

    public async Task<List<BookingRecord>> ListAsync()
    {
        var table = await GetTableAsync();
        if (table is not null)
        {
            try
            {
                var results = new List<BookingRecord>();
                await foreach (var entity in table.QueryAsync<BookingEntity>(e => e.PartitionKey == "BOOKING"))
                {
                    var rec = entity.ToRecord();
                    _memory[rec.Id] = rec;
                    results.Add(rec);
                }
                return results.OrderByDescending(r => r.CreatedAt).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error listing bookings from Table Storage.");
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
