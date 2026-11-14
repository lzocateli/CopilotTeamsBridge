using System.Security.Cryptography;
using System.Text;
using CopilotTeamsBridge.Worker.AgentHost;
using CopilotTeamsBridge.Worker.Configuration;
using CopilotTeamsBridge.Worker.Interactions;
using CopilotTeamsBridge.Worker.Security;
using CopilotTeamsBridge.Worker.Teams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotTeamsBridge.Worker.Bridge;

/// <summary>
/// Conduz um pedido pendente pelo ciclo completo: cartao no Teams, espera pela resposta assincrona
/// do Workflow, validacao e devolucao ao mesmo pedido no Agent Host.
/// </summary>
internal interface IInteractionBridge
{
    /// <summary>Processa um pedido pendente ate resolucao, expiracao ou indisponibilidade.</summary>
    /// <param name="interaction">Pedido pendente no chat observado.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns>Resultado final do contrato de resposta.</returns>
    Task<InteractionOutcome> ProcessAsync(PendingInteraction interaction, CancellationToken cancellationToken);
}

internal sealed partial class InteractionBridge(
    ICorrelationTokenService tokens,
    IAdaptiveCardBuilder cardBuilder,
    IWorkflowWebhookClient webhook,
    ITeamsResponseValidator validator,
    IPendingInteractionTracker tracker,
    IAgentHostSession session,
    TimeProvider timeProvider,
    IOptions<TeamsBridgeOptions> options,
    ILogger<InteractionBridge> logger) : IInteractionBridge
{
    private static readonly TimeSpan s_maxRetryAfter = TimeSpan.FromMinutes(1);
    private readonly TeamsBridgeOptions _options = options.Value;
    private readonly ILogger<InteractionBridge> _logger = logger;

    public async Task<InteractionOutcome> ProcessAsync(PendingInteraction interaction, CancellationToken cancellationToken)
    {
        var outcome = await RunAsync(interaction, cancellationToken);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var fingerprint = Fingerprint(interaction.Key);
            LogOutcome(interaction.Kind, fingerprint, outcome);
        }
        return outcome;
    }

    private async Task<InteractionOutcome> RunAsync(PendingInteraction interaction, CancellationToken cancellationToken)
    {
        if (!AdaptiveCardBuilder.CanPublish(interaction))
            return InteractionOutcome.Unsupported;

        var correlation = tokens.Issue(interaction, _options.AgentHost.ChatChannel, _options.RequestTimeToLive);
        var card = cardBuilder.Build(interaction, correlation);
        if (card.Payload is null)
            return InteractionOutcome.Unsupported;

        if (_options.DryRun)
            return InteractionOutcome.DryRun;

        var delivery = await TryPostAsync(card, cancellationToken);
        if (delivery?.Status != WebhookDeliveryStatus.AwaitingResponse || delivery.StatusLocation is null)
        {
            LogDelivery(delivery?.Status, delivery?.HttpStatus ?? 0);
            return InteractionOutcome.DeliveryUnavailable;
        }

        var published = new PublishedCard(interaction, correlation, card.ApprovalOffered);
        return await AwaitResponseAsync(published, delivery.StatusLocation, cancellationToken);
    }

    private async Task<WebhookDelivery?> TryPostAsync(CardBuildResult card, CancellationToken cancellationToken)
    {
        try
        {
            return await webhook.PostAsync(card.Payload!, cancellationToken);
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            LogTransportFailure(exception.GetType().Name);
            return null;
        }
    }

    private async Task<InteractionOutcome> AwaitResponseAsync(PublishedCard card, Uri location, CancellationToken cancellationToken)
    {
        var delay = _options.Workflow.PollInterval;
        while (timeProvider.GetUtcNow() < card.Correlation.ExpiresAt)
        {
            await Task.Delay(delay, timeProvider, cancellationToken);
            if (!tracker.IsPending(card.Interaction.Key))
                return InteractionOutcome.RuntimeRequestGone;

            var poll = await TryPollAsync(location, cancellationToken);
            delay = NextDelay(poll?.RetryAfter);
            if (poll is null || poll.Status == WorkflowPollStatus.Running)
                continue;

            return poll.Status == WorkflowPollStatus.Completed
                ? await ResolveAsync(card, poll.Body!, cancellationToken)
                : InteractionOutcome.DeliveryUnavailable;
        }

        return InteractionOutcome.Expired;
    }

    private async Task<WorkflowPollResult?> TryPollAsync(Uri location, CancellationToken cancellationToken)
    {
        try
        {
            return await webhook.PollAsync(location, cancellationToken);
        }
        catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
        {
            LogTransportFailure(exception.GetType().Name);
            return null;
        }
    }

    private async Task<InteractionOutcome> ResolveAsync(PublishedCard card, string body, CancellationToken cancellationToken)
    {
        var validated = validator.Validate(card, body, () => tracker.IsPending(card.Interaction.Key));
        if (validated.Outcome != InteractionOutcome.Accepted || validated.Action is null)
            return validated.Outcome;

        return await session.DispatchAsync(validated.Action, cancellationToken) switch
        {
            DispatchStatus.Accepted => InteractionOutcome.Accepted,
            DispatchStatus.Rejected => InteractionOutcome.RuntimeRequestGone,
            _ => InteractionOutcome.Unconfirmed,
        };
    }

    private TimeSpan NextDelay(TimeSpan? retryAfter)
    {
        if (retryAfter is not { } value || value <= _options.Workflow.PollInterval)
            return _options.Workflow.PollInterval;

        return value > s_maxRetryAfter ? s_maxRetryAfter : value;
    }

    private static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or InvalidOperationException or IOException
        || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>Identificador curto e nao reversivel do pedido, para correlacao em logs sem expor ids.</summary>
    internal static string Fingerprint(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 6).ToLowerInvariant();
}
