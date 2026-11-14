using System.Text.Json;
using CopilotTeamsBridge.Worker.Configuration;
using CopilotTeamsBridge.Worker.Interactions;
using Microsoft.Extensions.Options;

namespace CopilotTeamsBridge.Worker.AgentHost;

/// <summary>
/// Projecao em memoria dos pedidos pendentes do chat observado. A fonte da verdade continua
/// sendo o Agent Host: o estado e recriado a partir do snapshot a cada reconexao.
/// </summary>
internal interface IPendingInteractionTracker
{
    /// <summary>Recria a projecao a partir do estado do chat.</summary>
    /// <param name="chatState">Estado <c>ChatState</c> devolvido no snapshot.</param>
    /// <returns>Pedidos pendentes encontrados.</returns>
    IReadOnlyList<PendingInteraction> Reset(JsonElement chatState);

    /// <summary>Aplica uma notificacao AHP ao estado projetado.</summary>
    /// <param name="notification">Notificacao recebida do host.</param>
    /// <returns>Pedidos que passaram a ficar pendentes com esta notificacao.</returns>
    IReadOnlyList<PendingInteraction> Apply(AhpNotification notification);

    bool IsPending(string key);
}

internal sealed partial class PendingInteractionTracker(IOptions<TeamsBridgeOptions> options) : IPendingInteractionTracker
{
    private readonly string _chatChannel = options.Value.AgentHost.ChatChannel;
    private readonly object _sync = new();
    private readonly Dictionary<string, PendingInteraction> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private string? _currentTurnId;

    public IReadOnlyList<PendingInteraction> Reset(JsonElement chatState)
    {
        lock (_sync)
        {
            _pending.Clear();
            _toolNames.Clear();
            _currentTurnId = null;
            if (chatState.ValueKind != JsonValueKind.Object
                || !chatState.TryGetProperty("activeTurn", out var activeTurn)
                || activeTurn.ValueKind != JsonValueKind.Object)
                return [];

            _currentTurnId = AhpStateReader.GetString(activeTurn, "id");
            if (activeTurn.TryGetProperty("responseParts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                    ReadSnapshotPart(part);
            }

            return [.. _pending.Values];
        }
    }

    public IReadOnlyList<PendingInteraction> Apply(AhpNotification notification)
    {
        if (!string.Equals(notification.Method, AhpProtocol.ActionNotification, StringComparison.Ordinal)
            || !string.Equals(AhpStateReader.GetString(notification.Parameters, "channel"), _chatChannel, StringComparison.Ordinal)
            || AhpStateReader.HasProperty(notification.Parameters, "rejectionReason")
            || !notification.Parameters.TryGetProperty("action", out var action))
            return [];

        lock (_sync)
        {
            var added = ApplyAction(AhpStateReader.GetString(action, "type"), action);
            return added is null ? [] : [added];
        }
    }

    public bool IsPending(string key)
    {
        lock (_sync)
            return _pending.ContainsKey(key);
    }

    private void ReadSnapshotPart(JsonElement part)
    {
        var kind = AhpStateReader.GetString(part, "kind");
        if (string.Equals(kind, "inputRequest", StringComparison.Ordinal)
            && !AhpStateReader.HasProperty(part, "response")
            && part.TryGetProperty("request", out var request))
        {
            _ = Add(AhpStateReader.ReadInputRequest(request, _currentTurnId));
            return;
        }

        if (!string.Equals(kind, "toolCall", StringComparison.Ordinal) || !part.TryGetProperty("toolCall", out var toolCall))
            return;

        var toolCallId = AhpStateReader.GetString(toolCall, "toolCallId");
        var displayName = AhpStateReader.GetString(toolCall, "displayName");
        if (toolCallId is not null && displayName is not null)
            _toolNames[toolCallId] = displayName;

        if (string.Equals(AhpStateReader.GetString(toolCall, "status"), AhpProtocol.PendingConfirmationStatus, StringComparison.Ordinal))
            _ = Add(AhpStateReader.ReadToolConfirmation(toolCall, _currentTurnId, displayName));
    }

    private PendingInteraction? Add(PendingInteraction? interaction)
    {
        if (interaction is null)
            return null;

        var isNew = !_pending.ContainsKey(interaction.Key);
        _pending[interaction.Key] = interaction;
        return isNew ? interaction : null;
    }

    private void RemoveTurn(string? turnId)
    {
        foreach (var key in _pending.Where(item => string.Equals(item.Value.TurnId, turnId, StringComparison.Ordinal)).Select(item => item.Key).ToList())
            _ = _pending.Remove(key);

        if (string.Equals(_currentTurnId, turnId, StringComparison.Ordinal))
            _currentTurnId = null;
    }

    private static string Key(InteractionKind kind, string? id) => $"{kind}:{id}";
}
