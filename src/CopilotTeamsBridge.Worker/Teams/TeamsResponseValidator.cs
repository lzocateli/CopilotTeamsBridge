using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotTeamsBridge.Worker.AgentHost;
using CopilotTeamsBridge.Worker.Configuration;
using CopilotTeamsBridge.Worker.Interactions;
using CopilotTeamsBridge.Worker.Security;
using Microsoft.Extensions.Options;

namespace CopilotTeamsBridge.Worker.Teams;

/// <summary>Contexto de um cartao publicado e ainda aguardando resposta.</summary>
internal sealed record PublishedCard(PendingInteraction Interaction, IssuedCorrelation Correlation, bool ApprovalOffered);

internal sealed record ValidatedResponse(InteractionOutcome Outcome, JsonObject? Action);

/// <summary>
/// Valida o corpo devolvido pelo Workflow (<c>{responder, data}</c>) contra o pedido original,
/// a identidade autorizada, o prazo, a referencia opaca e o estado atual do host.
/// </summary>
internal interface ITeamsResponseValidator
{
    /// <summary>Valida a resposta e monta a acao AHP correspondente.</summary>
    /// <param name="card">Cartao publicado.</param>
    /// <param name="responseBody">Corpo JSON devolvido pela acao Response do Workflow.</param>
    /// <param name="isStillPending">Indica se o pedido ainda esta pendente no host.</param>
    /// <returns>Resultado do contrato e, quando <see cref="InteractionOutcome.Accepted"/>, a acao a despachar.</returns>
    ValidatedResponse Validate(PublishedCard card, string responseBody, Func<bool> isStillPending);
}

internal sealed partial class TeamsResponseValidator(
    ICorrelationTokenService tokens,
    IOptions<TeamsBridgeOptions> options) : ITeamsResponseValidator
{
    private readonly TeamsBridgeOptions _options = options.Value;

    public ValidatedResponse Validate(PublishedCard card, string responseBody, Func<bool> isStillPending)
    {
        using var document = TryParse(responseBody);
        if (document is null
            || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
            return Fail(InteractionOutcome.Invalid);

        var tokenStatus = tokens.Validate(
            AhpStateReader.GetString(data, CardDataKeys.CorrelationToken),
            card.Interaction,
            _options.AgentHost.ChatChannel,
            card.Correlation.Nonce);
        if (tokenStatus != TokenValidationStatus.Valid)
            return Fail(MapTokenStatus(tokenStatus));

        if (!IsAuthorizedResponder(document.RootElement))
            return Fail(InteractionOutcome.Forbidden);

        var action = BuildAction(card, AhpStateReader.GetString(data, CardDataKeys.Decision), data);
        if (action is null)
            return Fail(InteractionOutcome.Invalid);

        if (!isStillPending())
            return Fail(InteractionOutcome.RuntimeRequestGone);

        return tokens.TryConsume(card.Correlation.Nonce)
            ? new ValidatedResponse(InteractionOutcome.Accepted, action)
            : Fail(InteractionOutcome.Duplicate);
    }

    private static InteractionOutcome MapTokenStatus(TokenValidationStatus status) => status switch
    {
        TokenValidationStatus.Expired => InteractionOutcome.Expired,
        TokenValidationStatus.Mismatch => InteractionOutcome.StaleContext,
        _ => InteractionOutcome.Invalid,
    };

    private bool IsAuthorizedResponder(JsonElement root)
    {
        if (!root.TryGetProperty("responder", out var responder) || responder.ValueKind != JsonValueKind.Object)
            return false;

        return SameGuid(AhpStateReader.GetString(responder, "objectId"), _options.Responder.AllowedObjectId)
            && SameGuid(AhpStateReader.GetString(responder, "tenantId"), _options.Responder.AllowedTenantId);
    }

    private static bool SameGuid(string? actual, string expected) =>
        Guid.TryParse(actual, out var left) && Guid.TryParse(expected, out var right) && left == right;

    private static JsonDocument? TryParse(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ValidatedResponse Fail(InteractionOutcome outcome) => new(outcome, null);
}
