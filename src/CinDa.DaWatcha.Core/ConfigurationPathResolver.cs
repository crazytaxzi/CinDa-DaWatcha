namespace CinDa.DaWatcha.Core;

public static class ConfigurationPathResolver
{
    public static void ResolveRelativePaths(
        WatchConfiguration configuration, string configurationPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);

        var fullConfigurationPath = Path.GetFullPath(configurationPath);
        var root = Path.GetDirectoryName(fullConfigurationPath)
            ?? throw new InvalidDataException(
                "The job ledger must have a parent directory.");

        var settings = configuration.Settings
            ?? throw new InvalidDataException("settings cannot be null.");
        var jobs = configuration.Jobs
            ?? throw new InvalidDataException("jobs cannot be null.");

        settings.GeckoDriverPath = ResolveFile(
            root, settings.GeckoDriverPath,
            "settings.geckoDriverPath");
        settings.DeliveryStatePath = ResolveFile(
            root, settings.DeliveryStatePath,
            "settings.deliveryStatePath");

        foreach (var job in jobs)
        {
            if (job is null)
                throw new InvalidDataException(
                    "jobs cannot contain null entries.");
            if (job.Participants is null)
                throw new InvalidDataException(
                    $"job[{job.Id}].participants cannot be null.");
            foreach (var participant in job.Participants)
            {
                if (participant is null || participant.Process is null)
                    throw new InvalidDataException(
                        $"job[{job.Id}] contains a null participant or process.");
                participant.Process.ExecutablePath = ResolveFile(
                    root, participant.Process.ExecutablePath,
                    $"job[{job.Id}].participant[{participant.Id}]" +
                    ".process.executablePath");
            }
        }
    }

    private static string ResolveFile(
        string root, string configuredPath, string field)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidDataException($"{field} is required.");
        var segments = configuredPath.Split(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (Path.IsPathRooted(configuredPath) ||
            !configuredPath.StartsWith(@".\", StringComparison.Ordinal) ||
            segments.Any(segment => segment.Length == 0 ||
                segment.Equals("..", StringComparison.Ordinal) ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException(
                $"{field} must be relative to the job ledger directory " +
                "(for example .\\required-item.exe).");

        string resolved;
        try
        {
            resolved = Path.GetFullPath(configuredPath, root);
        }
        catch (Exception exception) when (exception is ArgumentException or
                   NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(
                $"{field} is not a valid relative path.", exception);
        }

        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"{field} cannot escape the job ledger directory.");
        return resolved;
    }
}
