using System.IO;
using System.Text;
using CinDa.DaWatcha.Core;
using OpenQA.Selenium;
using OpenQA.Selenium.Firefox;

namespace CinDa.DaWatcha.App;

public sealed class BrowserLoginRequiredException(string message)
    : InvalidOperationException(message);

public sealed class FirefoxChatController : IChatBrowserDelivery, IDisposable
{
    public static readonly Uri TrustedOrigin = new("https://chatgpt.com/");

    private readonly Func<AppSettings> _settings;
    private readonly Uri _trustedOrigin;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FirefoxDriver? _driver;
    private string? _primaryHandle;
    private bool _disposed;

    public FirefoxChatController(Func<AppSettings> settings)
        : this(settings, TrustedOrigin)
    {
    }

    internal FirefoxChatController(
        Func<AppSettings> settings, Uri trustedOrigin)
    {
        _settings = settings;
        _trustedOrigin = trustedOrigin;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(_ => { }, cancellationToken);

    public Task OpenHomeForLoginAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(driver =>
        {
            driver.Navigate().GoToUrl(_trustedOrigin);
            WaitForDocument(driver, cancellationToken);
            EnsureSingleTab(driver);
        }, cancellationToken);

    public Task NavigateToConversationAsync(
        string uuid, CancellationToken cancellationToken = default)
    {
        var canonical = CanonicalUuid(uuid);
        return ExecuteAsync(driver =>
        {
            driver.Navigate().GoToUrl(ConversationUri(canonical));
            WaitForDocument(driver, cancellationToken);
            EnsureSingleTab(driver);
            EnsureComposer(driver, cancellationToken);
            EnsureExactConversation(driver, canonical);
        }, cancellationToken);
    }

    public Task RefreshConversationAsync(
        string uuid, CancellationToken cancellationToken = default)
    {
        var canonical = CanonicalUuid(uuid);
        return ExecuteAsync(driver =>
        {
            EnsureExactConversation(driver, canonical);
            driver.Navigate().Refresh();
            WaitForDocument(driver, cancellationToken);
            EnsureSingleTab(driver);
            EnsureComposer(driver, cancellationToken);
            EnsureExactConversation(driver, canonical);
        }, cancellationToken);
    }

    public Task WaitForConversationIdleAsync(
        string uuid,
        string? allowedComposerText,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var canonical = CanonicalUuid(uuid);
        return ExecuteAsync(driver =>
        {
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(
                settings.ConversationIdleTimeoutMs);
            string? previousSignature = null;
            var stablePolls = 0;
            var lastReason = "Conversation state has not been inspected.";

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureExactConversation(driver, canonical);
                var composer = FindComposer(driver);
                if (composer is null)
                {
                    stablePolls = 0;
                    lastReason = "Composer is unavailable.";
                }
                else
                {
                    var composerText = ReadComposerText(driver, composer);
                    var composerAllowed = string.IsNullOrWhiteSpace(composerText) ||
                        allowedComposerText is not null &&
                        Equivalent(composerText, allowedComposerText);
                    var generating = IsGenerating(driver);
                    var signature = ReadConversationSignature(driver);

                    if (!composerAllowed)
                    {
                        stablePolls = 0;
                        lastReason = "The target conversation contains an unrelated draft.";
                    }
                    else if (generating)
                    {
                        stablePolls = 0;
                        lastReason = "ChatGPT is still generating a response.";
                    }
                    else
                    {
                        stablePolls = signature.Equals(previousSignature,
                            StringComparison.Ordinal) ? stablePolls + 1 : 1;
                        previousSignature = signature;
                        lastReason = $"Conversation stable for {stablePolls} poll(s).";
                        if (stablePolls >= settings.ConversationIdleStablePolls)
                            return;
                    }
                }

                Thread.Sleep(Math.Clamp(
                    settings.ConversationIdlePollMs, 250, 10_000));
            }
            throw new TimeoutException(
                "ChatGPT did not become verifiably idle. " + lastReason);
        }, cancellationToken);
    }

    public Task<bool> MessageExistsAsync(
        string uuid,
        string expectedMessage,
        CancellationToken cancellationToken = default)
    {
        var canonical = CanonicalUuid(uuid);
        return ExecuteAsync(driver =>
        {
            EnsureExactConversation(driver, canonical);
            return FindUserMessages(driver).Any(text =>
                Equivalent(text, expectedMessage));
        }, cancellationToken);
    }

