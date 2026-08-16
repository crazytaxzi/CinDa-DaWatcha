using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using CinDa.DaWatcha.Core;
using Microsoft.Win32;

namespace CinDa.DaWatcha.App;

[SuppressMessage("Design", "CA1001:Types that own disposable fields " +
    "should be disposable", Justification =
    "WPF owns the window lifecycle; OnClosing awaits all resource disposal.")]
public partial class MainWindow : Window
{
    private readonly ObservableCollection<JobRow> _rows = [];
    private readonly Dictionary<string, JobOperationalState> _jobStates =
        new(StringComparer.OrdinalIgnoreCase);
    private Channel<OutgoingMessage>? _incoming;
    private readonly SemaphoreSlim _manualActionGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private volatile WatchConfiguration _configuration = new();
    private WatchConfigurationService? _configurationService;
    private DeliveryStateStore? _deliveryStore;
    private JobMonitor? _monitor;
    private FirefoxChatController? _browser;
    private HandoffDeliveryCoordinator? _deliveryCoordinator;
    private CancellationTokenSource? _runCancellation;
    private Task? _deliveryWorker;
    private DeliveryRecord? _manualPending;
    private bool _automaticDeliveryActive;
    private bool _closing;
    private bool _shutdownReady;

    public MainWindow()
    {
        InitializeComponent();
        JobGrid.ItemsSource = _rows;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "watch-config.json");
        ConfigPathBox.Text = path;
        try
        {
            await EnsureConfigurationFileAsync(path);
            await ReloadConfigurationAsync();
        }
        catch (Exception exception)
        {
            ShowError("Could not initialize the job ledger", exception);
        }
    }

    private static async Task EnsureConfigurationFileAsync(string path)
    {
        if (File.Exists(path))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new WatchConfiguration(),
                    ConfigurationJson.CreateOptions(writeIndented: true));
                await stream.FlushAsync();
            }
            File.Move(temp, path, false);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        await _lifecycleGate.WaitAsync();
        try
        {
            await StopServicesAsync();
            _runCancellation = new CancellationTokenSource();
            _configurationService =
                new WatchConfigurationService(ConfigPathBox.Text);
            _configurationService.ConfigurationChanged += OnConfigurationChanged;
            _configurationService.ReloadFailed += OnConfigurationReloadFailed;
            _configuration = await _configurationService.LoadAsync(
                _runCancellation.Token);
            if (Path.GetFullPath(ConfigPathBox.Text).Equals(
                    Path.GetFullPath(_configuration.Settings.DeliveryStatePath),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The job ledger and delivery-state ledger must use different paths.");
            _configurationService.StartWatching();

            _deliveryStore = new DeliveryStateStore(
                _configuration.Settings.DeliveryStatePath);
            await _deliveryStore.InitializeAsync(_runCancellation.Token);
            _browser = new FirefoxChatController(() => _configuration.Settings);
            _deliveryCoordinator = new HandoffDeliveryCoordinator(
                _browser, () => _configuration.Settings);
            _incoming = Channel.CreateUnbounded<OutgoingMessage>();
            _monitor = new JobMonitor(
                () => _configuration,
                new JobEvaluator(new SystemProcessInspector()));
            _monitor.StatusChanged += OnJobStatusChanged;
            _monitor.DeliveryRequested += OnDeliveryRequested;
            _deliveryWorker = ProcessDeliveriesAsync(
                _incoming, _runCancellation.Token);
            _monitor.Start();

            RefreshRows();
            StartButton.IsEnabled = false;
            PauseButton.IsEnabled = true;
            LoginButton.IsEnabled = true;
            ConfigPathBox.IsEnabled = false;
            SetOperationalState("MONITORING ACTIVE",
                "Waiting for application-written job updates.", "#166534");
            AppendLog($"Monitoring started. Delivery state: {_deliveryStore.Path}");
        }
        catch (Exception exception)
        {
            await StopServicesAsync();
            ShowError("Could not start monitoring", exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async void OnPause(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        await _lifecycleGate.WaitAsync();
        try
        {
            await StopServicesAsync();
            SetOperationalState("MONITORING PAUSED",
                "Durable delivery state was preserved.", "#854D0E");
            AppendLog("Monitoring paused safely.");
        }
        catch (Exception exception)
        {
            ShowError("Could not pause monitoring cleanly", exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }
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
            SetStatus($"Validated {_configuration.Jobs.Count} job(s).");
            AppendLog("Job ledger validated and loaded.");
        }
        catch (Exception exception)
        {
            ShowError("Job ledger is invalid", exception);
        }
    }

    private void OnConfigurationChanged(WatchConfiguration configuration)
    {
        _configuration = configuration;
        Dispatcher.BeginInvoke(() =>
        {
            RefreshRows();
            SetStatus("Job ledger hot-reloaded.");
            AppendLog("Job ledger changed; complete validated snapshot accepted.");
        });
    }

    private void OnConfigurationReloadFailed(Exception exception) =>
        Dispatcher.BeginInvoke(() =>
        {
            SetOperationalState("LEDGER UPDATE REJECTED",
                "The last valid snapshot remains active. " + exception.Message,
                "#991B1B");
            AppendLog($"LEDGER ERROR: {exception.Message}");
        });

    private void OnJobStatusChanged(JobEvaluation evaluation) =>
        Dispatcher.BeginInvoke(() =>
        {
            var index = FindRow(evaluation.Job.Id);
            if (index < 0)
            {
                RefreshRows();
                index = FindRow(evaluation.Job.Id);
            }
            if (index < 0)
                return;
            var current = _rows[index];
            _jobStates[evaluation.Job.Id] = evaluation.State;
            _rows[index] = current with
            {
                ParticipantSummary = FormatParticipants(evaluation.Participants),
                State = FormatState(evaluation.State),
                Detail = evaluation.Detail
            };
            if (evaluation.State == JobOperationalState.ApplicationBlocked &&
                !_automaticDeliveryActive && _manualPending is null)
                SetOperationalState("APPLICATION BLOCKED",
                    $"{evaluation.Job.Id}: {evaluation.Detail}", "#991B1B");
            else if (!_jobStates.Values.Any(state =>
                         state == JobOperationalState.ApplicationBlocked) &&
                     !_automaticDeliveryActive && _manualPending is null &&
                     OperationalStateText.Text == "APPLICATION BLOCKED")
                SetOperationalState("MONITORING ACTIVE",
                    "The blocked condition cleared; monitoring continues.",
                    "#166534");
        });

    private void OnDeliveryRequested(OutgoingMessage message)
    {
        if (_incoming is null || !_incoming.Writer.TryWrite(message))
            AppendLog($"QUEUE ERROR: could not accept {message.DeliveryId}.");
    }

    private async Task ProcessDeliveriesAsync(
        Channel<OutgoingMessage> incoming,
        CancellationToken cancellationToken)
    {
        if (_deliveryStore is null || _deliveryCoordinator is null)
            return;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    while (incoming.Reader.TryRead(out var message))
                    {
                        try
                        {
                            await RegisterIncomingAsync(message,
                                cancellationToken);
                        }
                        catch
                        {
                            incoming.Writer.TryWrite(message);
                            throw;
                        }
                    }

                    if (_manualPending is null &&
                        await ProcessNextOutstandingAsync(cancellationToken))
                        continue;

                    if (!await incoming.Reader.WaitToReadAsync(cancellationToken))
                        break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    await Dispatcher.BeginInvoke(() =>
                    {
                        SetOperationalState("DELIVERY RETRY PENDING",
                            exception.Message, "#991B1B");
                        AppendLog($"DELIVERY ERROR (will retry): {exception.Message}");
                    });
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RegisterIncomingAsync(
        OutgoingMessage message, CancellationToken cancellationToken)
    {
        if (_deliveryStore is null)
            return;
        var disposition = await _deliveryStore.EnqueueAsync(
            message, cancellationToken);
        await Dispatcher.BeginInvoke(() =>
        {
            switch (disposition)
            {
                case EnqueueDisposition.Added:
                    AppendLog($"Queued {message.Kind} {message.DeliveryId} " +
                        $"for job {message.JobId}.");
                    SetJobDelivery(message.JobId, "Queued");
                    break;
                case EnqueueDisposition.AlreadyDelivered:
                    SetJobDelivery(message.JobId, "Delivered");
                    break;
                case EnqueueDisposition.RouteConflict:
                    SetOperationalState("UUID ROUTE CONFLICT",
                        $"Job {message.JobId} attempted to change its initiating UUID. " +
                        "The delivery was quarantined.", "#991B1B");
                    AppendLog($"ROUTE CONFLICT: job {message.JobId}.");
                    break;
                case EnqueueDisposition.PayloadConflict:
                    SetOperationalState("HANDOFF CONTENT CONFLICT",
                        $"Delivery ID {message.DeliveryId} changed content. " +
                        "The delivery was quarantined.", "#991B1B");
                    AppendLog($"PAYLOAD CONFLICT: {message.DeliveryId}.");
                    break;
            }
        });
        await UpdateQueueTextAsync(cancellationToken);
    }

    private async Task<bool> ProcessNextOutstandingAsync(
        CancellationToken cancellationToken)
    {
        if (_deliveryStore is null || _deliveryCoordinator is null)
            return false;
        var outstanding = await _deliveryStore.GetOutstandingAsync(
            cancellationToken);
        var next = outstanding.Count == 0 ? null : outstanding[0];
        if (next is null)
        {
            await UpdateQueueTextAsync(cancellationToken);
            return false;
        }
        if (next.Status == DeliveryStatus.ManualSendRequired)
        {
            _manualPending = next;
            await Dispatcher.BeginInvoke(() => ShowManualRequired(next));
            await UpdateQueueTextAsync(cancellationToken);
            return false;
        }

        await Dispatcher.BeginInvoke(() =>
        {
            ShowActiveDelivery(next, "Starting automatic delivery", "");
            SetJobDelivery(next.JobId, "Automatic delivery");
            SetOperationalState("AUTOMATIC HANDOFF IN PROGRESS",
                $"Job {next.JobId} is being delivered only to initiating UUID " +
                $"{next.ConversationUuid}.", "#1D4ED8");
        });
        var outcome = await _deliveryCoordinator.DeliverAutomaticallyAsync(
            next.ToOutgoingMessage(),
            progress => OnDeliveryProgressAsync(progress, cancellationToken),
            cancellationToken);
        if (outcome.Delivered)
        {
            await _deliveryStore.MarkDeliveredAsync(
                next.DeliveryId, cancellationToken);
            await Dispatcher.BeginInvoke(() =>
            {
                SetJobDelivery(next.JobId, "Delivered and verified");
                SetOperationalState("HANDOFF DELIVERED",
                    $"Job {next.JobId}, delivery {next.DeliveryId} was verified " +
                    "in the initiating conversation.", "#166534");
                AppendLog($"DELIVERED: {next.DeliveryId} to {next.ConversationUuid}.");
                ClearActiveDelivery();
            });
        }
        else
        {
            var error = string.Join(Environment.NewLine, outcome.Errors);
            await _deliveryStore.MarkManualRequiredAsync(
                next.DeliveryId, error, cancellationToken);
            _manualPending = (await _deliveryStore.GetAsync(
                next.DeliveryId, cancellationToken))!;
            await Dispatcher.BeginInvoke(() => ShowManualRequired(_manualPending));
        }
        await UpdateQueueTextAsync(cancellationToken);
        return outcome.Delivered;
    }

    private async Task OnDeliveryProgressAsync(
        DeliveryProgress progress, CancellationToken cancellationToken)
    {
        if (_deliveryStore is not null)
            await _deliveryStore.MarkAttemptingAsync(
                progress.DeliveryId, progress.Attempt, progress.Refreshed,
                progress.Phase, "", cancellationToken);
        await Dispatcher.BeginInvoke(() =>
        {
            DeliveryPhaseText.Text = progress.Phase;
            ActiveAttemptText.Text = progress.Refreshed
                ? "Attempt: final refresh recovery"
                : $"Attempt: {progress.Attempt}";
            OperationalDetailText.Text = progress.Detail;
            AppendLog($"{progress.DeliveryId}: {progress.Phase} — {progress.Detail}");
        });
    }

    private async void OnRetryDelivery(object sender, RoutedEventArgs e)
    {
        var pending = _manualPending;
        if (pending is null || _deliveryStore is null)
            return;
        SetManualButtons(false);
        await _manualActionGate.WaitAsync();
        try
        {
            if (_manualPending?.DeliveryId != pending.DeliveryId)
                return;
            var outgoing = pending.ToOutgoingMessage();
            await _deliveryStore.ResetQueuedAsync(outgoing.DeliveryId);
            _manualPending = null;
            _incoming?.Writer.TryWrite(outgoing);
            AppendLog($"Operator requested automatic retry for {outgoing.DeliveryId}.");
        }
        catch (Exception exception)
        {
            ShowManualRequired(pending);
            ShowError("Could not queue the automatic retry", exception);
        }
        finally
        {
            _manualActionGate.Release();
        }
    }

    private async void OnManualSend(object sender, RoutedEventArgs e)
    {
        var pending = _manualPending;
        if (pending is null || _deliveryCoordinator is null ||
            _deliveryStore is null || _runCancellation is null)
            return;
        SetManualButtons(false);
        await _manualActionGate.WaitAsync();
        try
        {
            if (_manualPending?.DeliveryId != pending.DeliveryId)
                return;
            SetOperationalState("OPERATOR SEND IN PROGRESS",
                "Checking the target UUID, then clicking Send once.", "#9A3412");
            var delivered = await _deliveryCoordinator.SendManualFallbackAsync(
                pending.ToOutgoingMessage(), _runCancellation.Token);
            if (!delivered)
            {
                ShowManualRequired(pending);
                SetStatus("The operator-controlled send was not verified.");
                return;
            }
            await CompleteManualDeliveryAsync(pending);
        }
        catch (Exception exception)
        {
            if (_manualPending?.DeliveryId == pending.DeliveryId)
                ShowManualRequired(pending);
            ShowError("Operator-controlled send failed", exception);
        }
        finally
        {
            _manualActionGate.Release();
        }
    }

    private async void OnVerifyDelivery(object sender, RoutedEventArgs e)
    {
        var pending = _manualPending;
        if (pending is null || _deliveryCoordinator is null ||
            _runCancellation is null)
            return;
        SetManualButtons(false);
        await _manualActionGate.WaitAsync();
        try
        {
            if (_manualPending?.DeliveryId != pending.DeliveryId)
                return;
            var verified = await _deliveryCoordinator.VerifyManualFallbackAsync(
                pending.ToOutgoingMessage(), _runCancellation.Token);
            if (!verified)
            {
                ShowManualRequired(pending);
                SetStatus("The complete handoff is not visible in the target chat.");
                return;
            }
            await CompleteManualDeliveryAsync(pending);
        }
        catch (Exception exception)
        {
            if (_manualPending?.DeliveryId == pending.DeliveryId)
                ShowManualRequired(pending);
            ShowError("Could not verify browser delivery", exception);
        }
        finally
        {
            _manualActionGate.Release();
        }
    }

    private async Task CompleteManualDeliveryAsync(DeliveryRecord record)
    {
        if (_deliveryStore is null)
            return;
        await _deliveryStore.MarkDeliveredAsync(record.DeliveryId);
        AppendLog($"DELIVERED: manual recovery verified for {record.DeliveryId}.");
        SetJobDelivery(record.JobId, "Delivered and verified");
        _manualPending = null;
        ClearActiveDelivery();
        SetOperationalState("HANDOFF DELIVERED",
            "Manual recovery was verified using the complete message.", "#166534");
        _incoming?.Writer.TryWrite(record.ToOutgoingMessage());
        await UpdateQueueTextAsync();
    }

    private void OnCopyHandoff(object sender, RoutedEventArgs e)
    {
        if (_manualPending is null)
            return;
        Clipboard.SetText(_manualPending.Message);
        SetStatus("Complete handoff copied to the clipboard.");
    }

    private async void OnOpenManagedFirefox(object sender, RoutedEventArgs e)
    {
        if (_browser is null || _runCancellation is null ||
            _automaticDeliveryActive)
            return;
        try
        {
            await _browser.OpenHomeForLoginAsync(_runCancellation.Token);
            SetStatus("Firefox opened. Complete ChatGPT sign-in if needed.");
        }
        catch (Exception exception)
        {
            ShowError("Could not open Firefox", exception);
        }
    }

    private async void OnOpenTarget(object sender, RoutedEventArgs e)
    {
        var uuid = _manualPending?.ConversationUuid ??
            (JobGrid.SelectedItem as JobRow)?.Uuid;
        await OpenUuidAsync(uuid, "initiating");
    }

    private async void OnOpenRecovery(object sender, RoutedEventArgs e)
    {
        var row = JobGrid.SelectedItem as JobRow;
        var job = _manualPending is not null
            ? FindJob(_manualPending.JobId)
            : row is null ? null : FindJob(row.Id);
        await OpenUuidAsync(job?.RecoveryChatUuid, "recovery");
    }

    private async Task OpenUuidAsync(string? uuid, string purpose)
    {
        if (_automaticDeliveryActive)
        {
            SetStatus("Wait for the active automatic delivery to finish.");
            return;
        }
        if (string.IsNullOrWhiteSpace(uuid) || _browser is null ||
            _runCancellation is null)
        {
            SetStatus($"No {purpose} UUID is available.");
            return;
        }
        try
        {
            await _browser.NavigateToConversationAsync(
                uuid, _runCancellation.Token);
            SetStatus($"Opened {purpose} UUID {uuid}. No routing was changed.");
        }
        catch (Exception exception)
        {
            ShowError($"Could not open {purpose} conversation", exception);
        }
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON job ledgers (*.json)|*.json|All files (*.*)|*.*",
            FileName = "watch-config.json",
            CheckFileExists = false
        };
        if (dialog.ShowDialog(this) != true)
            return;
        ConfigPathBox.Text = dialog.FileName;
        await ReloadConfigurationAsync();
    }

    private void OnJobSelectionChanged(
        object sender, SelectionChangedEventArgs e)
        => RefreshNavigationButtons();

    private void RefreshNavigationButtons()
    {
        var job = JobGrid.SelectedItem is JobRow row ? FindJob(row.Id) : null;
        var manualJob = _manualPending is null
            ? null : FindJob(_manualPending.JobId);
        var navigationJob = manualJob ?? job;
        var canNavigate = _browser is not null && !_automaticDeliveryActive;
        LoginButton.IsEnabled = canNavigate;
        OpenTargetButton.IsEnabled = canNavigate && navigationJob is not null;
        OpenRecoveryButton.IsEnabled = canNavigate &&
            !string.IsNullOrWhiteSpace(navigationJob?.RecoveryChatUuid);
    }

    private void RefreshRows()
    {
        var previous = _rows.ToDictionary(
            row => row.Id, StringComparer.OrdinalIgnoreCase);
        var validJobIds = _configuration.Jobs.Select(job => job.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var staleId in _jobStates.Keys
                     .Where(id => !validJobIds.Contains(id)).ToArray())
            _jobStates.Remove(staleId);
        _rows.Clear();
        foreach (var job in _configuration.Jobs)
        {
            previous.TryGetValue(job.Id, out var old);
            _rows.Add(new JobRow(
                job.Id,
                job.InitiatingChatUuid,
                $"{job.Participants.Count} expected",
                old?.State ?? (job.Enabled ? "Waiting" : "Disabled"),
                old?.Delivery ?? "Not queued",
                old?.Detail ?? ""));
        }
    }

    private static string FormatParticipants(
        IReadOnlyList<ParticipantEvaluation> participants)
    {
        if (participants.Count == 0)
            return "—";
        return string.Join(", ", participants.Select(item =>
            $"{item.Participant.Id}: {item.State}"));
    }

    private static string FormatState(JobOperationalState state) => state switch
    {
        JobOperationalState.WaitingForApplications => "Waiting for applications",
        JobOperationalState.ApplicationBlocked => "APPLICATION BLOCKED",
        JobOperationalState.WaitingForExit => "Waiting for shutdown",
        JobOperationalState.ReadyForHandoff => "Ready for handoff",
        JobOperationalState.Disabled => "Disabled",
        _ => "Invalid"
    };

    private TrainingJob? FindJob(string id) =>
        _configuration.Jobs.FirstOrDefault(job =>
            job.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private int FindRow(string id)
    {
        for (var index = 0; index < _rows.Count; index++)
            if (_rows[index].Id.Equals(id,
                    StringComparison.OrdinalIgnoreCase))
                return index;
        return -1;
    }

    private void SetJobDelivery(string jobId, string delivery)
    {
        var index = FindRow(jobId);
        if (index < 0)
            return;
        _rows[index] = _rows[index] with { Delivery = delivery };
    }

    private void ShowActiveDelivery(
        DeliveryRecord record, string phase, string error)
    {
        _automaticDeliveryActive = true;
        DeliveryPhaseText.Text = phase;
        ActiveJobText.Text = $"Job: {record.JobId}";
        ActiveUuidText.Text = $"Initiating UUID: {record.ConversationUuid}";
        ActiveAttemptText.Text = $"Automatic attempts: {record.AutomaticAttempts}";
        ActiveErrorText.Text = error;
        LoginButton.IsEnabled = false;
        OpenTargetButton.IsEnabled = false;
        OpenRecoveryButton.IsEnabled = false;
    }

    private void ShowManualRequired(DeliveryRecord? record)
    {
        if (record is null)
            return;
        ShowActiveDelivery(record, "MANUAL SEND REQUIRED", record.LastError);
        _automaticDeliveryActive = false;
        SetManualButtons(true);
        RefreshNavigationButtons();
        SetJobDelivery(record.JobId, "Manual send required");
        SetOperationalState("MANUAL SEND REQUIRED",
            "Automatic attempts and refresh recovery failed. Review the exact " +
            "initiating UUID, then use SEND NOW or send in Firefox and verify.",
            "#991B1B");
        AppendLog($"MANUAL REQUIRED: {record.DeliveryId} for job {record.JobId}.");
    }

    private void SetManualButtons(bool enabled)
    {
        RetryButton.IsEnabled = enabled;
        ManualSendButton.IsEnabled = enabled;
        VerifyDeliveryButton.IsEnabled = enabled;
        CopyHandoffButton.IsEnabled = enabled;
    }

    private void ClearActiveDelivery()
    {
        _automaticDeliveryActive = false;
        DeliveryPhaseText.Text = "None";
        ActiveJobText.Text = "Job: —";
        ActiveUuidText.Text = "Initiating UUID: —";
        ActiveAttemptText.Text = "Attempt: —";
        ActiveErrorText.Text = "";
        SetManualButtons(false);
        RefreshNavigationButtons();
    }

    private void SetOperationalState(
        string state, string detail, string background)
    {
        OperationalStateText.Text = state;
        OperationalDetailText.Text = detail;
        OperationalBanner.Background =
            (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
                .ConvertFromString(background)!;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private async Task UpdateQueueTextAsync(
        CancellationToken cancellationToken = default)
    {
        if (_deliveryStore is null)
            return;
        var outstanding = await _deliveryStore.GetOutstandingAsync(
            cancellationToken);
        await Dispatcher.BeginInvoke(() =>
            QueueText.Text = $"Outstanding deliveries: {outstanding.Count}");
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(message));
            return;
        }
        LogList.Items.Add($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
        while (LogList.Items.Count > 1000)
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
        _incoming?.Writer.TryComplete();
        if (_monitor is not null)
            await _monitor.DisposeAsync();
        if (_deliveryWorker is not null)
        {
            try { await _deliveryWorker; }
            catch (OperationCanceledException) { }
        }
        await _manualActionGate.WaitAsync();
        _manualActionGate.Release();
        _configurationService?.Dispose();
        if (_browser is not null)
            await Task.Run(_browser.Dispose);
        if (_deliveryStore is not null)
            await _deliveryStore.DisposeAsync();

        _monitor = null;
        _deliveryWorker = null;
        _incoming = null;
        _configurationService = null;
        _browser = null;
        _deliveryCoordinator = null;
        _deliveryStore = null;
        _runCancellation?.Dispose();
        _runCancellation = null;
        _manualPending = null;
        ClearActiveDelivery();
        PauseButton.IsEnabled = false;
        StartButton.IsEnabled = true;
        LoginButton.IsEnabled = false;
        OpenTargetButton.IsEnabled = false;
        OpenRecoveryButton.IsEnabled = false;
        ConfigPathBox.IsEnabled = true;
        QueueText.Text = "Outstanding deliveries: 0";
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownReady)
            return;
        e.Cancel = true;
        if (_closing)
            return;
        _closing = true;
        IsEnabled = false;
        SetStatus("Closing Firefox and preserving delivery state...");
        await _lifecycleGate.WaitAsync();
        try { await StopServicesAsync(); }
        catch (Exception exception)
        {
            AppendLog($"SHUTDOWN ERROR: {exception.Message}");
        }
        finally { _lifecycleGate.Release(); }
        _shutdownReady = true;
        Close();
    }
}

public sealed record JobRow(
    string Id,
    string Uuid,
    string ParticipantSummary,
    string State,
    string Delivery,
    string Detail)
{
    public string ShortUuid => Uuid.Length > 13 ? Uuid[..13] + "…" : Uuid;
}
