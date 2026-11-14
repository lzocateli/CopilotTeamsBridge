using System.Text.Json.Nodes;

namespace CopilotTeamsBridge.Worker.AgentHost;

/// <summary>Nomes de metodos, canais e acoes do Agent Host Protocol (AHP 1.0.0) usados pelo adaptador.</summary>
internal static class AhpProtocol
{
    public const string ProtocolVersion = "1.0.0";
    public const string RootChannel = "ahp-root://";
    public const string TokenQueryParameter = "tkn";

    public const string InitializeMethod = "initialize";
    public const string SubscribeMethod = "subscribe";
    public const string UnsubscribeMethod = "unsubscribe";
    public const string ListSessionsMethod = "listSessions";
    public const string SessionScheme = "ahp-session:/";
    public const string ChatScheme = "ahp-chat:/";
    public const string DispatchActionMethod = "dispatchAction";
    public const string ActionNotification = "action";

    public const string InputRequested = "chat/inputRequested";
    public const string InputCompleted = "chat/inputCompleted";
    public const string ToolCallStart = "chat/toolCallStart";
    public const string ToolCallReady = "chat/toolCallReady";
    public const string ToolCallConfirmed = "chat/toolCallConfirmed";
    public const string ToolCallComplete = "chat/toolCallComplete";
    public const string TurnStarted = "chat/turnStarted";
    public const string TurnComplete = "chat/turnComplete";
    public const string TurnCancelled = "chat/turnCancelled";

    public const string PendingConfirmationStatus = "pending-confirmation";
}

/// <summary>Monta as acoes AHP que resolvem o pedido original na mesma conversa.</summary>
internal static class AhpActionFactory
{
    public static JsonObject InputCompleted(string requestId, string response, JsonObject? answers)
    {
        var action = new JsonObject
        {
            ["type"] = AhpProtocol.InputCompleted,
            ["requestId"] = requestId,
            ["response"] = response,
        };

        if (answers is not null)
            action["answers"] = answers;

        return action;
    }

    public static JsonObject ToolCallConfirmed(string turnId, string toolCallId, bool approved)
    {
        var action = new JsonObject
        {
            ["type"] = AhpProtocol.ToolCallConfirmed,
            ["turnId"] = turnId,
            ["toolCallId"] = toolCallId,
            ["approved"] = approved,
        };

        if (approved)
            action["confirmed"] = "user-action";
        else
            action["reason"] = "denied";

        return action;
    }

    public static JsonObject Initialize(string clientId, string clientVersion) => new()
    {
        ["channel"] = AhpProtocol.RootChannel,
        ["protocolVersions"] = new JsonArray(AhpProtocol.ProtocolVersion),
        ["clientId"] = clientId,
        ["clientInfo"] = new JsonObject { ["name"] = "copilot-teams-bridge", ["version"] = clientVersion },
    };

    public static JsonObject Subscribe(string channel) => new() { ["channel"] = channel };

    public static JsonObject Dispatch(string channel, long clientSeq, JsonObject action) => new()
    {
        ["channel"] = channel,
        ["clientSeq"] = clientSeq,
        ["action"] = action,
    };
}
