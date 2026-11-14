using System.Text.Json;
using CopilotTeamsBridge.Worker.Interactions;

namespace CopilotTeamsBridge.Worker.AgentHost;

internal sealed partial class PendingInteractionTracker
{
    private PendingInteraction? ApplyAction(string? type, JsonElement action)
    {
        switch (type)
        {
            case AhpProtocol.TurnStarted:
                _currentTurnId = AhpStateReader.GetString(action, "turnId");
                return null;
            case AhpProtocol.InputRequested:
                return action.TryGetProperty("request", out var request)
                    ? Add(AhpStateReader.ReadInputRequest(request, _currentTurnId))
                    : null;
            case AhpProtocol.InputCompleted:
                _ = _pending.Remove(Key(InteractionKind.InputRequest, AhpStateReader.GetString(action, "requestId")));
                return null;
            case AhpProtocol.ToolCallStart:
                RememberToolName(action);
                return null;
            case AhpProtocol.ToolCallReady:
                return ApplyToolCallReady(action);
            case AhpProtocol.ToolCallConfirmed:
            case AhpProtocol.ToolCallComplete:
                _ = _pending.Remove(Key(InteractionKind.ToolConfirmation, AhpStateReader.GetString(action, "toolCallId")));
                return null;
            case AhpProtocol.TurnComplete:
            case AhpProtocol.TurnCancelled:
                RemoveTurn(AhpStateReader.GetString(action, "turnId"));
                return null;
            default:
                return null;
        }
    }

    private void RememberToolName(JsonElement action)
    {
        var toolCallId = AhpStateReader.GetString(action, "toolCallId");
        var displayName = AhpStateReader.GetString(action, "displayName");
        if (toolCallId is not null && displayName is not null)
            _toolNames[toolCallId] = displayName;
    }

    private PendingInteraction? ApplyToolCallReady(JsonElement action)
    {
        var toolCallId = AhpStateReader.GetString(action, "toolCallId");
        if (AhpStateReader.HasProperty(action, "confirmed"))
        {
            _ = _pending.Remove(Key(InteractionKind.ToolConfirmation, toolCallId));
            return null;
        }

        var displayName = toolCallId is not null && _toolNames.TryGetValue(toolCallId, out var name) ? name : null;
        return Add(AhpStateReader.ReadToolConfirmation(action, AhpStateReader.GetString(action, "turnId"), displayName));
    }
}
