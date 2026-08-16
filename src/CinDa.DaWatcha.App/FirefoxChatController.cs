using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CinDa.DaWatcha.Core;
using OpenQA.Selenium;
using OpenQA.Selenium.Firefox;

namespace CinDa.DaWatcha.App;

public sealed record SendVerification(
    bool ComposerEmpty,
    bool SendButtonNotReady,
    bool UserMessageVisible)
{
    public bool BrowserSignalsSatisfied =>
        ComposerEmpty && SendButtonNotReady && UserMessageVisible;
}

public sealed class BrowserLoginRequiredException(string message)
    : InvalidOperationException(message);

public sealed class FirefoxChatController : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FirefoxDriver? _driver;
    private string? _primaryHandle;

    public FirefoxChatController(Func<AppSettings> settings)
    {
        _settings = settings;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(driver => { }, cancellationToken);

    public Task OpenHomeForLoginAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(driver =>
        {
            driver.Navigate().GoToUrl(_settings().ChatBaseUrl.TrimEnd('/') + "/");
            WaitForDocument(driver, cancellationToken);
            EnsureSingleTab(driver);
        }, cancellationToken);

    public Task NavigateToConversationAsync(
        string uuid, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(uuid, out _))
            throw new ArgumentException("Conversation ID is not a UUID.");

        return ExecuteAsync(driver =>
        {
            var url = _settings().ChatBaseUrl.TrimEnd('/') + "/c/" + uuid;
            driver.Navigate().GoToUrl(url);
            WaitForDocument(driver, cancellationToken);
            EnsureSingleTab(driver);
            EnsureComposer(driver, cancellationToken);
        }, cancellationToken);
    }

    public Task OpenNewConversationAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(driver =>
        {
            driver.Navigate().GoToUrl(_settings().ChatBaseUrl.TrimEnd('/') + "/");
            WaitForDocument(driver, cancellationToken);
            EnsureSingleTab(driver);
            EnsureComposer(driver, cancellationToken);
        }, cancellationToken);

    public Task<long> GetVisibleConversationBytesAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(driver =>
        {
            const string script = """
                const visible = e => {
                  const s = getComputedStyle(e);
                  return s.visibility !== 'hidden' &&
                    s.display !== 'none' && e.getClientRects().length > 0;
                };
                const turns = [...document.querySelectorAll(
                  "main [data-testid^='conversation-turn-']")].filter(visible);
                if (turns.length)
                  return turns.map(x => x.innerText || '').join('\n\n');
                return document.querySelector('main')?.innerText || '';
                """;
            var text = (string?)((IJavaScriptExecutor)driver)
                .ExecuteScript(script) ?? "";
            return (long)Encoding.UTF8.GetByteCount(text);
        }, cancellationToken);

    public Task PrepareMessageAsync(
        string message, CancellationToken cancellationToken = default) =>
        ExecuteAsync(driver =>
        {
            var composer = WaitForComposer(driver, cancellationToken);
            SetComposerText(driver, composer, message);
            var actual = ReadComposerText(driver, composer);
            if (!Equivalent(actual, message))
            {
                composer.Click();
                composer.SendKeys(Keys.Control + "a");
                composer.SendKeys(Keys.Backspace);
                composer.SendKeys(message);
                actual = ReadComposerText(driver, composer);
            }

            if (!Equivalent(actual, message))
                throw new InvalidOperationException(
                    "ChatGPT composer did not retain the complete message.");
        }, cancellationToken);

    public Task WaitForSendReadyAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(driver =>
        {
            WaitUntil(() => IsSendReady(driver),
                TimeSpan.FromSeconds(30), cancellationToken,
                "Send button did not become ready.");
        }, cancellationToken);

    public Task<SendVerification> VerifyManualSendAsync(
        string expectedMessage,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(driver =>
        {
            var composer = FindComposer(driver);
            var composerEmpty = composer is not null &&
                string.IsNullOrWhiteSpace(ReadComposerText(driver, composer));
            var sendNotReady = !IsSendReady(driver);
            var marker = Regex.Match(expectedMessage,
                @"CinDa-DaWatcha-ID:\s*[0-9a-fA-F]{32}").Value;
            var messageVisible = FindUserMessages(driver).Any(text =>
                marker.Length > 0
                    ? text.Contains(marker, StringComparison.OrdinalIgnoreCase)
                    : Equivalent(text, expectedMessage));
            return new SendVerification(
                composerEmpty, sendNotReady, messageVisible);
        }, cancellationToken);

    public Task<string?> CaptureConversationUuidAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(driver =>
        {
            var match = Regex.Match(driver.Url,
                @"/c/(?<id>[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12})(?:[/?#]|$)");
            return match.Success ? match.Groups["id"].Value : null;
        }, cancellationToken);

    private async Task ExecuteAsync(
        Action<FirefoxDriver> action,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync<object?>(driver =>
        {
            action(driver);
            return null;
        }, cancellationToken);
    }

    private async Task<T> ExecuteAsync<T>(
        Func<FirefoxDriver, T> action,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var driver = EnsureDriver();
                EnsureSingleTab(driver);
                return action(driver);
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private FirefoxDriver EnsureDriver()
    {
        if (_driver is not null)
        {
            try
            {
                _ = _driver.Title;
                return _driver;
            }
            catch (WebDriverException)
            {
                _driver.Dispose();
                _driver = null;
            }
        }

        var settings = _settings();
        Directory.CreateDirectory(settings.FirefoxProfileDirectory);
        var options = new FirefoxOptions
        {
            BinaryLocation = settings.FirefoxBinary
        };
        options.AddArgument("-no-remote");
        options.AddArgument("-profile");
        options.AddArgument(settings.FirefoxProfileDirectory);
        _driver = new FirefoxDriver(options);
        _driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(45);
        _primaryHandle = _driver.CurrentWindowHandle;
        return _driver;
    }

    private void EnsureSingleTab(FirefoxDriver driver)
    {
        var handles = driver.WindowHandles.ToArray();
        if (handles.Length == 0)
            throw new WebDriverException("Firefox has no open tab.");
        if (_primaryHandle is null || !handles.Contains(_primaryHandle))
            _primaryHandle = handles[0];

        foreach (var handle in handles.Where(h => h != _primaryHandle))
        {
            driver.SwitchTo().Window(handle);
            driver.Close();
        }
        driver.SwitchTo().Window(_primaryHandle);
    }

    private static void WaitForDocument(
        FirefoxDriver driver, CancellationToken cancellationToken) =>
        WaitUntil(
            () => ((IJavaScriptExecutor)driver)
                .ExecuteScript("return document.readyState")?.ToString() ==
                "complete",
            TimeSpan.FromSeconds(45), cancellationToken,
            "ChatGPT page did not finish loading.");

    private static void EnsureComposer(
        FirefoxDriver driver, CancellationToken cancellationToken)
    {
        try
        {
            _ = WaitForComposer(driver, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new BrowserLoginRequiredException(
                $"ChatGPT composer is unavailable at {driver.Url}. " +
                "Complete sign-in in the managed Firefox window.");
        }
    }

    private static IWebElement WaitForComposer(
        FirefoxDriver driver, CancellationToken cancellationToken)
    {
        IWebElement? composer = null;
        WaitUntil(() => (composer = FindComposer(driver)) is not null,
            TimeSpan.FromSeconds(30), cancellationToken,
            "ChatGPT composer was not found.");
        return composer!;
    }

    private static IWebElement? FindComposer(FirefoxDriver driver)
    {
        var selectors = new[]
        {
            By.CssSelector("#prompt-textarea"),
            By.CssSelector("main [contenteditable='true']"),
            By.CssSelector("textarea[placeholder]")
        };
        foreach (var selector in selectors)
        {
            try
            {
                var element = driver.FindElements(selector)
                    .FirstOrDefault(item => item.Displayed && item.Enabled);
                if (element is not null)
                    return element;
            }
            catch (StaleElementReferenceException) { }
        }
        return null;
    }

    private static void SetComposerText(
        FirefoxDriver driver, IWebElement composer, string message)
    {
        const string script = """
            const el = arguments[0], text = arguments[1];
            el.focus();
            if (el instanceof HTMLTextAreaElement) {
              const set = Object.getOwnPropertyDescriptor(
                HTMLTextAreaElement.prototype, 'value').set;
              set.call(el, text);
            } else {
              el.replaceChildren(document.createTextNode(text));
            }
            el.dispatchEvent(new InputEvent('input', {
              bubbles: true, inputType: 'insertText', data: text
            }));
            """;
        ((IJavaScriptExecutor)driver).ExecuteScript(script, composer, message);
    }

    private static string ReadComposerText(
        FirefoxDriver driver, IWebElement composer) =>
        ((IJavaScriptExecutor)driver).ExecuteScript(
            "return arguments[0].value ?? arguments[0].innerText ?? '';",
            composer)?.ToString() ?? "";

    private static bool IsSendReady(FirefoxDriver driver)
    {
        var selectors = new[]
        {
            By.CssSelector("button[data-testid='send-button']"),
            By.CssSelector("button[aria-label*='Send']")
        };
        foreach (var selector in selectors)
        {
            try
            {
                var button = driver.FindElements(selector)
                    .FirstOrDefault(item => item.Displayed);
                if (button is not null)
                    return button.Enabled &&
                        button.GetAttribute("aria-disabled") != "true";
            }
            catch (StaleElementReferenceException) { }
        }
        return false;
    }

    private static IEnumerable<string> FindUserMessages(FirefoxDriver driver)
    {
        const string script = """
            return [...document.querySelectorAll(
              "[data-message-author-role='user']")]
              .filter(e => e.getClientRects().length > 0)
              .map(e => e.innerText || '');
            """;
        var values = ((IJavaScriptExecutor)driver)
            .ExecuteScript(script) as IReadOnlyCollection<object>;
        return values?.Select(value => value?.ToString() ?? "") ?? [];
    }

    private static bool Equivalent(string left, string right) =>
        Normalize(left).Equals(Normalize(right), StringComparison.Ordinal);

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Trim();

    private static void WaitUntil(
        Func<bool> condition, TimeSpan timeout,
        CancellationToken cancellationToken, string error)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (condition())
                    return;
            }
            catch (StaleElementReferenceException) { }
            Thread.Sleep(200);
        }
        throw new TimeoutException(error);
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _driver?.Quit();
            _driver?.Dispose();
            _driver = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
