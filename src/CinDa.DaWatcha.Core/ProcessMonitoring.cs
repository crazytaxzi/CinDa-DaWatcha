using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CinDa.DaWatcha.Core;

public sealed record ProcessInspection(
    bool IsAlive,
    bool FingerprintMatches,
    bool? IsResponding,
    string Detail,
    bool IdentityIndeterminate = false);

public interface IProcessInspector
{
    ProcessInspection Inspect(ProcessFingerprint expected);
}

public sealed class SystemProcessInspector : IProcessInspector
{
    public ProcessInspection Inspect(ProcessFingerprint expected)
    {
        try
        {
            using var process = Process.GetProcessById(expected.Pid);
            if (process.HasExited)
                return new(false, false, null, "Process exited.");

            var actualName = NormalizeName(process.ProcessName);
            var expectedName = NormalizeName(expected.Name);
            if (!actualName.Equals(expectedName,
                    StringComparison.OrdinalIgnoreCase))
                return new(true, false, null,
                    $"PID belongs to {process.ProcessName}, not {expected.Name}.");

            var actualPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualPath))
                return new(true, false, null, "Executable path is unavailable.",
                    true);
            if (!Path.GetFullPath(actualPath).Equals(
                    Path.GetFullPath(expected.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
                return new(true, false, null, "Executable path does not match.");

            var actualStart = process.StartTime.ToUniversalTime();
            var delta = (actualStart - expected.StartTimeUtc.UtcDateTime).Duration();
            if (delta > TimeSpan.FromSeconds(1))
                return new(true, false, null, "Process start time does not match.");

            bool? responding = null;
            if (process.MainWindowHandle != nint.Zero)
            {
                try { responding = process.Responding; }
                catch (InvalidOperationException) { }
            }
            return new(true, true, responding, "Fingerprint verified.");
        }
        catch (ArgumentException)
        {
            return new(false, false, null, "PID is not running.");
        }
        catch (InvalidOperationException)
        {
            return new(false, false, null, "Process exited during inspection.");
        }
        catch (Exception exception)
        {
            return new(true, false, null,
                $"Identity check failed: {exception.Message}", true);
        }
    }

    private static string NormalizeName(string name) =>
        Path.GetFileNameWithoutExtension(name.Trim());
}

public enum JobOperationalState
{
    Disabled,
    WaitingForApplications,
    ApplicationBlocked,
    WaitingForExit,
    ReadyForHandoff,
    Invalid
}

public enum ParticipantOperationalState
{
    Active,
    Blocked,
    WaitingForExit,
    Succeeded,
    Failed
}

public sealed record ParticipantEvaluation(
    JobParticipant Participant,
    ParticipantOperationalState State,
    string Detail,
    bool IsTerminal);

public sealed record JobEvaluation(
    TrainingJob Job,
    JobOperationalState State,
    string Detail,
    IReadOnlyList<ParticipantEvaluation> Participants,
    IReadOnlyList<OutgoingMessage> OutgoingMessages,
    DateTimeOffset EvaluatedAt);

[System.Text.Json.Serialization.JsonConverter(
    typeof(StrictStringEnumConverter<OutgoingMessageKind>))]
public enum OutgoingMessageKind
{
    BlockedWarning,
    FinalHandoff
}

public sealed record OutgoingMessage(
    string DeliveryId,
    string JobId,
    string ConversationUuid,
    OutgoingMessageKind Kind,
    string Message,
    DateTimeOffset CreatedAtUtc);

public static class HandoffIdentity
{
    public static string Create(string stableKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(stableKey));
        return Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant();
    }
}

public sealed class JobEvaluator
{
    private readonly IProcessInspector _processInspector;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly HashSet<string> _observedAlive =
        new(StringComparer.OrdinalIgnoreCase);

