using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CinDa.DaWatcha.Core;
using Microsoft.Win32;

namespace CinDa.DaWatcha.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<WatchRow> _rows = [];
    private readonly Queue<HandoffBatch> _handoffQueue = new();
    private readonly SemaphoreSlim _handoffGate = new(1, 1);
    private WatchConfiguration _configuration = new();
    private WatchConfigurationService? _configurationService;
    private ProcessMonitor? _monitor;
    private EventBatcher? _batcher;
    private FirefoxChatController? _browser;
    private CancellationTokenSource? _runCancellation;
    private PendingHandoff? _pending;

    public MainWindow()
    {
        InitializeComponent();
        WatchGrid.ItemsSource = _rows;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "CinDa-DaWatcha", "watch-config.json");
        ConfigPathBox.Text = path;
        await EnsureConfigurationFileAsync(path);
        await ReloadConfigurationAsync();
    }

    private async Task EnsureConfigurationFileAsync(string path)
    {
        if (File.Exists(path))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(new WatchConfiguration(),
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });
        await File.WriteAllTextAsync(path, json);
        AppendLog($"Created starter watch file: {path}");
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        try
        {
            _runCancellation = new CancellationTokenSource();
            _configurationService =
                new WatchConfigurationService(ConfigPathBox.Text);
            _configurationService.ConfigurationChanged += OnConfigurationChanged;
            _configurationService.ReloadFailed += OnConfigurationReloadFailed;
            _configuration = await _configurationService.LoadAsync(
                _runCancellation.Token);
            _configurationService.StartWatching();

            _browser = new FirefoxChatController(() => _configuration.Settings);
            _batcher = new EventBatcher(
                () => _configuration.Settings.BatchWindowMs);
            _batcher.BatchReady += OnBatchReady;
            _monitor = new ProcessMonitor(
                () => _configuration, new WindowCompletionDetector());
            _monitor.StatusChanged += OnWatchStatusChanged;
            _monitor.Triggered += OnWatchTriggered;
            _monitor.Start();

            RefreshRows();
            StartButton.IsEnabled = false;
            PauseButton.IsEnabled = true;
            ConfigPathBox.IsEnabled = false;
            SetStatus("Monitoring active.");
            AppendLog("Monitoring started.");
        }
        catch (Exception exception)
        {
            await StopServicesAsync();
            ShowError("Could not start monitoring", exception);
        }
    }

    private async void OnPause(object sender, RoutedEventArgs e)
    {
        await StopServicesAsync();
        SetStatus("Monitoring paused.");
        AppendLog("Monitoring paused.");
    }

    private async void OnReload(object sender, RoutedEventArgs e) =>
        await ReloadConfigurationAsync();

    private async Task ReloadConfigurationAsync()
    {
        try
        {
            if (!File.Exists(ConfigPathBox.Text))
                await EnsureConfigurationFileAsync(ConfigPathBox.Text);

            if (_configurationService is not null)
                _configuration = await _configurationService.LoadAsync();
            else
            {
                using var loader =
                    new WatchConfigurationService(ConfigPathBox.Text);
                _configuration = await loader.LoadAsync();
            }

            RefreshRows();
            SetStatus($"Loaded {_configuration.Watches.Count} watch record(s).");
            AppendLog("Configuration loaded.");
        }
        catch (Exception exception)
        {
            ShowError("Configuration is invalid", exception);
        }
    }

    private void OnConfigurationChanged(WatchConfiguration configuration)
    {
        _configuration = configuration;
        Dispatcher.BeginInvoke(() =>
        {
            RefreshRows();
            SetStatus("Configuration hot-reloaded.");
            AppendLog("Watch file changed; variables refreshed.");
        });
    }

    private void OnConfigurationReloadFailed(Exception exception) =>
        Dispatcher.BeginInvoke(() =>
        {
            SetStatus("Configuration reload failed.");
            AppendLog($"RELOAD ERROR: {exception.Message}");
        });

    private void OnWatchStatusChanged(WatchStatus status) =>
        Dispatcher.BeginInvoke(() =>
        {
            var index = FindRow(status.WatchId);
            if (index < 0)
                return;
            var current = _rows[index];
            _rows[index] = current with
            {
                State = status.State,
                Detail = status.Detail,
                UpdatedAt = status.UpdatedAt
            };
        });

    private void OnWatchTriggered(WatchEvent watchEvent)
    {
        _batcher?.Submit(watchEvent);
        Dispatcher.BeginInvoke(() =>
        {
            AppendLog($"{watchEvent.Watch.Id}: {watchEvent.Trigger} detected.");
            SetStatus("Completion event queued.");
        });
    }

    private void OnBatchReady(HandoffBatch batch) =>
        Dispatcher.BeginInvoke(() =>
        {
            _handoffQueue.Enqueue(batch);
            UpdateQueueText();
            _ = PrepareNextBatchAsync();
        });

    private async Task PrepareNextBatchAsync()
    {
        await _handoffGate.WaitAsync();
        try
        {
            if (_pending is not null || _handoffQueue.Count == 0 ||
                _browser is null || _runCancellation is null)
                return;

            var batch = _handoffQueue.Dequeue();
            UpdateQueueText();
            var message = HandoffMessageBuilder.Build(batch);
            var errors = new List<string>();
            var retries = Math.Max(1, _configuration.Settings.HandoffRetries);

            for (var attempt = 1; attempt <= retries; attempt++)
            {
                try
                {
                    SetStatus($"Preparing handoff attempt {attempt}/{retries}.");
                    await _browser.StartAsync(_runCancellation.Token);
                    await _browser.NavigateToConversationAsync(
                        batch.ConversationUuid, _runCancellation.Token);
                    var bytes = await _browser.GetVisibleConversationBytesAsync(
                        _runCancellation.Token);
                    var rollover = bytes >=
                        _configuration.Settings.ConversationLimitBytes;
                    if (rollover)
                    {
                        AppendLog($"Conversation is {bytes:N0} bytes; opening a new chat.");
                        await _browser.OpenNewConversationAsync(
                            _runCancellation.Token);
                    }

                    await _browser.PrepareMessageAsync(
                        message, _runCancellation.Token);
                    await _browser.WaitForSendReadyAsync(
                        _runCancellation.Token);
                    _pending = new PendingHandoff(batch, message, rollover);
                    ConfirmButton.IsEnabled = true;
                    SetStatus("Message prepared. Send it manually, then confirm.");
                    AppendLog($"Handoff ready for {batch.ConversationUuid}.");
                    return;
                }
                catch (Exception exception)
                {
                    var error = $"Attempt {attempt}: {exception.Message}";
                    errors.Add(error);
                    AppendLog($"HANDOFF ERROR: {error}");
                }
            }

            await PrepareFailureReportAsync(batch, errors);
        }
        finally
        {
            _handoffGate.Release();
        }
    }

    private async Task PrepareFailureReportAsync(
        HandoffBatch batch, IReadOnlyList<string> errors)
    {
        if (_browser is null || _runCancellation is null)
            return;
        try
        {
            var report = HandoffMessageBuilder.BuildFailure(batch, errors);
            await _browser.OpenNewConversationAsync(_runCancellation.Token);
            await _browser.PrepareMessageAsync(
                report, _runCancellation.Token);
            await _browser.WaitForSendReadyAsync(_runCancellation.Token);
            _pending = new PendingHandoff(batch, report, true);
            ConfirmButton.IsEnabled = true;
            SetStatus("Failure report prepared in a new chat; send manually.");
            AppendLog("Both attempts failed; prepared diagnostic handoff.");
        }
        catch (Exception exception)
        {
            SetStatus("Browser handoff failed completely.");
            AppendLog($"FATAL HANDOFF ERROR: {exception.Message}");
        }
    }

    private async void OnConfirmSent(object sender, RoutedEventArgs e)
    {
        if (_pending is null || _browser is null ||
            _configurationService is null || _runCancellation is null)
            return;

        try
        {
            var verification = await _browser.VerifyManualSendAsync(
                _pending.Message, _runCancellation.Token);
            if (!verification.BrowserSignalsSatisfied)
            {
                SetStatus("Send cannot be confirmed yet.");
                AppendLog(
                    $"VERIFY: composerEmpty={verification.ComposerEmpty}, " +
                    $"sendChanged={verification.SendButtonNotReady}, " +
                    $"messageVisible={verification.UserMessageVisible}");
                return;
            }

            if (_pending.Rollover)
            {
                var newUuid = await WaitForConversationUuidAsync(
                    _runCancellation.Token);
                if (newUuid is null)
                    throw new InvalidOperationException(
                        "The new conversation UUID has not appeared in the URL.");
                _configuration = await _configurationService.UpdateUuidAsync(
                    _pending.Batch.ConversationUuid,
                    newUuid, _runCancellation.Token);
                AppendLog($"Conversation UUID replaced with {newUuid}.");
            }

            AppendLog("Manual send verified by all four signals.");
            _pending = null;
            ConfirmButton.IsEnabled = false;
            SetStatus("Handoff completed.");
            RefreshRows();
            _ = PrepareNextBatchAsync();
        }
        catch (Exception exception)
        {
            ShowError("Could not confirm the handoff", exception);
        }
    }

    private async Task<string?> WaitForConversationUuidAsync(
        CancellationToken cancellationToken)
    {
        if (_browser is null)
            return null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var uuid = await _browser.CaptureConversationUuidAsync(
                cancellationToken);
            if (uuid is not null)
                return uuid;
            await Task.Delay(250, cancellationToken);
        }
        return null;
    }

    private async void OnOpenManagedFirefox(object sender, RoutedEventArgs e)
    {
        if (_browser is null || _runCancellation is null)
        {
            SetStatus("Start monitoring before opening managed Firefox.");
            return;
        }
        try
        {
            await _browser.OpenHomeForLoginAsync(_runCancellation.Token);
            SetStatus("Managed Firefox opened. Sign in to ChatGPT if needed.");
        }
        catch (Exception exception)
        {
            ShowError("Could not open managed Firefox", exception);
        }
    }

    private async void OnOpenSelectedChat(object sender, RoutedEventArgs e)
    {
        if (WatchGrid.SelectedItem is not WatchRow row)
        {
            SetStatus("Select a watch first.");
            return;
        }
        if (_browser is null || _runCancellation is null)
        {
            SetStatus("Start monitoring before opening a chat.");
            return;
        }

        try
        {
            await _browser.NavigateToConversationAsync(
                row.Uuid, _runCancellation.Token);
            SetStatus($"Opened chat for {row.Id}.");
        }
        catch (Exception exception)
        {
            ShowError("Could not open the conversation", exception);
        }
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = "watch-config.json",
            CheckFileExists = false
        };
        if (dialog.ShowDialog(this) != true)
            return;
        ConfigPathBox.Text = dialog.FileName;
        await ReloadConfigurationAsync();
    }

    private void RefreshRows()
    {
        var previous = _rows.ToDictionary(
            row => row.Id, StringComparer.OrdinalIgnoreCase);
        _rows.Clear();
        foreach (var watch in _configuration.Watches)
        {
            previous.TryGetValue(watch.Id, out var old);
            _rows.Add(new WatchRow(
                watch.Id,
                watch.Process.Pid,
                watch.Process.Name,
                watch.Chat.Uuid,
                old?.State ?? (watch.Enabled ? "Ready" : "Disabled"),
                old?.Detail ?? "",
                old?.UpdatedAt ?? DateTimeOffset.Now));
        }
    }

    private int FindRow(string id)
    {
        for (var index = 0; index < _rows.Count; index++)
            if (_rows[index].Id.Equals(id,
                    StringComparison.OrdinalIgnoreCase))
                return index;
        return -1;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void UpdateQueueText() =>
        QueueText.Text = $"Pending: {_handoffQueue.Count + (_pending is null ? 0 : 1)}";

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(message));
            return;
        }
        LogList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (LogList.Items.Count > 500)
            LogList.Items.RemoveAt(0);
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void ShowError(string title, Exception exception)
    {
        SetStatus(title + ".");
        AppendLog($"ERROR: {exception.Message}");
        MessageBox.Show(this, exception.Message, title,
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async Task StopServicesAsync()
    {
        _runCancellation?.Cancel();
        if (_monitor is not null)
            await _monitor.DisposeAsync();
        if (_batcher is not null)
            await _batcher.DisposeAsync();
        _configurationService?.Dispose();
        if (_browser is not null)
            await Task.Run(_browser.Dispose);

        _monitor = null;
        _batcher = null;
        _configurationService = null;
        _browser = null;
        _runCancellation?.Dispose();
        _runCancellation = null;
        _pending = null;
        _handoffQueue.Clear();
        ConfirmButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        StartButton.IsEnabled = true;
        ConfigPathBox.IsEnabled = true;
        UpdateQueueText();
    }

    private async void OnClosed(object? sender, EventArgs e) =>
        await StopServicesAsync();

    private sealed record PendingHandoff(
        HandoffBatch Batch,
        string Message,
        bool Rollover);
}

public sealed record WatchRow(
    string Id,
    int Pid,
    string ProcessName,
    string Uuid,
    string State,
    string Detail,
    DateTimeOffset UpdatedAt)
{
    public string ShortUuid => Uuid.Length > 8 ? Uuid[..8] + "…" : Uuid;
}
