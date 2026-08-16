using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CinDa.DaWatcha.Core;

[JsonConverter(typeof(StrictStringEnumConverter<DeliveryStatus>))]
public enum DeliveryStatus
{
    Queued,
    Attempting,
    ManualSendRequired,
    Delivered
}

public enum EnqueueDisposition
{
    Added,
    Existing,
    AlreadyDelivered,
    RouteConflict,
    PayloadConflict
}

public sealed class DeliveryRecord
{
    [JsonRequired]
    public string DeliveryId { get; set; } = "";

    [JsonRequired]
    public string JobId { get; set; } = "";

    [JsonRequired]
    public string ConversationUuid { get; set; } = "";

    [JsonRequired]
    public OutgoingMessageKind Kind { get; set; }

    [JsonRequired]
    public string Message { get; set; } = "";

    [JsonRequired]
    public string MessageSha256 { get; set; } = "";

    [JsonRequired]
    public DeliveryStatus Status { get; set; }

    [JsonRequired]
    public int AutomaticAttempts { get; set; }

    [JsonRequired]
    public bool RefreshAttempted { get; set; }

    [JsonRequired]
    public string Phase { get; set; } = "";

    [JsonRequired]
    public string LastError { get; set; } = "";

    [JsonRequired]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [JsonRequired]
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? DeliveredAtUtc { get; set; }

    public OutgoingMessage ToOutgoingMessage() => new(
        DeliveryId, JobId, ConversationUuid, Kind, Message, CreatedAtUtc);

    public DeliveryRecord Copy() => (DeliveryRecord)MemberwiseClone();
}

public sealed class JobRouteBinding
{
    [JsonRequired]
    public string JobId { get; set; } = "";

    [JsonRequired]
    public string InitiatingChatUuid { get; set; } = "";

    [JsonRequired]
    public DateTimeOffset BoundAtUtc { get; set; }
}

public sealed class DeliveryLedgerDocument
{
    [JsonRequired]
    public int Version { get; set; } = 1;

    [JsonRequired]
    public List<JobRouteBinding> JobRoutes { get; set; } = [];

    [JsonRequired]
    public List<DeliveryRecord> Deliveries { get; set; } = [];
}

