using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CinDa.DaWatcha.App;
using CinDa.DaWatcha.Core;

var driverPath = ReadArgument("--driver") ??
    Environment.GetEnvironmentVariable("GECKODRIVER_PATH") ??
    Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.UserProfile), ".cache", "selenium",
        "geckodriver", "win64", "0.37.1", "geckodriver.exe");
if (!File.Exists(driverPath))
    throw new FileNotFoundException(
        "Pass the local GeckoDriver path with --driver. The smoke test will " +
        "not download it.", driverPath);

var normalUuid = "11111111-2222-3333-4444-555555555555";
var draftUuid = "22222222-3333-4444-5555-666666666666";
var redirectUuid = "33333333-4444-5555-6666-777777777777";
await using var fixture = new BrowserFixture(draftUuid, redirectUuid);
await fixture.StartAsync();

var settings = new AppSettings
{
    PollIntervalMs = 250,
    HeartbeatStaleMs = 60_000,
    ConversationIdlePollMs = 250,
    ConversationIdleStablePolls = 3,
    ConversationIdleTimeoutMs = 10_000,
    SendVerificationTimeoutMs = 5_000,
    AutomaticSendAttempts = 2,
    GeckoDriverPath = Path.GetFullPath(driverPath),
    DeliveryStatePath = Path.Combine(
        Path.GetTempPath(), "CinDa-DaWatcha-BrowserSmoke-delivery-state.json")
};
var deliveryId = HandoffIdentity.Create("browser-smoke|final");
var message = "CinDa-DaWatcha browser verification\n" +
    "Outcome: SUCCESS\n" +
    "Unicode: training complete - alpha Ω\n\n" +
    $"CinDa-DaWatcha-ID: {deliveryId}";

Console.WriteLine("Firefox: standard installed browser window");
Console.WriteLine($"GeckoDriver: {settings.GeckoDriverPath}");
using var browser = new FirefoxChatController(
    () => settings, fixture.Origin);
var coordinator = new HandoffDeliveryCoordinator(browser, () => settings);

await browser.NavigateToConversationAsync(normalUuid);
Assert(!await browser.MessageExistsAsync(normalUuid, message),
    "A marker-only bubble must not count as complete delivery.");

var stopwatch = Stopwatch.StartNew();
var outgoing = new OutgoingMessage(
    deliveryId, "browser-smoke", normalUuid,
    OutgoingMessageKind.FinalHandoff, message, DateTimeOffset.UtcNow);
var outcome = await coordinator.DeliverAutomaticallyAsync(outgoing);
stopwatch.Stop();
Assert(outcome.Delivered, "Automatic browser delivery was not verified.");
Assert(stopwatch.Elapsed >= TimeSpan.FromSeconds(1),
    "Delivery did not wait for the simulated generating state to stop.");
await WaitUntilAsync(() => fixture.SendCount == 1, TimeSpan.FromSeconds(2));
Assert(fixture.SendCount == 1,
    $"Expected exactly one Send click, observed {fixture.SendCount}.");
Assert(await browser.MessageExistsAsync(normalUuid, message),
    "The complete user bubble was not found after Send.");
await browser.PrepareMessageAsync(normalUuid, message);
await browser.SendPreparedMessageAsync(normalUuid, message);
await Task.Delay(100);
Assert(fixture.SendCount == 1,
    "An already delivered message was sent a second time.");
Console.WriteLine("PASS: waited for idle, clicked Send once, and verified " +
    "the complete user bubble without a duplicate re-send.");

await browser.NavigateToConversationAsync(draftUuid);
await AssertThrowsAsync<InvalidOperationException>(() =>
    browser.PrepareMessageAsync(draftUuid, message));
Assert(fixture.SendCount == 1,
    "The unrelated draft path unexpectedly clicked Send.");
Console.WriteLine("PASS: unrelated composer draft was preserved and blocked.");

await AssertThrowsAsync<InvalidOperationException>(() =>
    browser.NavigateToConversationAsync(redirectUuid));
