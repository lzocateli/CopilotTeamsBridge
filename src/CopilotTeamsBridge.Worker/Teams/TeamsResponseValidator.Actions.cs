using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotTeamsBridge.Worker.AgentHost;
using CopilotTeamsBridge.Worker.Interactions;

namespace CopilotTeamsBridge.Worker.Teams;

internal sealed partial class TeamsResponseValidator
{
    private static JsonObject? BuildAction(PublishedCard card, string? decision, JsonElement data)
    {
        var interaction = card.Interaction;
        if (interaction.Kind == InteractionKind.ToolConfirmation)
            return BuildToolConfirmation(card, decision);

        switch (decision)
        {
            case CardDecisions.Accept:
                return AnswerMapper.TryMap(interaction, data, out var answers)
                    ? AhpActionFactory.InputCompleted(interaction.Id, CardDecisions.Accept, answers)
                    : null;
            case CardDecisions.Decline:
            case CardDecisions.Cancel:
                return AhpActionFactory.InputCompleted(interaction.Id, decision, null);
            default:
                return null;
        }
    }

    private static JsonObject? BuildToolConfirmation(PublishedCard card, string? decision)
    {
        var turnId = card.Interaction.TurnId;
        if (string.IsNullOrEmpty(turnId))
            return null;

        return decision switch
        {
            CardDecisions.Approve when card.ApprovalOffered => AhpActionFactory.ToolCallConfirmed(turnId, card.Interaction.Id, approved: true),
            CardDecisions.Deny => AhpActionFactory.ToolCallConfirmed(turnId, card.Interaction.Id, approved: false),
            _ => null,
        };
    }
}
