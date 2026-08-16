using System.Text.Json;
using System.Globalization;
using CinDa.DaWatcha.Core;
using Json.Schema;

namespace CinDa.DaWatcha.Core.Tests;

public sealed class CoreBehaviorTests
{
    [Fact]
    public void Validator_rejects_duplicate_jobs_and_noncanonical_routes()
    {
        var config = CreateConfiguration();
        var duplicate = CreateJob();
        duplicate.Id = config.Jobs[0].Id.ToUpperInvariant();
        duplicate.InitiatingChatUuid = Guid.NewGuid().ToString("N");
        config.Jobs.Add(duplicate);

        var errors = ConfigurationValidator.Validate(config);

        Assert.Contains(errors, error =>
            error.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error =>
            error.Contains("canonical", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_reports_explicit_nulls_without_crashing()
    {
        var missingSettings = CreateConfiguration();
        missingSettings.Settings = null!;
        var nullParticipant = CreateConfiguration();
        nullParticipant.Jobs[0].Participants.Add(null!);

        Assert.Contains(ConfigurationValidator.Validate(missingSettings),
            error => error.Contains("settings cannot be null",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ConfigurationValidator.Validate(nullParticipant),
            error => error.Contains("null entries",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_rejects_empty_uuid_and_duplicate_process_fingerprint()
    {
        var config = CreateConfiguration();
        var participant = config.Jobs[0].Participants[0];
        config.Jobs[0].InitiatingChatUuid = Guid.Empty.ToString("D");
        config.Jobs[0].Participants.Add(new JobParticipant
        {
            Id = "different-logical-name",
            Process = participant.Process,
            State = ParticipantState.Running,
            UpdatedAtUtc = participant.UpdatedAtUtc,
            HeartbeatUtc = participant.HeartbeatUtc,
            Detail = "Duplicate",
            HandoffMessage = ""
        });

        var errors = ConfigurationValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("UUID",
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("fingerprint",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Json_converter_requires_literal_utc_z_timestamp()
    {
        var json = JsonSerializer.Serialize(CreateConfiguration(),
            ConfigurationJson.CreateOptions());
        var nonCanonical = json.Replace("Z\"", "+00:00\"",
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<WatchConfiguration>(nonCanonical,
                ConfigurationJson.CreateOptions()));
    }

    [Fact]
    public void Json_converter_requires_exact_participant_state_spelling()
    {
        var json = JsonSerializer.Serialize(CreateConfiguration(),
            ConfigurationJson.CreateOptions());
        var wrongCase = json.Replace("\"Running\"", "\"running\"",
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<WatchConfiguration>(wrongCase,
                ConfigurationJson.CreateOptions()));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"surprise\":true}")]
    [InlineData("{\"settings\":{},\"settings\":{},\"jobs\":[]}")]
    public async Task Loader_rejects_missing_unknown_and_duplicate_properties(
        string json)
    {
        var directory = Directory.CreateTempSubdirectory("dawatcha-strict-");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var path = Path.Combine(directory.FullName, "watch-config.json");
            await File.WriteAllTextAsync(path, json, cancellationToken);
            using var service = new WatchConfigurationService(path);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.LoadAsync(cancellationToken));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Watcher_accepts_an_external_atomic_replacement()
    {
        var directory = Directory.CreateTempSubdirectory("dawatcha-watch-");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var path = Path.Combine(directory.FullName, "watch-config.json");
            var options = ConfigurationJson.CreateOptions(writeIndented: true);
            var initial = CreateLedgerConfiguration();
            await File.WriteAllTextAsync(path,
                JsonSerializer.Serialize(initial, options), cancellationToken);
            using var service = new WatchConfigurationService(path);
            _ = await service.LoadAsync(cancellationToken);
            var received = new TaskCompletionSource<WatchConfiguration>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.ConfigurationChanged += config => received.TrySetResult(config);
            service.StartWatching();

            var changed = CreateLedgerConfiguration();
            changed.Jobs[0].Id = initial.Jobs[0].Id;
            changed.Jobs[0].InitiatingChatUuid =
                initial.Jobs[0].InitiatingChatUuid;
            changed.Jobs[0].Summary = "Atomic external update received";
            var temp = Path.Combine(directory.FullName, "replacement.tmp");
            await File.WriteAllTextAsync(temp,
                JsonSerializer.Serialize(changed, options), cancellationToken);
            File.Move(temp, path, true);

            var reloaded = await received.Task.WaitAsync(
                TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal("Atomic external update received",
                reloaded.Jobs[0].Summary);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Loader_resolves_every_configured_path_from_ledger_root()
    {
        var directory = Directory.CreateTempSubdirectory("dawatcha-root-");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var path = Path.Combine(directory.FullName, "watch-config.json");
            var config = CreateLedgerConfiguration();
            config.Settings.GeckoDriverPath = @".\tools\geckodriver.exe";
            config.Settings.DeliveryStatePath = @".\state\delivery.json";
            config.Jobs[0].Participants[0].Process.ExecutablePath =
                @".\apps\trainer.exe";
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config,
                ConfigurationJson.CreateOptions()), cancellationToken);

            using var service = new WatchConfigurationService(path);
            var loaded = await service.LoadAsync(cancellationToken);

            Assert.Equal(Path.Combine(directory.FullName, "tools",
                    "geckodriver.exe"),
                loaded.Settings.GeckoDriverPath);
            Assert.Equal(Path.Combine(directory.FullName, "state",
                    "delivery.json"),
                loaded.Settings.DeliveryStatePath);
            Assert.Equal(Path.Combine(directory.FullName, "apps", "trainer.exe"),
                loaded.Jobs[0].Participants[0].Process.ExecutablePath);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData(@"C:\outside\geckodriver.exe")]
    [InlineData(@".\..\geckodriver.exe")]
    [InlineData(@"tools\geckodriver.exe")]
    [InlineData(@".\gecko*.exe")]
    public async Task Loader_rejects_paths_not_rooted_safely(string driverPath)
    {
        var directory = Directory.CreateTempSubdirectory("dawatcha-root-");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var path = Path.Combine(directory.FullName, "watch-config.json");
            var config = CreateLedgerConfiguration();
            config.Settings.GeckoDriverPath = driverPath;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config,
                ConfigurationJson.CreateOptions()), cancellationToken);

            using var service = new WatchConfigurationService(path);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.LoadAsync(cancellationToken));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void Evaluator_waits_until_every_application_is_terminal_and_stopped()
    {
        var job = CreateJob(twoParticipants: true);
        job.Participants[0].State = ParticipantState.Succeeded;
        job.Participants[0].FinishedAtUtc = DateTimeOffset.UtcNow;
        job.Participants[0].HandoffMessage = "First trainer completed.";
        job.Participants[1].State = ParticipantState.Running;
        var inspector = new FakeProcessInspector();
        inspector.Set(job.Participants[0].Process.Pid, NotRunning());
        inspector.Set(job.Participants[1].Process.Pid, Running());
        var evaluator = new JobEvaluator(inspector);

        var evaluation = evaluator.Evaluate(job, CreateSettings());

        Assert.Equal(JobOperationalState.WaitingForApplications,
            evaluation.State);
        Assert.Empty(evaluation.OutgoingMessages);
    }

    [Fact]
    public void Evaluator_emits_one_blocked_warning_to_the_initiating_uuid()
    {
        var now = DateTimeOffset.UtcNow;
        var job = CreateJob();
        job.Participants[0].State = ParticipantState.Running;
        job.Participants[0].HeartbeatUtc = now.AddMinutes(-10);
        var inspector = new FakeProcessInspector();
        inspector.Set(job.Participants[0].Process.Pid, Running());
        var evaluator = new JobEvaluator(inspector, () => now);

        var evaluation = evaluator.Evaluate(job, CreateSettings());

        Assert.Equal(JobOperationalState.ApplicationBlocked, evaluation.State);
        var warning = Assert.Single(evaluation.OutgoingMessages);
        Assert.Equal(OutgoingMessageKind.BlockedWarning, warning.Kind);
        Assert.Equal(job.InitiatingChatUuid, warning.ConversationUuid);
        Assert.Contains("Heartbeat is stale", warning.Message);
        Assert.Contains($"CinDa-DaWatcha-ID: {warning.DeliveryId}",
            warning.Message);
    }

    [Fact]
    public void Blocked_warning_is_stable_across_polls_and_restarts()
    {
        var now = DateTimeOffset.UtcNow;
        var job = CreateJob();
        job.Participants[0].HeartbeatUtc = now.AddMinutes(-10);
        var inspector = new FakeProcessInspector();
        inspector.Set(job.Participants[0].Process.Pid, Running());

        var first = new JobEvaluator(inspector, () => now)
            .Evaluate(job, CreateSettings());
        var later = new JobEvaluator(inspector, () => now.AddHours(1))
            .Evaluate(job, CreateSettings());

        var firstWarning = Assert.Single(first.OutgoingMessages);
        var laterWarning = Assert.Single(later.OutgoingMessages);
        Assert.Equal(firstWarning.DeliveryId, laterWarning.DeliveryId);
        Assert.Equal(firstWarning.Message, laterWarning.Message);
    }

    [Fact]
    public void Monitor_emits_each_warning_once_then_allows_the_final_handoff()
    {
        var now = DateTimeOffset.UtcNow;
        var job = CreateJob();
        job.Participants[0].HeartbeatUtc = now.AddMinutes(-10);
        var config = new WatchConfiguration
        {
            Settings = CreateSettings(),
            Jobs = [job]
        };
        var inspector = new FakeProcessInspector();
        inspector.Set(job.Participants[0].Process.Pid, Running());
        var monitor = new JobMonitor(() => config,
            new JobEvaluator(inspector, () => now));
        var emitted = new List<OutgoingMessage>();
        monitor.DeliveryRequested += emitted.Add;

        monitor.PollOnce(config);
        monitor.PollOnce(config);
        job.Participants[0].State = ParticipantState.Succeeded;
        job.Participants[0].FinishedAtUtc = now;
        job.Participants[0].HandoffMessage = "Checkpoint ready.";
        inspector.Set(job.Participants[0].Process.Pid, NotRunning());
        monitor.PollOnce(config);
        monitor.PollOnce(config);

        Assert.Equal(2, emitted.Count);
        Assert.Equal(OutgoingMessageKind.BlockedWarning, emitted[0].Kind);
        Assert.Equal(OutgoingMessageKind.FinalHandoff, emitted[1].Kind);
        Assert.NotEqual(emitted[0].DeliveryId, emitted[1].DeliveryId);
        Assert.All(emitted, message =>
            Assert.Equal(job.InitiatingChatUuid, message.ConversationUuid));
    }

    [Fact]
    public void Pending_application_waits_for_start_then_detects_early_exit()
    {
        var job = CreateJob();
        job.Participants[0].State = ParticipantState.Pending;
        var inspector = new FakeProcessInspector();
        var evaluator = new JobEvaluator(inspector);

        var beforeStart = evaluator.Evaluate(job, CreateSettings());
        inspector.Set(job.Participants[0].Process.Pid, Running());
        var running = evaluator.Evaluate(job, CreateSettings());
        inspector.Set(job.Participants[0].Process.Pid, NotRunning());
        var exited = evaluator.Evaluate(job, CreateSettings());

        Assert.Equal(JobOperationalState.WaitingForApplications,
            beforeStart.State);
        Assert.Empty(beforeStart.OutgoingMessages);
        Assert.Equal(JobOperationalState.WaitingForApplications, running.State);
        Assert.Equal(JobOperationalState.ReadyForHandoff, exited.State);
        Assert.Contains("Outcome: FAILURE",
            Assert.Single(exited.OutgoingMessages).Message);
    }

    [Fact]
    public void Indeterminate_live_process_prevents_final_handoff()
    {
        var job = CreateJob();
        job.Participants[0].State = ParticipantState.Succeeded;
        job.Participants[0].FinishedAtUtc = DateTimeOffset.UtcNow;
        job.Participants[0].HandoffMessage = "Output ready.";
        var inspector = new FakeProcessInspector();
        inspector.Set(job.Participants[0].Process.Pid,
            new ProcessInspection(true, false, null,
                "Access denied.", IdentityIndeterminate: true));

        var evaluation = new JobEvaluator(inspector)
            .Evaluate(job, CreateSettings());

        Assert.Equal(JobOperationalState.ApplicationBlocked, evaluation.State);
        Assert.Single(evaluation.OutgoingMessages);
        Assert.DoesNotContain(evaluation.OutgoingMessages,
            message => message.Kind == OutgoingMessageKind.FinalHandoff);
    }

    [Fact]
    public void Evaluator_builds_one_final_failure_when_process_exits_without_handoff()
    {
        var job = CreateJob(twoParticipants: true);
        foreach (var participant in job.Participants)
        {
            participant.State = ParticipantState.Running;
            participant.HeartbeatUtc = DateTimeOffset.UtcNow;
        }
        var inspector = new FakeProcessInspector();
        foreach (var participant in job.Participants)
            inspector.Set(participant.Process.Pid, NotRunning());
        var evaluator = new JobEvaluator(inspector);

        var first = evaluator.Evaluate(job, CreateSettings());
        var second = evaluator.Evaluate(job, CreateSettings());

        Assert.Equal(JobOperationalState.ReadyForHandoff, first.State);
        var handoff = Assert.Single(first.OutgoingMessages);
        Assert.Equal(OutgoingMessageKind.FinalHandoff, handoff.Kind);
        Assert.Contains("Outcome: FAILURE", handoff.Message);
        Assert.Contains("before a terminal handoff", handoff.Message);
        Assert.Equal(handoff.DeliveryId,
            Assert.Single(second.OutgoingMessages).DeliveryId);
    }

    [Fact]
    public void Evaluator_builds_success_only_after_terminal_processes_exit()
    {
        var job = CreateJob(twoParticipants: true);
        foreach (var participant in job.Participants)
        {
            participant.State = ParticipantState.Succeeded;
            participant.FinishedAtUtc = DateTimeOffset.UtcNow;
            participant.HandoffMessage = $"{participant.Id} output is ready.";
        }
        var inspector = new FakeProcessInspector();
        inspector.Set(job.Participants[0].Process.Pid, NotRunning());
        inspector.Set(job.Participants[1].Process.Pid, Running());
        var evaluator = new JobEvaluator(inspector);

        var waiting = evaluator.Evaluate(job, CreateSettings());
        inspector.Set(job.Participants[1].Process.Pid, NotRunning());
        var ready = evaluator.Evaluate(job, CreateSettings());

        Assert.Equal(JobOperationalState.WaitingForExit, waiting.State);
        Assert.Empty(waiting.OutgoingMessages);
        Assert.Equal(JobOperationalState.ReadyForHandoff, ready.State);
        var handoff = Assert.Single(ready.OutgoingMessages);
        Assert.Contains("Outcome: SUCCESS", handoff.Message);
        Assert.Contains("trainer-01 output is ready", handoff.Message);
        Assert.Contains("trainer-02 output is ready", handoff.Message);
    }

    [Fact]
    public void Final_handoff_content_is_stable_across_restarts()
    {
        var job = CreateJob();
        job.Participants[0].State = ParticipantState.Succeeded;
        job.Participants[0].FinishedAtUtc = DateTimeOffset.UtcNow;
        job.Participants[0].HandoffMessage = "Checkpoint ready.";
        var inspector = new FakeProcessInspector();
        inspector.Set(job.Participants[0].Process.Pid, NotRunning());

        var first = new JobEvaluator(inspector,
            () => DateTimeOffset.UtcNow).Evaluate(job, CreateSettings());
        var second = new JobEvaluator(inspector,
            () => DateTimeOffset.UtcNow.AddDays(1)).Evaluate(job, CreateSettings());

        var firstMessage = Assert.Single(first.OutgoingMessages);
        var secondMessage = Assert.Single(second.OutgoingMessages);
        Assert.Equal(firstMessage.DeliveryId, secondMessage.DeliveryId);
        Assert.Equal(firstMessage.Message, secondMessage.Message);
    }

    [Fact]
    public void Final_handoff_content_is_stable_across_machine_cultures()
    {
        var job = CreateJob();
        job.Participants[0].State = ParticipantState.Succeeded;
        job.Participants[0].FinishedAtUtc = DateTimeOffset.UtcNow;
        job.Participants[0].ExitCode = 1234;
        job.Participants[0].HandoffMessage = "Checkpoint ready.";
        var inspector = new FakeProcessInspector();
        inspector.Set(job.Participants[0].Process.Pid, NotRunning());
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG");
            var first = new JobEvaluator(inspector).Evaluate(job,
                CreateSettings());
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var second = new JobEvaluator(inspector).Evaluate(job,
                CreateSettings());

            Assert.Equal(Assert.Single(first.OutgoingMessages).Message,
                Assert.Single(second.OutgoingMessages).Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task Delivery_store_persists_confirmation_and_freezes_job_route()
    {
        var directory = Directory.CreateTempSubdirectory("dawatcha-state-");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var path = Path.Combine(directory.FullName, "delivery-state.json");
            var message = CreateOutgoing();
            await using (var store = new DeliveryStateStore(path))
            {
                await store.InitializeAsync(cancellationToken);
                Assert.Equal(EnqueueDisposition.Added,
                    await store.EnqueueAsync(message, cancellationToken));
                await store.MarkDeliveredAsync(message.DeliveryId,
                    cancellationToken);
            }

            await using var reopened = new DeliveryStateStore(path);
            await reopened.InitializeAsync(cancellationToken);
            Assert.Equal(EnqueueDisposition.AlreadyDelivered,
                await reopened.EnqueueAsync(message, cancellationToken));
            var rerouted = message with
            {
                DeliveryId = HandoffIdentity.Create("different"),
                ConversationUuid = Guid.NewGuid().ToString("D")
            };
            Assert.Equal(EnqueueDisposition.RouteConflict,
                await reopened.EnqueueAsync(rerouted, cancellationToken));
            Assert.Empty(await reopened.GetOutstandingAsync(cancellationToken));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Delivery_store_quarantines_changed_content_for_same_id()
    {
        var directory = Directory.CreateTempSubdirectory("dawatcha-state-");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var path = Path.Combine(directory.FullName, "delivery-state.json");
            var message = CreateOutgoing();
            await using var store = new DeliveryStateStore(path);
            await store.InitializeAsync(cancellationToken);
            Assert.Equal(EnqueueDisposition.Added,
                await store.EnqueueAsync(message, cancellationToken));

            Assert.Equal(EnqueueDisposition.PayloadConflict,
                await store.EnqueueAsync(message with { Message = "changed" },
                    cancellationToken));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Delivery_store_rejects_on_disk_content_tampering()
    {
        var directory = Directory.CreateTempSubdirectory("dawatcha-state-");
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var path = Path.Combine(directory.FullName, "delivery-state.json");
            var message = CreateOutgoing();
            await using (var store = new DeliveryStateStore(path))
            {
                await store.InitializeAsync(cancellationToken);
                await store.EnqueueAsync(message, cancellationToken);
            }
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            content = content.Replace("Completed", "Tampered",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, content, cancellationToken);

            await using var reopened = new DeliveryStateStore(path);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                reopened.InitializeAsync(cancellationToken));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Coordinator_does_not_duplicate_after_ambiguous_send_error()
    {
        var browser = new FakeChatBrowser
        {
            ThrowAfterFirstSuccessfulSend = true
        };
        var settings = CreateSettings();
        settings.AutomaticSendAttempts = 3;
        var coordinator = new HandoffDeliveryCoordinator(browser, () => settings);

        var result = await coordinator.DeliverAutomaticallyAsync(
            CreateOutgoing(), cancellationToken:
            TestContext.Current.CancellationToken);

        Assert.True(result.Delivered);
        Assert.False(result.ManualSendRequired);
        Assert.Equal(1, browser.SendCalls);
        Assert.True(browser.MessageExists);
    }

    [Fact]
    public async Task Coordinator_refreshes_once_then_requires_manual_send()
    {
        var browser = new FakeChatBrowser { AlwaysFailSend = true };
        var settings = CreateSettings();
        settings.AutomaticSendAttempts = 2;
        var coordinator = new HandoffDeliveryCoordinator(browser, () => settings);

        var result = await coordinator.DeliverAutomaticallyAsync(
            CreateOutgoing(), cancellationToken:
            TestContext.Current.CancellationToken);

        Assert.False(result.Delivered);
        Assert.True(result.ManualSendRequired);
        Assert.True(result.RefreshAttempted);
        Assert.Equal(3, browser.SendCalls);
        Assert.Equal(1, browser.RefreshCalls);
        Assert.Equal(3, result.Errors.Count);
    }

    [Fact]
    public async Task Coordinator_manual_fallback_sends_and_verifies_once()
    {
        var browser = new FakeChatBrowser();
        var coordinator = new HandoffDeliveryCoordinator(
            browser, CreateSettings);

        var delivered = await coordinator.SendManualFallbackAsync(
            CreateOutgoing(), TestContext.Current.CancellationToken);

        Assert.True(delivered);
        Assert.Equal(1, browser.SendCalls);
    }

    [Fact]
    public void Example_matches_schema_and_runtime_model()
    {
        var schemaText = File.ReadAllText(
            FindRepositoryFile(@"docs\watch-config.schema.json"));
        var examplePath = FindRepositoryFile("watch-config.example.json");
        var exampleText = File.ReadAllText(examplePath);
        var schema = JsonSchema.FromText(schemaText);
        using var instance = JsonDocument.Parse(exampleText);
        var result = schema.Evaluate(instance.RootElement);
        Assert.True(result.IsValid, result.ToString());

        var config = JsonSerializer.Deserialize<WatchConfiguration>(
            exampleText, ConfigurationJson.CreateOptions());
        Assert.NotNull(config);
        ConfigurationPathResolver.ResolveRelativePaths(config, examplePath);
        var errors = ConfigurationValidator.Validate(config);
        Assert.Empty(errors);
    }

    private static WatchConfiguration CreateConfiguration() => new()
    {
        Settings = CreateSettings(),
        Jobs = [CreateJob()]
    };

    private static AppSettings CreateSettings() => new()
    {
        PollIntervalMs = 250,
        HeartbeatStaleMs = 60_000,
        ConversationIdlePollMs = 250,
        ConversationIdleStablePolls = 2,
        ConversationIdleTimeoutMs = 10_000,
        SendVerificationTimeoutMs = 5_000,
        AutomaticSendAttempts = 3,
        GeckoDriverPath = @"C:\CinDa-Test\geckodriver.exe",
        DeliveryStatePath = @"C:\CinDa-Test\delivery-state.json"
    };

    private static WatchConfiguration CreateLedgerConfiguration()
    {
        var configuration = CreateConfiguration();
        configuration.Settings.GeckoDriverPath = @".\geckodriver.exe";
        configuration.Settings.DeliveryStatePath = @".\delivery-state.json";
        foreach (var participant in configuration.Jobs.SelectMany(
                     job => job.Participants))
            participant.Process.ExecutablePath = @".\trainer.exe";
        return configuration;
    }

    private static TrainingJob CreateJob(bool twoParticipants = false)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new TrainingJob
        {
            Id = "training-01",
            Enabled = true,
            InitiatingChatUuid = Guid.NewGuid().ToString("D"),
            CreatedAtUtc = now.AddMinutes(-5),
            UpdatedAtUtc = now,
            Summary = "Local model training run",
            Participants = [CreateParticipant("trainer-01", 1001, now)]
        };
        if (twoParticipants)
            job.Participants.Add(CreateParticipant("trainer-02", 1002, now));
        return job;
    }

    private static JobParticipant CreateParticipant(
        string id, int pid, DateTimeOffset now) =>
        new()
        {
            Id = id,
            Process = new ProcessFingerprint
            {
                Pid = pid,
                Name = "trainer.exe",
                ExecutablePath = @"C:\Training\trainer.exe",
                StartTimeUtc = now.AddMinutes(-5)
            },
            State = ParticipantState.Running,
            UpdatedAtUtc = now,
            HeartbeatUtc = now,
            Detail = "Training",
            HandoffMessage = ""
        };

    private static OutgoingMessage CreateOutgoing()
    {
        var id = HandoffIdentity.Create("job|final");
        return new OutgoingMessage(
            id, "training-01", Guid.NewGuid().ToString("D"),
            OutgoingMessageKind.FinalHandoff,
            $"Completed\n\nCinDa-DaWatcha-ID: {id}",
            DateTimeOffset.UtcNow);
    }

    private static ProcessInspection Running() =>
        new(true, true, true, "Fingerprint verified.");

    private static ProcessInspection NotRunning() =>
        new(false, false, null, "PID is not running.");

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException(
            $"Could not find repository file: {relativePath}");
    }

    private sealed class FakeProcessInspector : IProcessInspector
    {
        private readonly Dictionary<int, ProcessInspection> _inspections = [];

        public void Set(int pid, ProcessInspection inspection) =>
            _inspections[pid] = inspection;

        public ProcessInspection Inspect(ProcessFingerprint expected) =>
            _inspections.TryGetValue(expected.Pid, out var inspection)
                ? inspection : NotRunning();
    }

    private sealed class FakeChatBrowser : IChatBrowserDelivery
    {
        public bool MessageExists { get; private set; }
        public bool ThrowAfterFirstSuccessfulSend { get; init; }
        public bool AlwaysFailSend { get; init; }
        public int SendCalls { get; private set; }
        public int RefreshCalls { get; private set; }

        public Task NavigateToConversationAsync(
            string uuid, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RefreshConversationAsync(
            string uuid, CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.CompletedTask;
        }

        public Task WaitForConversationIdleAsync(
            string uuid, string? allowedComposerText, AppSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> MessageExistsAsync(
            string uuid, string expectedMessage,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MessageExists);

        public Task PrepareMessageAsync(
            string uuid, string message,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendPreparedMessageAsync(
            string uuid, string expectedMessage,
            CancellationToken cancellationToken = default)
        {
            SendCalls++;
            if (AlwaysFailSend)
                throw new InvalidOperationException("simulated send failure");
            MessageExists = true;
            if (ThrowAfterFirstSuccessfulSend && SendCalls == 1)
                throw new InvalidOperationException(
                    "simulated WebDriver failure after accepted click");
            return Task.CompletedTask;
        }

        public Task<bool> VerifyDeliveredAsync(
            string uuid, string expectedMessage, TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MessageExists);
    }
}
