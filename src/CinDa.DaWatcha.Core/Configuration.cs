using System.Text.Json.Serialization;

namespace CinDa.DaWatcha.Core;

public sealed class WatchConfiguration
{
    [JsonRequired]
    public AppSettings Settings { get; set; } = new();

    [JsonRequired]
    public List<TrainingJob> Jobs { get; set; } = [];
}

public sealed class AppSettings
{
    [JsonRequired]
    public int PollIntervalMs { get; set; } = 1000;

    [JsonRequired]
    public int HeartbeatStaleMs { get; set; } = 120_000;

    [JsonRequired]
    public int ConversationIdlePollMs { get; set; } = 1000;

    [JsonRequired]
    public int ConversationIdleStablePolls { get; set; } = 3;

    [JsonRequired]
    public int ConversationIdleTimeoutMs { get; set; } = 15 * 60 * 1000;

    [JsonRequired]
    public int SendVerificationTimeoutMs { get; set; } = 45_000;

    [JsonRequired]
    public int AutomaticSendAttempts { get; set; } = 3;

    [JsonRequired]
    public string FirefoxBinary { get; set; } =
        @"C:\Program Files\Mozilla Firefox\firefox.exe";

    [JsonRequired]
    public string FirefoxProfileDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
            "CinDa-DaWatcha", "FirefoxProfile");

    [JsonRequired]
    public string GeckoDriverPath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "geckodriver.exe");

    [JsonRequired]
    public string DeliveryStatePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
            "CinDa-DaWatcha", "delivery-state.json");
}

public sealed class TrainingJob
{
    [JsonRequired]
    public string Id { get; set; } = "";

    [JsonRequired]
    public bool Enabled { get; set; }

    [JsonRequired]
    public string InitiatingChatUuid { get; set; } = "";

    public string? RecoveryChatUuid { get; set; }

    [JsonRequired]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [JsonRequired]
    public DateTimeOffset UpdatedAtUtc { get; set; }

    [JsonRequired]
    public string Summary { get; set; } = "";

    [JsonRequired]
    public List<JobParticipant> Participants { get; set; } = [];
}

[JsonConverter(typeof(StrictStringEnumConverter<ParticipantState>))]
public enum ParticipantState
{
    Pending,
    Running,
    Blocked,
    Succeeded,
    Failed
}

public sealed class JobParticipant
{
    [JsonRequired]
    public string Id { get; set; } = "";

    [JsonRequired]
    public ProcessFingerprint Process { get; set; } = new();

    [JsonRequired]
    public ParticipantState State { get; set; }

    [JsonRequired]
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? HeartbeatUtc { get; set; }

    public DateTimeOffset? FinishedAtUtc { get; set; }

    public int? ExitCode { get; set; }

    [JsonRequired]
    public string Detail { get; set; } = "";

    [JsonRequired]
    public string HandoffMessage { get; set; } = "";
}

public sealed class ProcessFingerprint
{
    [JsonRequired]
    public int Pid { get; set; }

    [JsonRequired]
    public string Name { get; set; } = "";

    [JsonRequired]
    public string ExecutablePath { get; set; } = "";

    [JsonRequired]
    public DateTimeOffset StartTimeUtc { get; set; }
}
