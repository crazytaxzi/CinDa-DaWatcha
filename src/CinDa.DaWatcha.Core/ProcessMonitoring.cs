using System.Diagnostics;

namespace CinDa.DaWatcha.Core;

public enum WatchTriggerKind
{
    Completion,
    Exit
}

public sealed record WatchEvent(
    WatchItem Watch,
    WatchTriggerKind Trigger,
    DateTimeOffset OccurredAt,
    string Detail);

public sealed record WatchStatus(
    string WatchId,
    int Pid,
    string State,
    string Detail,
    DateTimeOffset UpdatedAt);

public sealed record ProcessInspection(
    bool IsAlive,
    bool FingerprintMatches,
    string Detail);

public interface ICompletionDetector
{
    Task<bool> IsCompleteAsync(
        WatchItem watch, CancellationToken cancellationToken);
}

public static class ProcessIdentityVerifier
{
    public static ProcessInspection Inspect(ProcessFingerprint expected)
    {
        try
        {
            using var process = Process.GetProcessById(expected.Pid);
            if (process.HasExited)
                return new(false, false, "Process exited.");

            var actualName = NormalizeName(process.ProcessName);
            var expectedName = NormalizeName(expected.Name);
            if (!actualName.Equals(expectedName,
                    StringComparison.OrdinalIgnoreCase))
                return new(true, false,
                    $"PID belongs to {process.ProcessName}, not {expected.Name}.");

            var actualPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualPath))
                return new(true, false, "Executable path is unavailable.");
            if (!Path.GetFullPath(actualPath).Equals(
                    Path.GetFullPath(expected.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
                return new(true, false, "Executable path does not match.");

            var actualStart = process.StartTime.ToUniversalTime();
            var delta = (actualStart - expected.StartTimeUtc.UtcDateTime).Duration();
            if (delta > TimeSpan.FromSeconds(2))
                return new(true, false, "Process start time does not match.");

            return new(true, true, "Fingerprint verified.");
        }
        catch (ArgumentException)
        {
            return new(false, false, "PID is not running.");
        }
        catch (InvalidOperationException)
        {
            return new(false, false, "Process exited during inspection.");
        }
        catch (Exception exception)
        {
            return new(true, false, $"Identity check failed: {exception.Message}");
        }
    }

    private static string NormalizeName(string name) =>
        Path.GetFileNameWithoutExtension(name.Trim());
}

public sealed class ProcessMonitor : IAsyncDisposable
{
    private readonly Func<WatchConfiguration> _configuration;
    private readonly ICompletionDetector _completionDetector;
    private readonly Dictionary<string, RuntimeState> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cancellation;
    private Task? _worker;

    public ProcessMonitor(
        Func<WatchConfiguration> configuration,
        ICompletionDetector completionDetector)
    {
        _configuration = configuration;
        _completionDetector = completionDetector;
    }

    public event Action<WatchStatus>? StatusChanged;
    public event Action<WatchEvent>? Triggered;

    public void Start()
    {
        if (_worker is not null)
            return;
        _cancellation = new CancellationTokenSource();
        _worker = RunAsync(_cancellation.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var config = _configuration();
            await PollOnceAsync(config, cancellationToken);
            await Task.Delay(
                Math.Max(config.Settings.PollIntervalMs, 250),
                cancellationToken);
        }
    }

    public async Task PollOnceAsync(
        WatchConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var watch in config.Watches)
        {
            activeIds.Add(watch.Id);
            if (!watch.Enabled)
            {
                Publish(watch, "Disabled", "Monitoring disabled.");
                continue;
            }

            var identity = IdentityKey(watch.Process);
            if (!_states.TryGetValue(watch.Id, out var state) ||
                state.Identity != identity)
            {
                state = new RuntimeState(identity);
                _states[watch.Id] = state;
            }

            var inspection = ProcessIdentityVerifier.Inspect(watch.Process);
            if (!inspection.IsAlive)
            {
                if (state.SeenAlive && !state.Triggered)
                    Fire(watch, state, WatchTriggerKind.Exit, inspection.Detail);
                Publish(watch, state.Triggered ? "Handled" : "Not running",
                    inspection.Detail);
                continue;
            }

            if (!inspection.FingerprintMatches)
            {
                state.StableCompletionPolls = 0;
                Publish(watch, "Stale PID", inspection.Detail);
                continue;
            }

            state.SeenAlive = true;
            if (state.Triggered)
            {
                Publish(watch, "Handled", "One handoff already prepared.");
                continue;
            }

            bool complete;
            try
            {
                complete = await _completionDetector.IsCompleteAsync(
                    watch, cancellationToken);
            }
            catch (Exception exception)
            {
                Publish(watch, "Detector error", exception.Message);
                continue;
            }

            state.StableCompletionPolls = complete
                ? state.StableCompletionPolls + 1 : 0;
            if (state.StableCompletionPolls >=
                config.Settings.CompletionStablePolls)
                Fire(watch, state, WatchTriggerKind.Completion,
                    "Configured completion state remained stable.");

            Publish(watch, state.Triggered ? "Queued" : "Watching",
                complete ? "Completion state observed." : inspection.Detail);
        }

        foreach (var removed in _states.Keys.Except(activeIds).ToArray())
            _states.Remove(removed);
    }

    private void Fire(
        WatchItem watch, RuntimeState state,
        WatchTriggerKind trigger, string detail)
    {
        state.Triggered = true;
        Triggered?.Invoke(new WatchEvent(
            watch, trigger, DateTimeOffset.UtcNow, detail));
    }

    private void Publish(WatchItem watch, string state, string detail) =>
        StatusChanged?.Invoke(new WatchStatus(
            watch.Id, watch.Process.Pid, state, detail, DateTimeOffset.Now));

    private static string IdentityKey(ProcessFingerprint process) =>
        $"{process.Pid}|{process.Name}|{process.ExecutablePath}|" +
        process.StartTimeUtc.UtcTicks;

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is null)
            return;
        _cancellation.Cancel();
        if (_worker is not null)
        {
            try { await _worker; }
            catch (OperationCanceledException) { }
        }
        _cancellation.Dispose();
    }

    private sealed class RuntimeState(string identity)
    {
        public string Identity { get; } = identity;
        public bool SeenAlive { get; set; }
        public bool Triggered { get; set; }
        public int StableCompletionPolls { get; set; }
    }
}