Console.WriteLine("PASS: redirect away from the exact UUID was rejected.");
Console.WriteLine("BROWSER SMOKE PASS");

return;

string? ReadArgument(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException("Browser smoke assertion: " + message);
}

static async Task AssertThrowsAsync<T>(Func<Task> action)
    where T : Exception
{
    try
    {
        await action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException(
        $"Browser smoke assertion: expected {typeof(T).Name}.");
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (condition())
            return;
        await Task.Delay(25);
    }
}

internal sealed class BrowserFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly string _draftUuid;
    private readonly string _redirectUuid;
    private Task? _worker;
    private int _sendCount;

    public BrowserFixture(string draftUuid, string redirectUuid)
    {
        _draftUuid = draftUuid;
        _redirectUuid = redirectUuid;
    }

    public Uri Origin { get; private set; } = null!;
    public int SendCount => Volatile.Read(ref _sendCount);

    public Task StartAsync()
    {
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Origin = new Uri($"http://127.0.0.1:{endpoint.Port}/");
        _worker = AcceptLoopAsync(_cancellation.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            _ = HandleAsync(client, cancellationToken);
        }
    }

    private async Task HandleAsync(
        TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.ASCII,
                   false, 4096, leaveOpen: true))
        {
            var request = await reader.ReadLineAsync(cancellationToken) ?? "";
            string? line;
            do
            {
                line = await reader.ReadLineAsync(cancellationToken);
            } while (!string.IsNullOrEmpty(line));

            var target = request.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ElementAtOrDefault(1) ?? "/";
            if (target.Equals("/sent", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _sendCount);
                await WriteResponseAsync(stream, "200 OK", "text/plain", "ok",
                    "", cancellationToken);
                return;
            }
            if (target.Equals($"/c/{_redirectUuid}",
                    StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, "302 Found", "text/plain", "",
                    "Location: /wrong\r\n", cancellationToken);
                return;
            }

            var draft = target.Equals($"/c/{_draftUuid}",
                StringComparison.OrdinalIgnoreCase)
                ? "operator draft - do not overwrite" : "";
            var body = Page(draft);
            await WriteResponseAsync(stream, "200 OK", "text/html; charset=utf-8",
                body, "", cancellationToken);
        }
    }

    private static string Page(string draft)
    {
        var encodedDraft = WebUtility.HtmlEncode(draft);
        return $$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>Browser fixture</title></head>
            <body>
              <main>
                <div data-message-author-role="user">CinDa-DaWatcha-ID: marker-only</div>
                <div data-message-author-role="assistant" id="assistant">Working</div>
                <button data-testid="stop-button" id="stop">Stop generating</button>
                <div id="prompt-textarea" contenteditable="true" role="textbox">{{encodedDraft}}</div>
                <button data-testid="send-button" aria-label="Send prompt" id="send" disabled>Send</button>
              </main>
              <script>
                const composer = document.getElementById('prompt-textarea');
                const send = document.getElementById('send');
                const update = () => send.disabled = !composer.innerText.trim();
                composer.addEventListener('input', update);
                update();
                send.addEventListener('click', () => {
                  if (send.disabled) return;
                  const bubble = document.createElement('div');
                  bubble.setAttribute('data-message-author-role', 'user');
                  bubble.innerText = composer.innerText;
                  document.querySelector('main').insertBefore(bubble, composer);
                  composer.replaceChildren();
                  update();
                  fetch('/sent', { method: 'POST' }).catch(() => {});
                });
                setTimeout(() => {
                  document.getElementById('assistant').innerText = 'Finished';
                  document.getElementById('stop')?.remove();
                }, 1250);
              </script>
            </body></html>
            """;
    }

    private static async Task WriteResponseAsync(
        Stream stream, string status, string contentType, string body,
        string extraHeaders, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\n" +
            $"Content-Length: {bytes.Length}\r\n{extraHeaders}" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _listener.Stop();
        if (_worker is not null)
        {
            try { await _worker; }
            catch (SocketException) { }
        }
        _cancellation.Dispose();
    }
}