public sealed class DeliveryStateStore : IAsyncDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json =
        ConfigurationJson.CreateOptions(writeIndented: true);
    private DeliveryLedgerDocument _document = new();
    private bool _initialized;

    public DeliveryStateStore(string path)
    {
        _path = System.IO.Path.GetFullPath(path);
    }

    public string Path => _path;

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;
            var directory = System.IO.Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException(
                    "Delivery state path has no directory.");
            Directory.CreateDirectory(directory);
            if (File.Exists(_path))
            {
                var bytes = await File.ReadAllBytesAsync(_path, cancellationToken);
                StrictJson.RejectDuplicateProperties(bytes);
                _document = JsonSerializer.Deserialize<DeliveryLedgerDocument>(
                    bytes, _json)
                    ?? throw new InvalidDataException(
                        "Delivery state document is empty.");
                ValidateDocument(_document);
            }
            else
            {
                _document = new DeliveryLedgerDocument();
                await SaveUnlockedAsync(cancellationToken);
            }
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EnqueueDisposition> EnqueueAsync(
        OutgoingMessage message,
        CancellationToken cancellationToken = default)
    {
        ValidateOutgoing(message);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            var route = _document.JobRoutes.FirstOrDefault(item =>
                item.JobId.Equals(message.JobId,
                    StringComparison.OrdinalIgnoreCase));
            if (route is null)
            {
                _document.JobRoutes.Add(new JobRouteBinding
                {
                    JobId = message.JobId,
                    InitiatingChatUuid = message.ConversationUuid,
                    BoundAtUtc = DateTimeOffset.UtcNow
                });
            }
            else if (!route.InitiatingChatUuid.Equals(
                         message.ConversationUuid,
                         StringComparison.OrdinalIgnoreCase))
            {
                return EnqueueDisposition.RouteConflict;
            }

            var hash = MessageHash(message.Message);
            var existing = FindDelivery(message.DeliveryId);
            if (existing is not null)
            {
                if (!existing.MessageSha256.Equals(hash,
                        StringComparison.OrdinalIgnoreCase) ||
                    !existing.ConversationUuid.Equals(message.ConversationUuid,
                        StringComparison.OrdinalIgnoreCase) ||
                    !existing.JobId.Equals(message.JobId,
                        StringComparison.OrdinalIgnoreCase) ||
                    existing.Kind != message.Kind)
                    return EnqueueDisposition.PayloadConflict;
                return existing.Status == DeliveryStatus.Delivered
                    ? EnqueueDisposition.AlreadyDelivered
                    : EnqueueDisposition.Existing;
            }

            var now = DateTimeOffset.UtcNow;
            _document.Deliveries.Add(new DeliveryRecord
            {
                DeliveryId = message.DeliveryId,
                JobId = message.JobId,
                ConversationUuid = message.ConversationUuid,
                Kind = message.Kind,
                Message = message.Message,
                MessageSha256 = hash,
                Status = DeliveryStatus.Queued,
                Phase = "Queued",
                CreatedAtUtc = message.CreatedAtUtc,
                UpdatedAtUtc = now
            });
            await SaveUnlockedAsync(cancellationToken);
            return EnqueueDisposition.Added;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<DeliveryRecord>> GetOutstandingAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            return _document.Deliveries
                .Where(item => item.Status != DeliveryStatus.Delivered)
                .OrderBy(item => item.CreatedAtUtc)
                .Select(item => item.Copy())
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DeliveryRecord?> GetAsync(
        string deliveryId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            return FindDelivery(deliveryId)?.Copy();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task MarkAttemptingAsync(
        string deliveryId, int attempt, bool refreshed,
        string phase, string error,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(deliveryId, record =>
        {
            record.Status = DeliveryStatus.Attempting;
            record.AutomaticAttempts = Math.Max(
                record.AutomaticAttempts, attempt);
            record.RefreshAttempted |= refreshed;
            record.Phase = phase;
            record.LastError = error;
        }, cancellationToken);

    public Task MarkManualRequiredAsync(
        string deliveryId, string error,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(deliveryId, record =>
        {
            record.Status = DeliveryStatus.ManualSendRequired;
            record.Phase = "Manual send required";
            record.LastError = error;
        }, cancellationToken);

    public Task ResetQueuedAsync(
        string deliveryId,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(deliveryId, record =>
        {
            record.Status = DeliveryStatus.Queued;
            record.Phase = "Queued for operator retry";
            record.LastError = "";
        }, cancellationToken);

    public Task MarkDeliveredAsync(
        string deliveryId,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(deliveryId, record =>
        {
            record.Status = DeliveryStatus.Delivered;
            record.Phase = "Delivered and verified";
            record.LastError = "";
            record.DeliveredAtUtc = DateTimeOffset.UtcNow;
        }, cancellationToken);

    private async Task UpdateAsync(
        string deliveryId,
        Action<DeliveryRecord> update,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureInitialized();
            var record = FindDelivery(deliveryId)
                ?? throw new InvalidOperationException(
                    $"Delivery {deliveryId} is not registered.");
            update(record);
            record.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await SaveUnlockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private DeliveryRecord? FindDelivery(string deliveryId) =>
        _document.Deliveries.FirstOrDefault(item =>
            item.DeliveryId.Equals(deliveryId,
                StringComparison.OrdinalIgnoreCase));

    private async Task SaveUnlockedAsync(CancellationToken cancellationToken)
    {
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, _document, _json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temp, _path, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static void ValidateDocument(DeliveryLedgerDocument document)
    {
        if (document.Version != 1)
            throw new InvalidDataException(
                $"Unsupported delivery state version {document.Version}.");
        if (document.JobRoutes is null || document.Deliveries is null)
            throw new InvalidDataException(
                "Delivery state lists cannot be null.");
        if (document.JobRoutes.Any(route => route is null) ||
            document.Deliveries.Any(delivery => delivery is null))
            throw new InvalidDataException(
                "Delivery state lists cannot contain null entries.");
        if (document.JobRoutes.GroupBy(item => item.JobId,
                StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException(
                "Delivery state contains duplicate job route bindings.");
        if (document.Deliveries.GroupBy(item => item.DeliveryId,
                StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException(
                "Delivery state contains duplicate delivery IDs.");
        foreach (var route in document.JobRoutes)
        {
            if (string.IsNullOrWhiteSpace(route.JobId))
                throw new InvalidDataException(
                    "Delivery state contains an empty job route ID.");
            if (!Guid.TryParseExact(route.InitiatingChatUuid, "D", out _))
                throw new InvalidDataException(
                    $"Job route {route.JobId} has an invalid UUID.");
            if (route.BoundAtUtc == default ||
                route.BoundAtUtc.Offset != TimeSpan.Zero)
                throw new InvalidDataException(
                    $"Job route {route.JobId} has an invalid bound time.");
        }
        foreach (var delivery in document.Deliveries)
        {
            if (delivery.DeliveryId is null ||
                delivery.DeliveryId.Length != 32 ||
                !delivery.DeliveryId.All(Uri.IsHexDigit))
                throw new InvalidDataException(
                    $"Delivery ID {delivery.DeliveryId} is not 32 hexadecimal characters.");
            if (string.IsNullOrWhiteSpace(delivery.JobId) ||
                string.IsNullOrWhiteSpace(delivery.Message))
                throw new InvalidDataException(
                    $"Delivery {delivery.DeliveryId} is missing required content.");
            if (!Guid.TryParseExact(delivery.ConversationUuid, "D", out _))
                throw new InvalidDataException(
                    $"Delivery {delivery.DeliveryId} has an invalid UUID.");
            var route = document.JobRoutes.FirstOrDefault(item =>
                item.JobId.Equals(delivery.JobId,
                    StringComparison.OrdinalIgnoreCase));
            if (route is null || !route.InitiatingChatUuid.Equals(
                    delivery.ConversationUuid,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Delivery {delivery.DeliveryId} does not match a bound job route.");
            if (delivery.MessageSha256 is null ||
                !delivery.MessageSha256.Equals(
                    MessageHash(delivery.Message),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Delivery {delivery.DeliveryId} failed its content hash check.");
            if (delivery.CreatedAtUtc == default ||
                delivery.CreatedAtUtc.Offset != TimeSpan.Zero ||
                delivery.UpdatedAtUtc == default ||
                delivery.UpdatedAtUtc.Offset != TimeSpan.Zero)
                throw new InvalidDataException(
                    $"Delivery {delivery.DeliveryId} has an invalid UTC timestamp.");
            if (delivery.Status == DeliveryStatus.Delivered &&
                (delivery.DeliveredAtUtc is null ||
                 delivery.DeliveredAtUtc.Value.Offset != TimeSpan.Zero))
                throw new InvalidDataException(
                    $"Delivered record {delivery.DeliveryId} lacks a UTC delivery time.");
        }
    }

    private static string MessageHash(string message) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(message))).ToLowerInvariant();

    private static void ValidateOutgoing(OutgoingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(message.DeliveryId) ||
            message.DeliveryId.Length != 32 ||
            !message.DeliveryId.All(Uri.IsHexDigit))
            throw new ArgumentException(
                "Delivery ID must contain 32 hexadecimal characters.");
        if (string.IsNullOrWhiteSpace(message.JobId) ||
            string.IsNullOrWhiteSpace(message.Message))
            throw new ArgumentException(
                "Outgoing delivery job ID and message are required.");
        if (!Guid.TryParseExact(message.ConversationUuid, "D", out var uuid) ||
            uuid == Guid.Empty)
            throw new ArgumentException(
                "Outgoing delivery requires a canonical non-empty UUID.");
        if (!Enum.IsDefined(message.Kind))
            throw new ArgumentException("Outgoing delivery kind is invalid.");
        if (message.CreatedAtUtc == default ||
            message.CreatedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException(
                "Outgoing delivery creation time must be UTC.");
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException(
                "Delivery state store has not been initialized.");
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
