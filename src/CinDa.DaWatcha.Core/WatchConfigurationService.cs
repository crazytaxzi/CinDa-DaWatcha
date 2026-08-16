using System.Text.Json;

namespace CinDa.DaWatcha.Core;

public sealed class WatchConfigurationService : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private DateTimeOffset _suppressEventsUntil;

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
                NotifyFilters.FileName | NotifyFilters.Size
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
        _watcher.EnableRaisingEvents = true;
    }

    public async Task<WatchConfiguration> UpdateUuidAsync(
        string oldUuid, string newUuid,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(newUuid, out _))
            throw new ArgumentException("The new conversation ID is not a UUID.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var config = await ReadUnlockedAsync(cancellationToken);
            var matches = config.Watches.Where(w =>
                w.Chat.Uuid.Equals(oldUuid,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                throw new InvalidOperationException(
                    $"No watch targets conversation {oldUuid}.");

            foreach (var watch in matches)
                watch.Chat.Uuid = newUuid;

            var errors = ConfigurationValidator.Validate(config);
            if (errors.Count > 0)
                throw new InvalidDataException(string.Join(Environment.NewLine, errors));

            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var json = JsonSerializer.Serialize(config, _json);
                await File.WriteAllTextAsync(temp, json, cancellationToken);
                _suppressEventsUntil = DateTimeOffset.UtcNow.AddSeconds(2);
                File.Move(temp, _path, true);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }

            ConfigurationChanged?.Invoke(config);
            return config;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WatchConfiguration> ReadUnlockedAsync(
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var config = await JsonSerializer.DeserializeAsync<WatchConfiguration>(
            stream, _json, cancellationToken)
            ?? throw new InvalidDataException("Configuration is empty.");

        var errors = ConfigurationValidator.Validate(config);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return config;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        if (DateTimeOffset.UtcNow <= _suppressEventsUntil)
            return;
        _debounceTimer?.Change(350, Timeout.Infinite);
    }

    private async Task ReloadFromWatcherAsync()
    {
        try
        {
            ConfigurationChanged?.Invoke(await LoadAsync());
        }
        catch (Exception exception)
        {
            ReloadFailed?.Invoke(exception);
        }
    }

    public void Dispose()
    {
        if (_watcher is not null)
            _watcher.EnableRaisingEvents = false;
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
        _gate.Dispose();
    }
}