    public JobEvaluator(
        IProcessInspector processInspector,
        Func<DateTimeOffset>? utcNow = null)
    {
        _processInspector = processInspector;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public JobEvaluation Evaluate(TrainingJob job, AppSettings settings)
    {
        var now = _utcNow();
        if (!job.Enabled)
            return new(job, JobOperationalState.Disabled,
                "Job monitoring is disabled.", [], [], now);

        var participants = job.Participants
            .Select(participant => EvaluateParticipant(
                participant, settings, now))
            .ToArray();
        var messages = new List<OutgoingMessage>();

        foreach (var blocked in participants.Where(item =>
                     item.State == ParticipantOperationalState.Blocked))
        {
            var process = blocked.Participant.Process;
            var stableKey = $"{job.Id}|blocked|{blocked.Participant.Id}|" +
                $"{process.Pid}|{process.Name}|{process.ExecutablePath}|" +
                $"{process.StartTimeUtc.UtcTicks}";
            var deliveryId = HandoffIdentity.Create(stableKey);
            messages.Add(new OutgoingMessage(
                deliveryId, job.Id, job.InitiatingChatUuid,
                OutgoingMessageKind.BlockedWarning,
                HandoffMessageBuilder.BuildBlockedWarning(
                    job, blocked, deliveryId), now));
        }

        JobOperationalState state;
        string detail;
        if (participants.Any(item =>
                item.State == ParticipantOperationalState.Blocked))
        {
            state = JobOperationalState.ApplicationBlocked;
            detail = "One or more applications are blocked or stale.";
        }
        else if (participants.Any(item =>
                     item.State == ParticipantOperationalState.Active))
        {
            state = JobOperationalState.WaitingForApplications;
            detail = "Waiting for every application to report a terminal state.";
        }
        else if (participants.Any(item =>
                     item.State == ParticipantOperationalState.WaitingForExit))
        {
            state = JobOperationalState.WaitingForExit;
            detail = "Every reporting application is terminal; waiting for shutdown.";
        }
        else
        {
            state = JobOperationalState.ReadyForHandoff;
            detail = "All applications are terminal and no matching process is running.";
            var deliveryId = HandoffIdentity.Create($"{job.Id}|final");
            messages.Add(new OutgoingMessage(
                deliveryId, job.Id, job.InitiatingChatUuid,
                OutgoingMessageKind.FinalHandoff,
                HandoffMessageBuilder.BuildFinal(job, participants,
                    deliveryId), now));
        }

        return new(job, state, detail, participants, messages, now);
    }

    private ParticipantEvaluation EvaluateParticipant(
        JobParticipant participant, AppSettings settings, DateTimeOffset now)
    {
        var inspection = _processInspector.Inspect(participant.Process);
        var matchingAlive = inspection.IsAlive && inspection.FingerprintMatches;
        var fingerprintKey = FingerprintKey(participant.Process);
        if (matchingAlive)
            _observedAlive.Add(fingerprintKey);
        var declaredTerminal = participant.State is
            ParticipantState.Succeeded or ParticipantState.Failed;

        if (inspection.IsAlive && inspection.IdentityIndeterminate)
            return new(participant, ParticipantOperationalState.Blocked,
                "The process is still present, but its identity and shutdown " +
                "cannot be verified.", false);

        if (matchingAlive && participant.State == ParticipantState.Blocked)
            return new(participant, ParticipantOperationalState.Blocked,
                string.IsNullOrWhiteSpace(participant.Detail)
                    ? "Application declared itself blocked."
                    : participant.Detail, false);

        if (matchingAlive && inspection.IsResponding == false)
            return new(participant, ParticipantOperationalState.Blocked,
                "The application window is not responding.", false);

        if (matchingAlive && participant.State is
                ParticipantState.Pending or ParticipantState.Running &&
            participant.HeartbeatUtc is { } heartbeat &&
            now - heartbeat > TimeSpan.FromMilliseconds(settings.HeartbeatStaleMs))
            return new(participant, ParticipantOperationalState.Blocked,
                $"Heartbeat is stale. Last heartbeat: {heartbeat:O}; " +
                $"threshold: {settings.HeartbeatStaleMs} ms.",
                false);

        if (declaredTerminal)
        {
            if (matchingAlive)
                return new(participant,
                    ParticipantOperationalState.WaitingForExit,
                    "Terminal handoff recorded; matching process is still running.",
                    false);
            return new(participant,
                participant.State == ParticipantState.Succeeded
                    ? ParticipantOperationalState.Succeeded
                    : ParticipantOperationalState.Failed,
                string.IsNullOrWhiteSpace(participant.Detail)
                    ? "The application reported a terminal state without detail."
                    : participant.Detail,
                true);
        }

        if (matchingAlive)
            return new(participant, ParticipantOperationalState.Active,
                inspection.Detail, false);

        if (participant.State == ParticipantState.Pending &&
            !_observedAlive.Contains(fingerprintKey))
        {
            if (participant.HeartbeatUtc is { } pendingHeartbeat &&
                now - pendingHeartbeat >
                    TimeSpan.FromMilliseconds(settings.HeartbeatStaleMs))
                return new(participant, ParticipantOperationalState.Blocked,
                    "Pending application did not start and its heartbeat is " +
                    $"stale. Last heartbeat: {pendingHeartbeat:O}; threshold: " +
                    $"{settings.HeartbeatStaleMs} ms.", false);
            return new(participant, ParticipantOperationalState.Active,
                "Waiting for the pending application fingerprint to appear.",
                false);
        }

        return new(participant, ParticipantOperationalState.Failed,
            "Process stopped or its fingerprint changed before a terminal " +
            "handoff was recorded.", true);
    }

    private static string FingerprintKey(ProcessFingerprint process) =>
        $"{process.Pid}|{process.Name}|{process.ExecutablePath}|" +
        $"{process.StartTimeUtc.UtcTicks}";
}

public sealed class JobMonitor : IAsyncDisposable
{
    private readonly Func<WatchConfiguration> _configuration;
    private readonly JobEvaluator _evaluator;
    private readonly HashSet<string> _emittedDeliveries =
        new(StringComparer.Ordinal);
    private CancellationTokenSource? _cancellation;
    private Task? _worker;

