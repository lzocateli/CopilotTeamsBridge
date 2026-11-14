using System.Text;
using System.Text.Json.Nodes;
using CopilotTeamsBridge.Worker.Configuration;
using CopilotTeamsBridge.Worker.Interactions;
using CopilotTeamsBridge.Worker.Security;
using Microsoft.Extensions.Options;

namespace CopilotTeamsBridge.Worker.Teams;

internal static class CardDataKeys
{
    public const string CorrelationToken = "correlationToken";
    public const string Decision = "decision";
}

internal static class CardDecisions
{
    public const string Accept = "accept";
    public const string Decline = "decline";
    public const string Cancel = "cancel";
    public const string Approve = "approve";
    public const string Deny = "deny";
}

internal sealed record CardBuildResult(JsonObject? Payload, bool ApprovalOffered);

/// <summary>Monta o envelope de webhook com Adaptive Card para o pedido pendente.</summary>
internal interface IAdaptiveCardBuilder
{
    /// <summary>Monta o cartao com contexto minimo, entradas tipadas e a referencia opaca.</summary>
    /// <param name="interaction">Pedido pendente no host.</param>
    /// <param name="correlation">Referencia emitida para este cartao.</param>
    /// <returns>Payload pronto para o Workflow ou <see langword="null"/> quando o pedido nao pode ser respondido pelo Teams.</returns>
    CardBuildResult Build(PendingInteraction interaction, IssuedCorrelation correlation);
}

internal sealed partial class AdaptiveCardBuilder(IOptions<TeamsBridgeOptions> options) : IAdaptiveCardBuilder
{
    private const int MaxLabelLength = 300;
    private readonly int _maxTextLength = options.Value.MaxCardTextLength;

    public static bool CanPublish(PendingInteraction interaction) =>
        !interaction.RequiresExternalUrl
        && interaction.Questions.All(question =>
            question.Kind != QuestionKind.Unsupported
            && question.Id.Length > 0
            && !string.Equals(question.Id, CardDataKeys.CorrelationToken, StringComparison.Ordinal)
            && !string.Equals(question.Id, CardDataKeys.Decision, StringComparison.Ordinal));

    public CardBuildResult Build(PendingInteraction interaction, IssuedCorrelation correlation)
    {
        if (!CanPublish(interaction))
            return new CardBuildResult(null, ApprovalOffered: false);

        var (message, truncated) = Sanitize(interaction.Message, _maxTextLength);
        var body = new JsonArray
        {
            TextBlock(Sanitize(interaction.Title, MaxLabelLength).Text, bold: true),
            TextBlock(message.Length == 0 ? "(sem descricao)" : message, bold: false),
        };

        foreach (var question in interaction.Questions)
            body.Add(BuildInput(question));

        var offerApproval = interaction.Kind == InteractionKind.InputRequest || !truncated;
        if (truncated)
            body.Add(Note(offerApproval
                ? "Conteudo resumido; consulte o detalhe completo no VS Code."
                : "Conteudo excede o limite do cartao; somente recusa disponivel aqui. Para aprovar, use o VS Code."));

        body.Add(Note($"Expira em {correlation.ExpiresAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC. Respostas de outras pessoas sao ignoradas."));

        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = body,
            ["actions"] = BuildActions(interaction.Kind, correlation.Token, offerApproval),
        };

        var payload = new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(new JsonObject
            {
                ["contentType"] = "application/vnd.microsoft.card.adaptive",
                ["contentUrl"] = null,
                ["content"] = card,
            }),
        };

        return new CardBuildResult(payload, offerApproval);
    }

    /// <summary>Remove caracteres de controle, neutraliza links Markdown e limita o tamanho.</summary>
    internal static (string Text, bool Truncated) Sanitize(string? value, int maxLength)
    {
        var builder = new StringBuilder();
        foreach (var character in value ?? string.Empty)
        {
            if (!char.IsControl(character) || character == '\n')
                _ = builder.Append(character);
        }

        var text = builder.ToString().Replace("](", "] (", StringComparison.Ordinal).Trim();
        return text.Length <= maxLength ? (text, false) : (string.Concat(text.AsSpan(0, maxLength), "..."), true);
    }

    private static JsonArray BuildActions(InteractionKind kind, string token, bool offerApproval)
    {
        var actions = new JsonArray();
        if (kind == InteractionKind.ToolConfirmation)
        {
            if (offerApproval)
                actions.Add(Submit("Aprovar", token, CardDecisions.Approve));

            actions.Add(Submit("Recusar", token, CardDecisions.Deny));
            return actions;
        }

        actions.Add(Submit("Enviar", token, CardDecisions.Accept));
        actions.Add(Submit("Recusar", token, CardDecisions.Decline));
        actions.Add(Submit("Cancelar", token, CardDecisions.Cancel));
        return actions;
    }

    private static JsonObject Submit(string title, string token, string decision) => new()
    {
        ["type"] = "Action.Submit",
        ["title"] = title,
        ["data"] = new JsonObject
        {
            [CardDataKeys.CorrelationToken] = token,
            [CardDataKeys.Decision] = decision,
        },
    };

    private static JsonObject TextBlock(string text, bool bold)
    {
        var block = new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true };
        if (bold)
        {
            block["weight"] = "Bolder";
            block["size"] = "Medium";
        }

        return block;
    }

    private static JsonObject Note(string text) => new()
    {
        ["type"] = "TextBlock",
        ["text"] = text,
        ["wrap"] = true,
        ["isSubtle"] = true,
        ["size"] = "Small",
    };
}
