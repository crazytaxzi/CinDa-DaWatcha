using System.Diagnostics;
using System.Text.Json;
using CinDa.DaWatcha.Core;
using Json.Schema;

namespace CinDa.DaWatcha.Core.Tests;

public sealed class CoreBehaviorTests
{
    [Fact]
    public void Validator_rejects_duplicate_watch_ids()
    {
        var config = CreateConfiguration();
        config.Watches.Add(CloneWatch(config.Watches[0]));

        var errors = ConfigurationValidator.Validate(config);

        Assert.Contains(errors, error =>
            error.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Message_builder_combines_all_events_for_a_uuid()
    {
        var config = CreateConfiguration();
        var second = CloneWatch(config.Watches[0]);
        second.Id = "worker-02";
        second.PassalongMessage = "Review the second worker.";
        var batch = new HandoffBatch(
            config.Watches[0].Chat.Uuid,
            [
                new(config.Watches[0], WatchTriggerKind.Completion,
                    DateTimeOffset.UtcNow, "Done"),
                new(second, WatchTriggerKind.Exit,
                    DateTimeOffset.UtcNow, "Exited")
            ]);

        var message = HandoffMessageBuilder.Build(batch);

        Assert.Contains("worker-01", message);
        Assert.Contains("worker-02", message);
        Assert.Contains("Review the second worker.", message);
        Assert.Matches(@"CinDa-DaWatcha-ID: [0-9a-f]{32}", message);
    }

    [Fact]
    public async Task Uuid_update_replaces_every_matching_record()
    {
        var directory = Directory.CreateTempSubdirectory("dawatcha-");
        try
        {
            var path = Path.Combine(directory.FullName, "watch-config.json");
            var config = CreateConfiguration();
            var duplicateTarget = CloneWatch(config.Watches[0]);
            duplicateTarget.Id = "worker-02";
            config.Watches.Add(duplicateTarget);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(
                config, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));

            using var service = new WatchConfigurationService(path);
            var newUuid = Guid.NewGuid().ToString();
            var updated = await service.UpdateUuidAsync(
                config.Watches[0].Chat.Uuid, newUuid);

            Assert.All(updated.Watches,
                watch => Assert.Equal(newUuid, watch.Chat.Uuid));
            Assert.DoesNotContain(config.Watches[0].Chat.Uuid,
                await File.ReadAllTextAsync(path));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void Example_matches_schema_and_runtime_model()
    {
        var schemaText = File.ReadAllText(
            FindRepositoryFile(@"docs\watch-config.schema.json"));
        var exampleText = File.ReadAllText(
            FindRepositoryFile("watch-config.example.json"));

        var schema = JsonSchema.FromText(schemaText);
        using var instance = JsonDocument.Parse(exampleText);
        var result = schema.Evaluate(instance.RootElement);
        Assert.True(result.IsValid, result.ToString());

        var config = JsonSerializer.Deserialize<WatchConfiguration>(
            exampleText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        Assert.NotNull(config);
        Assert.Empty(ConfigurationValidator.Validate(config));
    }

    [Fact]
    public async Task Monitor_emits_only_one_completion_per_process_run()
    {
        using var process = Process.GetCurrentProcess();
        var watch = CreateWatch();
        watch.Process = new ProcessFingerprint
        {
            Pid = process.Id,
            Name = process.ProcessName,
            ExecutablePath = process.MainModule!.FileName,
            StartTimeUtc = process.StartTime.ToUniversalTime()
        };
        var config = new WatchConfiguration
        {
            Settings = new AppSettings { CompletionStablePolls = 2 },
            Watches = [watch]
        };
        var events = new List<WatchEvent>();
        await using var monitor = new ProcessMonitor(
            () => config, new AlwaysCompleteDetector());
        monitor.Triggered += events.Add;

        await monitor.PollOnceAsync(config);
        await monitor.PollOnceAsync(config);
        await monitor.PollOnceAsync(config);

        Assert.Single(events);
        Assert.Equal(WatchTriggerKind.Completion, events[0].Trigger);
    }

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

    private static WatchConfiguration CreateConfiguration() =>
        new() { Watches = [CreateWatch()] };

    private static WatchItem CreateWatch() =>
        new()
        {
            Id = "worker-01",
            Process = new ProcessFingerprint
            {
                Pid = 1234,
                Name = "worker.exe",
                ExecutablePath = @"C:\Apps\worker.exe",
                StartTimeUtc = DateTimeOffset.UtcNow
            },
            Completion = new CompletionRule(),
            Chat = new ChatTarget { Uuid = Guid.NewGuid().ToString() },
            PassalongMessage = "Review the completed worker."
        };

    private static WatchItem CloneWatch(WatchItem source) =>
        new()
        {
            Id = source.Id,
            Enabled = source.Enabled,
            Process = new ProcessFingerprint
            {
                Pid = source.Process.Pid,
                Name = source.Process.Name,
                ExecutablePath = source.Process.ExecutablePath,
                StartTimeUtc = source.Process.StartTimeUtc
            },
            Completion = new CompletionRule
            {
                Method = source.Completion.Method,
                Patterns = [.. source.Completion.Patterns],
                WindowTitlePattern = source.Completion.WindowTitlePattern
            },
            Chat = new ChatTarget { Uuid = source.Chat.Uuid },
            PassalongMessage = source.PassalongMessage
        };

    private sealed class AlwaysCompleteDetector : ICompletionDetector
    {
        public Task<bool> IsCompleteAsync(
            WatchItem watch, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
