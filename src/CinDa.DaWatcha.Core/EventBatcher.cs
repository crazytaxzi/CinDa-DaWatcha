using System.Threading.Channels;

namespace CinDa.DaWatcha.Core;

public sealed record HandoffBatch(
    string ConversationUuid,
    IReadOnlyList<WatchEvent> Events);

public sealed class EventBatcher : IAsyncDisposable
{
    private readonly Channel<WatchEvent> _channel =
        Channel.CreateUnbounded<WatchEvent>();
    private readonly Func<int> _windowMilliseconds;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;

    public EventBatcher(Func<int> windowMilliseconds)
    {
        _windowMilliseconds = windowMilliseconds;
        _worker = RunAsync(_cancellation.Token);
    }

    public event Action<HandoffBatch>? BatchReady;

    public bool Submit(WatchEvent watchEvent) =>
        _channel.Writer.TryWrite(watchEvent);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken))
        {
            if (!_channel.Reader.TryRead(out var first))
                continue;

            var events = new List<WatchEvent> { first };
            var delay = Math.Max(0, _windowMilliseconds());
            if (delay > 0)
                await Task.Delay(delay, cancellationToken);
            while (_channel.Reader.TryRead(out var next))
                events.Add(next);

            foreach (var group in events.GroupBy(
                item => item.Watch.Chat.Uuid,
                StringComparer.OrdinalIgnoreCase))
                BatchReady?.Invoke(new HandoffBatch(
                    group.Key, group.ToArray()));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cancellation.Cancel();
        try { await _worker; }
        catch (OperationCanceledException) { }
        _cancellation.Dispose();
    }
}