    public Task PrepareMessageAsync(
        string uuid,
        string message,
        CancellationToken cancellationToken = default)
    {
        var canonical = CanonicalUuid(uuid);
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Handoff message cannot be empty.");

        return ExecuteAsync(driver =>
        {
            EnsureExactConversation(driver, canonical);
            var composer = WaitForComposer(driver, cancellationToken);
            var existing = ReadComposerText(driver, composer);
            if (!string.IsNullOrWhiteSpace(existing) &&
                !Equivalent(existing, message))
                throw new InvalidOperationException(
                    "The target conversation contains an unrelated draft. " +
                    "CinDa-DaWatcha will not overwrite it.");
            if (Equivalent(existing, message))
                return;

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
                    "ChatGPT composer did not retain the complete handoff.");
        }, cancellationToken);
    }

    public Task SendPreparedMessageAsync(
        string uuid,
        string expectedMessage,
        CancellationToken cancellationToken = default)
    {
        var canonical = CanonicalUuid(uuid);
        return ExecuteAsync(driver =>
        {
            EnsureExactConversation(driver, canonical);
            var composer = WaitForComposer(driver, cancellationToken);
            var actualMessage = ReadComposerText(driver, composer);
            if (FindUserMessages(driver).Any(text =>
                    Equivalent(text, expectedMessage)))
            {
                if (Equivalent(actualMessage, expectedMessage))
                    SetComposerText(driver, composer, "");
                return;
            }
            if (!Equivalent(actualMessage, expectedMessage))
                throw new InvalidOperationException(
                    "The composer no longer contains the exact handoff; " +
                    "refusing to click Send.");
            var button = WaitForSendButton(driver, cancellationToken);
            actualMessage = ReadComposerText(driver, composer);
            if (!Equivalent(actualMessage, expectedMessage))
                throw new InvalidOperationException(
                    "The composer changed while Send was becoming ready; " +
                    "refusing to click Send.");
            button.Click();
        }, cancellationToken);
    }

    public Task<bool> VerifyDeliveredAsync(
        string uuid,
        string expectedMessage,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var canonical = CanonicalUuid(uuid);
        return ExecuteAsync(driver =>
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureExactConversation(driver, canonical);
                if (FindUserMessages(driver).Any(text =>
                        Equivalent(text, expectedMessage)))
                    return true;
                Thread.Sleep(250);
            }
            return false;
        }, cancellationToken);
    }

    private Uri ConversationUri(string canonicalUuid) =>
        new(_trustedOrigin, "c/" + canonicalUuid);

    private static string CanonicalUuid(string uuid)
    {
        if (!Guid.TryParseExact(uuid, "D", out var parsed))
            throw new ArgumentException(
                "Conversation ID must be a canonical hyphenated UUID.");
        return parsed.ToString("D");
    }

    private void EnsureExactConversation(
        FirefoxDriver driver, string canonicalUuid)
    {
        if (!Uri.TryCreate(driver.Url, UriKind.Absolute, out var actual))
            throw new InvalidOperationException(
                "Firefox did not report a valid page URL.");
        if (!actual.Scheme.Equals(_trustedOrigin.Scheme,
                StringComparison.OrdinalIgnoreCase) ||
            !actual.Host.Equals(_trustedOrigin.Host,
                StringComparison.OrdinalIgnoreCase) ||
            actual.Port != _trustedOrigin.Port)
            throw new InvalidOperationException(
                $"Refusing handoff outside trusted origin {_trustedOrigin.GetLeftPart(UriPartial.Authority)}.");

        var expectedPath = "/c/" + canonicalUuid;
        if (!actual.AbsolutePath.TrimEnd('/').Equals(
                expectedPath, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(actual.Query) ||
            !string.IsNullOrEmpty(actual.Fragment))
            throw new InvalidOperationException(
                $"Firefox is not on the initiating conversation {canonicalUuid}. " +
                $"Current route: {actual.PathAndQuery}{actual.Fragment}");
    }

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
        ObjectDisposedException.ThrowIf(_disposed, this);
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
                try { _driver.Dispose(); }
                catch (WebDriverException) { }
                _driver = null;
            }
        }

        var settings = _settings();
        if (!File.Exists(settings.FirefoxBinary))
            throw new FileNotFoundException(
                "Configured Firefox executable was not found.",
                settings.FirefoxBinary);
        if (!File.Exists(settings.GeckoDriverPath))
            throw new FileNotFoundException(
                "Bundled GeckoDriver was not found. CinDa-DaWatcha will not " +
                "download a driver at runtime.", settings.GeckoDriverPath);

        Directory.CreateDirectory(settings.FirefoxProfileDirectory);
        var options = new FirefoxOptions
        {
            BinaryLocation = settings.FirefoxBinary
        };
        options.AddArgument("-no-remote");
        options.AddArgument("-profile");
        options.AddArgument(settings.FirefoxProfileDirectory);

        var driverDirectory = Path.GetDirectoryName(settings.GeckoDriverPath)!;
        var driverFile = Path.GetFileName(settings.GeckoDriverPath);
        var service = FirefoxDriverService.CreateDefaultService(
            driverDirectory, driverFile);
        service.HideCommandPromptWindow = true;
        _driver = new FirefoxDriver(service, options,
            TimeSpan.FromSeconds(60));
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
        foreach (var handle in handles.Where(handle => handle != _primaryHandle))
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
            TimeSpan.FromSeconds(45), "Page did not finish loading.",
            cancellationToken);

    private static void EnsureComposer(
        FirefoxDriver driver, CancellationToken cancellationToken)
    {
        try { _ = WaitForComposer(driver, cancellationToken); }
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
            TimeSpan.FromSeconds(30), "ChatGPT composer was not found.",
            cancellationToken);
        return composer!;
    }

    private static IWebElement? FindComposer(FirefoxDriver driver)
    {
        var selectors = new[]
        {
            By.CssSelector("#prompt-textarea"),
            By.CssSelector("main [contenteditable='true']"),
            By.CssSelector("main textarea[placeholder]")
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

    private static IWebElement WaitForSendButton(
        FirefoxDriver driver, CancellationToken cancellationToken)
    {
        IWebElement? button = null;
        WaitUntil(() => (button = FindReadySendButton(driver)) is not null,
            TimeSpan.FromSeconds(30),
            "ChatGPT Send button did not become ready.", cancellationToken);
        return button!;
    }

    private static IWebElement? FindReadySendButton(FirefoxDriver driver)
    {
        var selectors = new[]
        {
            By.CssSelector("button[data-testid='send-button']"),
            By.CssSelector("main button[aria-label='Send prompt']"),
            By.CssSelector("main button[aria-label='Send message']")
        };
        foreach (var selector in selectors)
        {
            try
            {
                var button = driver.FindElements(selector)
                    .FirstOrDefault(item => item.Displayed && item.Enabled &&
                        item.GetAttribute("aria-disabled") != "true");
                if (button is not null)
                    return button;
            }
            catch (StaleElementReferenceException) { }
        }
        return null;
    }

    private static bool IsGenerating(FirefoxDriver driver)
    {
        var selectors = new[]
        {
            By.CssSelector("button[data-testid='stop-button']"),
            By.CssSelector("main button[data-testid*='stop' i]"),
            By.CssSelector("main button[aria-label*='stop' i]"),
            By.CssSelector("main button[title*='stop' i]"),
            By.CssSelector("main [aria-busy='true']"),
            By.CssSelector("main [data-is-streaming='true']")
        };
        foreach (var selector in selectors)
        {
            try
            {
                if (driver.FindElements(selector).Any(item => item.Displayed))
                    return true;
            }
            catch (StaleElementReferenceException) { }
        }
        return false;
    }

    private static string ReadConversationSignature(FirefoxDriver driver)
    {
        const string script = """
            const visible = e => e.getClientRects().length > 0;
            const a = [...document.querySelectorAll(
              "[data-message-author-role='assistant']")].filter(visible);
            const u = [...document.querySelectorAll(
              "[data-message-author-role='user']")].filter(visible);
            return JSON.stringify({
              assistants: a.length,
              users: u.length,
              latest: a.length ? (a[a.length - 1].innerText || '') : '',
              pageTail: (document.querySelector('main')?.innerText || '').slice(-4096)
            });
            """;
        return ((IJavaScriptExecutor)driver)
            .ExecuteScript(script)?.ToString() ?? "";
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

    internal static bool Equivalent(string left, string right) =>
        Normalize(left).Equals(Normalize(right), StringComparison.Ordinal);

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Trim();

    private static void WaitUntil(
        Func<bool> condition, TimeSpan timeout,
        string error, CancellationToken cancellationToken)
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
        if (_disposed)
            return;
        _disposed = true;
        _gate.Wait();
        try
        {
            if (_driver is not null)
            {
                try { _driver.Quit(); }
                catch (WebDriverException) { }
                try { _driver.Dispose(); }
                catch (WebDriverException) { }
            }
            _driver = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
