namespace CinDa.DaWatcha.Core;

public interface IChatBrowserDelivery
{
    Task NavigateToConversationAsync(
        string uuid, CancellationToken cancellationToken = default);

    Task RefreshConversationAsync(
        string uuid, CancellationToken cancellationToken = default);

    Task WaitForConversationIdleAsync(
        string uuid,
        string? allowedComposerText,
        AppSettings settings,
        CancellationToken cancellationToken = default);

    Task<bool> MessageExistsAsync(
        string uuid,
        string expectedMessage,
        CancellationToken cancellationToken = default);

    Task PrepareMessageAsync(
        string uuid,
        string message,
        CancellationToken cancellationToken = default);

    Task SendPreparedMessageAsync(
        string uuid,
        string expectedMessage,
        CancellationToken cancellationToken = default);

    Task<bool> VerifyDeliveredAsync(
        string uuid,
        string expectedMessage,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record DeliveryProgress(
    string DeliveryId,
    int Attempt,
    bool Refreshed,
    string Phase,
    string Detail);

public sealed record DeliveryOutcome(
    bool Delivered,
    bool ManualSendRequired,
    int AutomaticAttempts,
    bool RefreshAttempted,
    IReadOnlyList<string> Errors);

public sealed class HandoffDeliveryCoordinator
{
    private readonly IChatBrowserDelivery _browser;
    private readonly Func<AppSettings> _settings;

    public HandoffDeliveryCoordinator(
        IChatBrowserDelivery browser,
        Func<AppSettings> settings)
    {
        _browser = browser;
        _settings = settings;
    }

    public async Task<DeliveryOutcome> DeliverAutomaticallyAsync(
        OutgoingMessage outgoing,
        Func<DeliveryProgress, Task>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var settings = _settings();
        var attempts = Math.Clamp(settings.AutomaticSendAttempts, 1, 5);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await ReportAsync(progress, new DeliveryProgress(
                    outgoing.DeliveryId, attempt, false,
                    "Waiting for ChatGPT",
                    $"Automatic send {attempt} of {attempts}."));
                await _browser.NavigateToConversationAsync(
                    outgoing.ConversationUuid, cancellationToken);
                if (await IsAlreadyDeliveredAsync(outgoing, cancellationToken))
                    return Delivered(attempt, false, errors);

                await _browser.WaitForConversationIdleAsync(
                    outgoing.ConversationUuid, null, settings,
                    cancellationToken);
                if (await IsAlreadyDeliveredAsync(outgoing, cancellationToken))
                    return Delivered(attempt, false, errors);

                await ReportAsync(progress, new DeliveryProgress(
                    outgoing.DeliveryId, attempt, false,
                    "Preparing handoff", "Writing and verifying the composer."));
                await _browser.PrepareMessageAsync(
                    outgoing.ConversationUuid, outgoing.Message,
                    cancellationToken);
                await ReportAsync(progress, new DeliveryProgress(
                    outgoing.DeliveryId, attempt, false,
                    "Sending handoff", "Clicking ChatGPT Send once."));
                await _browser.SendPreparedMessageAsync(
                    outgoing.ConversationUuid, outgoing.Message,
                    cancellationToken);
                await ReportAsync(progress, new DeliveryProgress(
                    outgoing.DeliveryId, attempt, false,
                    "Verifying delivery",
                    "Waiting for the complete user message to appear."));
                if (await _browser.VerifyDeliveredAsync(
                        outgoing.ConversationUuid, outgoing.Message,
                        TimeSpan.FromMilliseconds(
                            settings.SendVerificationTimeoutMs),
                        cancellationToken))
                    return Delivered(attempt, false, errors);
                errors.Add($"Attempt {attempt}: complete message was not verified.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Add($"Attempt {attempt}: {exception.Message}");
            }
        }

        try
        {
            await ReportAsync(progress, new DeliveryProgress(
                outgoing.DeliveryId, attempts + 1, true,
                "Refresh recovery",
                "Refreshing once, checking for prior delivery, then trying once."));
            await _browser.NavigateToConversationAsync(
                outgoing.ConversationUuid, cancellationToken);
            await _browser.RefreshConversationAsync(
                outgoing.ConversationUuid, cancellationToken);
            if (await IsAlreadyDeliveredAsync(outgoing, cancellationToken))
                return Delivered(attempts, true, errors);

            await _browser.WaitForConversationIdleAsync(
                outgoing.ConversationUuid, null, settings,
                cancellationToken);
            if (await IsAlreadyDeliveredAsync(outgoing, cancellationToken))
                return Delivered(attempts, true, errors);
            await _browser.PrepareMessageAsync(
                outgoing.ConversationUuid, outgoing.Message,
                cancellationToken);
            await _browser.SendPreparedMessageAsync(
                outgoing.ConversationUuid, outgoing.Message,
                cancellationToken);
            if (await _browser.VerifyDeliveredAsync(
                    outgoing.ConversationUuid, outgoing.Message,
                    TimeSpan.FromMilliseconds(
                        settings.SendVerificationTimeoutMs),
                    cancellationToken))
                return Delivered(attempts, true, errors);
            errors.Add("Refresh attempt: complete message was not verified.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            errors.Add($"Refresh attempt: {exception.Message}");
        }

        try
        {
            await ReportAsync(progress, new DeliveryProgress(
                outgoing.DeliveryId, attempts + 1, true,
                "Preparing manual fallback",
                "Automatic delivery exhausted; preparing an operator-controlled send."));
            await _browser.NavigateToConversationAsync(
                outgoing.ConversationUuid, cancellationToken);
            if (await IsAlreadyDeliveredAsync(outgoing, cancellationToken))
                return Delivered(attempts, true, errors);
            await _browser.WaitForConversationIdleAsync(
                outgoing.ConversationUuid, null, settings,
                cancellationToken);
            await _browser.PrepareMessageAsync(
                outgoing.ConversationUuid, outgoing.Message,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            errors.Add($"Manual preparation: {exception.Message}");
        }

        return new DeliveryOutcome(false, true, attempts, true, errors);
    }

    public async Task<bool> SendManualFallbackAsync(
        OutgoingMessage outgoing,
        CancellationToken cancellationToken = default)
    {
        var settings = _settings();
        await _browser.NavigateToConversationAsync(
            outgoing.ConversationUuid, cancellationToken);
        if (await IsAlreadyDeliveredAsync(outgoing, cancellationToken))
            return true;
        await _browser.WaitForConversationIdleAsync(
            outgoing.ConversationUuid, outgoing.Message, settings,
            cancellationToken);
        await _browser.PrepareMessageAsync(
            outgoing.ConversationUuid, outgoing.Message, cancellationToken);
        await _browser.SendPreparedMessageAsync(
            outgoing.ConversationUuid, outgoing.Message,
            cancellationToken);
        return await _browser.VerifyDeliveredAsync(
            outgoing.ConversationUuid, outgoing.Message,
            TimeSpan.FromMilliseconds(settings.SendVerificationTimeoutMs),
            cancellationToken);
    }

    public Task<bool> VerifyManualFallbackAsync(
        OutgoingMessage outgoing,
        CancellationToken cancellationToken = default) =>
        _browser.MessageExistsAsync(
            outgoing.ConversationUuid, outgoing.Message, cancellationToken);

    private Task<bool> IsAlreadyDeliveredAsync(
        OutgoingMessage outgoing, CancellationToken cancellationToken) =>
        _browser.MessageExistsAsync(
            outgoing.ConversationUuid, outgoing.Message, cancellationToken);

    private static DeliveryOutcome Delivered(
        int attempts, bool refreshed, IReadOnlyList<string> errors) =>
        new(true, false, attempts, refreshed, errors.ToArray());

    private static Task ReportAsync(
        Func<DeliveryProgress, Task>? progress,
        DeliveryProgress value) =>
        progress?.Invoke(value) ?? Task.CompletedTask;
}
