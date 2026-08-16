namespace CinDa.DaWatcha.Core;

public static class ConfigurationValidator
{
    public static IReadOnlyList<string> Validate(WatchConfiguration config)
    {
        var errors = new List<string>();
        if (config.Settings is null)
            errors.Add("settings cannot be null.");
        else
            ValidateSettings(config.Settings, errors);

        if (config.Jobs is null)
        {
            errors.Add("jobs cannot be null.");
            return errors;
        }

        var jobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in config.Jobs)
        {
            if (job is null)
            {
                errors.Add("jobs cannot contain null entries.");
                continue;
            }
            var prefix = string.IsNullOrWhiteSpace(job.Id)
                ? "job[unnamed]" : $"job[{job.Id}]";
            if (string.IsNullOrWhiteSpace(job.Id))
                errors.Add("Every job requires an id.");
            else if (!jobIds.Add(job.Id))
                errors.Add($"{prefix} has a duplicate id.");

            ValidateCanonicalUuid(
                job.InitiatingChatUuid,
                $"{prefix}.initiatingChatUuid", errors);
            if (!string.IsNullOrWhiteSpace(job.RecoveryChatUuid))
                ValidateCanonicalUuid(
                    job.RecoveryChatUuid,
                    $"{prefix}.recoveryChatUuid", errors);
            ValidateUtc(job.CreatedAtUtc, $"{prefix}.createdAtUtc", errors);
            ValidateUtc(job.UpdatedAtUtc, $"{prefix}.updatedAtUtc", errors);
            if (job.UpdatedAtUtc < job.CreatedAtUtc)
                errors.Add($"{prefix}.updatedAtUtc cannot precede createdAtUtc.");
            if (job.Summary is null)
                errors.Add($"{prefix}.summary cannot be null.");

            if (job.Participants is null)
            {
                errors.Add($"{prefix}.participants cannot be null.");
                continue;
            }
            if (job.Participants.Count == 0)
                errors.Add($"{prefix}.participants must contain at least one application.");
            var participantIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var participantFingerprints = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var participant in job.Participants)
            {
                if (participant is null)
                {
                    errors.Add($"{prefix}.participants cannot contain null entries.");
                    continue;
                }
                var participantPrefix = string.IsNullOrWhiteSpace(participant.Id)
                    ? $"{prefix}.participant[unnamed]"
                    : $"{prefix}.participant[{participant.Id}]";
                if (string.IsNullOrWhiteSpace(participant.Id))
                    errors.Add($"{prefix} contains a participant without an id.");
                else if (!participantIds.Add(participant.Id))
                    errors.Add($"{participantPrefix} has a duplicate id.");

                if (participant.Process is null)
                    errors.Add($"{participantPrefix}.process cannot be null.");
                else
                {
                    ValidateProcess(participant.Process, participantPrefix, errors);
                    var fingerprint = $"{participant.Process.Pid}|" +
                        $"{participant.Process.Name}|" +
                        $"{participant.Process.ExecutablePath}|" +
                        $"{participant.Process.StartTimeUtc.UtcTicks}";
                    if (!participantFingerprints.Add(fingerprint))
                        errors.Add($"{participantPrefix}.process duplicates another " +
                            "application fingerprint in this job.");
                }
                ValidateUtc(participant.UpdatedAtUtc,
                    $"{participantPrefix}.updatedAtUtc", errors);
                if (participant.HeartbeatUtc is { } heartbeat)
                    ValidateUtc(heartbeat,
                        $"{participantPrefix}.heartbeatUtc", errors);
                if (participant.FinishedAtUtc is { } finished)
                {
                    ValidateUtc(finished,
                        $"{participantPrefix}.finishedAtUtc", errors);
                    if (finished < job.CreatedAtUtc)
                        errors.Add($"{participantPrefix}.finishedAtUtc cannot " +
                            "precede the job creation time.");
                }

                if (participant.Detail is null)
                    errors.Add($"{participantPrefix}.detail cannot be null.");
                if (participant.HandoffMessage is null)
                    errors.Add($"{participantPrefix}.handoffMessage cannot be null.");

                if (participant.State is ParticipantState.Pending or
                    ParticipantState.Running && participant.HeartbeatUtc is null)
                    errors.Add($"{participantPrefix}.heartbeatUtc is required " +
                        "while pending or running.");

                if (participant.State is ParticipantState.Succeeded or
                    ParticipantState.Failed)
                {
                    if (participant.FinishedAtUtc is null)
                        errors.Add($"{participantPrefix}.finishedAtUtc is required " +
                            "for a terminal state.");
                    if (string.IsNullOrWhiteSpace(participant.HandoffMessage))
                        errors.Add($"{participantPrefix}.handoffMessage is required " +
                            "for a terminal state.");
                }
            }
        }

        return errors;
    }

    private static void ValidateSettings(
        AppSettings settings, List<string> errors)
    {
        if (settings.PollIntervalMs is < 250 or > 60_000)
            errors.Add("settings.pollIntervalMs must be from 250 through 60000.");
        if (settings.HeartbeatStaleMs is < 1_000 or > 86_400_000)
            errors.Add("settings.heartbeatStaleMs must be from 1000 through 86400000.");
        if (settings.ConversationIdlePollMs is < 250 or > 10_000)
            errors.Add("settings.conversationIdlePollMs must be from 250 through 10000.");
        if (settings.ConversationIdleStablePolls is < 2 or > 30)
            errors.Add("settings.conversationIdleStablePolls must be from 2 through 30.");
        if (settings.ConversationIdleTimeoutMs is < 10_000 or > 3_600_000)
            errors.Add("settings.conversationIdleTimeoutMs must be from 10000 through 3600000.");
        if (settings.SendVerificationTimeoutMs is < 5_000 or > 300_000)
            errors.Add("settings.sendVerificationTimeoutMs must be from 5000 through 300000.");
        if (settings.AutomaticSendAttempts is < 1 or > 5)
            errors.Add("settings.automaticSendAttempts must be from 1 through 5.");

        ValidateAbsoluteFile(settings.GeckoDriverPath,
            "settings.geckoDriverPath", "geckodriver.exe", errors,
            requireExists: false);
        ValidateAbsoluteFile(settings.DeliveryStatePath,
            "settings.deliveryStatePath", null, errors,
            requireExists: false);
    }

    private static void ValidateProcess(
        ProcessFingerprint process, string prefix, List<string> errors)
    {
        if (process.Pid <= 0)
            errors.Add($"{prefix}.process.pid must be positive.");
        if (string.IsNullOrWhiteSpace(process.Name))
            errors.Add($"{prefix}.process.name is required.");
        ValidateAbsoluteFile(process.ExecutablePath,
            $"{prefix}.process.executablePath", null, errors,
            requireExists: false);
        ValidateUtc(process.StartTimeUtc,
            $"{prefix}.process.startTimeUtc", errors);
    }

    private static void ValidateCanonicalUuid(
        string value, string field, List<string> errors)
    {
        if (!Guid.TryParseExact(value, "D", out var uuid) ||
            uuid == Guid.Empty)
            errors.Add($"{field} must be a canonical hyphenated UUID.");
    }

    private static void ValidateUtc(
        DateTimeOffset value, string field, List<string> errors)
    {
        if (value == default)
            errors.Add($"{field} is required.");
        else if (value.Offset != TimeSpan.Zero)
            errors.Add($"{field} must use UTC with a Z suffix.");
    }

    private static void ValidateAbsoluteFile(
        string value, string field, string? expectedName,
        List<string> errors, bool requireExists = true)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            errors.Add($"{field} must be an absolute file path.");
            return;
        }
        if (expectedName is not null && !Path.GetFileName(value).Equals(
                expectedName, StringComparison.OrdinalIgnoreCase))
            errors.Add($"{field} must identify {expectedName}.");
        if (requireExists && !File.Exists(value))
            errors.Add($"{field} does not exist: {value}");
    }

}
