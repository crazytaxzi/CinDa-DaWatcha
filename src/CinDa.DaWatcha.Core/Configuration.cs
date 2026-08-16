namespace CinDa.DaWatcha.Core;

public sealed class WatchConfiguration
{
    public AppSettings Settings { get; set; } = new();
    public List<WatchItem> Watches { get; set; } = [];
}

public sealed class AppSettings
{
    public string ChatBaseUrl { get; set; } = "https://chatgpt.com";
    public long ConversationLimitBytes { get; set; } = 5 * 1024 * 1024;
    public int PollIntervalMs { get; set; } = 1000;
    public int CompletionStablePolls { get; set; } = 2;
    public int HandoffRetries { get; set; } = 2;
    public int BatchWindowMs { get; set; } = 3000;
    public string FirefoxBinary { get; set; } =
        @"C:\Program Files\Mozilla Firefox\firefox.exe";
    public string FirefoxProfileDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
            "CinDa-DaWatcha", "FirefoxProfile");
}

public sealed class WatchItem
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public ProcessFingerprint Process { get; set; } = new();
    public CompletionRule Completion { get; set; } = new();
    public ChatTarget Chat { get; set; } = new();
    public string PassalongMessage { get; set; } = "";
}

public sealed class ProcessFingerprint
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public DateTimeOffset StartTimeUtc { get; set; }
}

public sealed class CompletionRule
{
    public string Method { get; set; } = "uia-text";
    public List<string> Patterns { get; set; } =
        ["Completed", "Finished", "Done", "Success"];
    public string WindowTitlePattern { get; set; } = "";
}

public sealed class ChatTarget
{
    public string Uuid { get; set; } = "";
}