    public JobMonitor(
        Func<WatchConfiguration> configuration,
        JobEvaluator evaluator)
    {
        _configuration = configuration;
        _evaluator = evaluator;
    }

    public event Action<JobEvaluation>? StatusChanged;
    public event Action<OutgoingMessage>? DeliveryRequested;

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
            PollOnce(config);
            await Task.Delay(
                Math.Clamp(config.Settings.PollIntervalMs, 250, 60_000),
                cancellationToken);
        }
    }

    public IReadOnlyList<JobEvaluation> PollOnce(WatchConfiguration config)
    {
        var evaluations = new List<JobEvaluation>();
        foreach (var job in config.Jobs)
        {
            try
            {
                var evaluation = _evaluator.Evaluate(job, config.Settings);
                evaluations.Add(evaluation);
                SafeInvoke(StatusChanged, evaluation);
                foreach (var message in evaluation.OutgoingMessages)
                {
                    if (_emittedDeliveries.Contains(message.DeliveryId))
                        continue;
                    if (SafeInvoke(DeliveryRequested, message))
                        _emittedDeliveries.Add(message.DeliveryId);
                }
            }
            catch (Exception exception)
            {
                var evaluation = new JobEvaluation(job,
                    JobOperationalState.Invalid, exception.Message, [], [],
                    DateTimeOffset.UtcNow);
                evaluations.Add(evaluation);
                SafeInvoke(StatusChanged, evaluation);
            }
        }
        return evaluations;
    }

    private static bool SafeInvoke<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
            return false;
        var succeeded = false;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
                succeeded = true;
            }
            catch
            {
                // A failed observer must not terminate monitoring. The event is
                // retried on the next poll unless another observer accepted it.
            }
        }
        return succeeded;
    }

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
        _cancellation = null;
        _worker = null;
    }
}
