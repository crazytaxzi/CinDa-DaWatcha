namespace CinDa.DaWatcha.Core;

public static class ConfigurationValidator
{
    public static IReadOnlyList<string> Validate(WatchConfiguration config)
    {
        var errors = new List<string>();
        if (config.Settings.PollIntervalMs < 250)
            errors.Add("settings.pollIntervalMs must be at least 250.");
        if (config.Settings.CompletionStablePolls < 1)
            errors.Add("settings.completionStablePolls must be positive.");
        if (config.Settings.HandoffRetries < 1)
            errors.Add("settings.handoffRetries must be positive.");
        if (config.Settings.BatchWindowMs < 0)
            errors.Add("settings.batchWindowMs cannot be negative.");
        if (config.Settings.ConversationLimitBytes < 1)
            errors.Add("settings.conversationLimitBytes must be positive.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var watch in config.Watches)
        {
            var prefix = string.IsNullOrWhiteSpace(watch.Id)
                ? "watch[unnamed]" : $"watch[{watch.Id}]";
            if (string.IsNullOrWhiteSpace(watch.Id))
                errors.Add("Every watch requires an id.");
            else if (!ids.Add(watch.Id))
                errors.Add($"{prefix} has a duplicate id.");
            if (watch.Process.Pid <= 0)
                errors.Add($"{prefix}.process.pid must be positive.");
            if (string.IsNullOrWhiteSpace(watch.Process.Name))
                errors.Add($"{prefix}.process.name is required.");
            if (string.IsNullOrWhiteSpace(watch.Process.ExecutablePath))
                errors.Add($"{prefix}.process.executablePath is required.");
            if (watch.Process.StartTimeUtc == default)
                errors.Add($"{prefix}.process.startTimeUtc is required.");
            if (!Guid.TryParse(watch.Chat.Uuid, out _))
                errors.Add($"{prefix}.chat.uuid must be a UUID.");
            if (string.IsNullOrWhiteSpace(watch.PassalongMessage))
                errors.Add($"{prefix}.passalongMessage is required.");
            if (watch.Completion.Method.Equals(
                "uia-text", StringComparison.OrdinalIgnoreCase) &&
                watch.Completion.Patterns.Count == 0)
                errors.Add($"{prefix}.completion.patterns cannot be empty.");
        }

        return errors;
    }
}
