using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace CinDa.DaWatcha.Core;

public static class ConfigurationJson
{
    public static JsonSerializerOptions CreateOptions(bool writeIndented = false)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 64
        };
        options.Converters.Add(new StrictUtcDateTimeOffsetConverter());
        return options;
    }
}

public sealed class StrictUtcDateTimeOffsetConverter
    : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(
        ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("UTC timestamps must be JSON strings.");
        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text) ||
            !text.EndsWith('Z') ||
            !reader.TryGetDateTimeOffset(out var value) ||
            value.Offset != TimeSpan.Zero)
            throw new JsonException(
                "Timestamps must be ISO 8601 UTC values ending in Z.");
        return value;
    }

    public override void Write(
        Utf8JsonWriter writer, DateTimeOffset value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString(
            "O", CultureInfo.InvariantCulture));
}

public sealed class WatchConfigurationService : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json =
        ConfigurationJson.CreateOptions();
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private bool _disposed;

    public WatchConfigurationService(string path)
    {
        _path = System.IO.Path.GetFullPath(path);
    }

    public string Path => _path;
    public event Action<WatchConfiguration>? ConfigurationChanged;
    public event Action<Exception>? ReloadFailed;

    public async Task<WatchConfiguration> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadUnlockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void StartWatching()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watcher is not null)
            return;

        var directory = System.IO.Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Configuration has no directory.");
        Directory.CreateDirectory(directory);
        _debounceTimer = new Timer(
            _ => _ = ReloadFromWatcherAsync(), null,
            Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(directory,
            System.IO.Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite |
                NotifyFilters.FileName | NotifyFilters.Size |
                NotifyFilters.CreationTime
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;
    }

    private async Task<WatchConfiguration> ReadUnlockedAsync(
        CancellationToken cancellationToken)
    {
        byte[] bytes = [];
        Exception? lastError = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                bytes = await File.ReadAllBytesAsync(_path, cancellationToken);
                lastError = null;
                break;
            }
            catch (IOException exception) when (attempt < 3)
            {
                lastError = exception;
                await Task.Delay(100 * (attempt + 1), cancellationToken);
            }
        }
        if (lastError is not null)
            throw lastError;
        if (bytes.Length == 0)
            throw new InvalidDataException("Configuration is empty.");

        StrictJson.RejectDuplicateProperties(bytes);
        var config = JsonSerializer.Deserialize<WatchConfiguration>(bytes, _json)
            ?? throw new InvalidDataException("Configuration is empty.");
        ConfigurationPathResolver.ResolveRelativePaths(config, _path);
        var errors = ConfigurationValidator.Validate(config);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return config;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args) =>
        _debounceTimer?.Change(350, Timeout.Infinite);

    private void OnWatcherError(object sender, ErrorEventArgs args) =>
        ReloadFailed?.Invoke(args.GetException());

    private async Task ReloadFromWatcherAsync()
    {
        if (_disposed)
            return;
        try
        {
            ConfigurationChanged?.Invoke(await LoadAsync());
        }
        catch (Exception exception) when (!_disposed)
        {
            ReloadFailed?.Invoke(exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_watcher is not null)
            _watcher.EnableRaisingEvents = false;
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
        // A timer callback may already be inside LoadAsync. Leaving this small
        // semaphore for collection avoids disposing it underneath that callback.
    }
}
