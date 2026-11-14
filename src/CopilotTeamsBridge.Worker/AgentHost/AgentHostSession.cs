using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotTeamsBridge.Worker.Configuration;
using Microsoft.Extensions.Options;

namespace CopilotTeamsBridge.Worker.AgentHost;

internal enum DispatchStatus
{
    Accepted = 1,
    Rejected = 2,
    Unconfirmed = 3,
    Unavailable = 4,
}

/// <summary>
/// Mantem a identidade AHP do adaptador entre reconexoes e envia acoes ao chat existente,
/// considerando-as resolvidas somente apos o eco aceito pelo host.
/// </summary>
internal interface IAgentHostSession
{
    string ClientId { get; }

    void Attach(IAhpConnection connection);

    void Detach();

    void OnNotification(AhpNotification notification);

    /// <summary>Envia a acao ao chat configurado e aguarda o eco do host.</summary>
    /// <param name="action">Acao AHP (<c>chat/inputCompleted</c> ou <c>chat/toolCallConfirmed</c>).</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns><see cref="DispatchStatus.Accepted"/> somente quando o host ecoar a acao sem rejeicao.</returns>
    Task<DispatchStatus> DispatchAsync(JsonObject action, CancellationToken cancellationToken);
}

internal sealed class AgentHostSession(
    IOptions<TeamsBridgeOptions> options,
    TimeProvider timeProvider) : IAgentHostSession
{
    private readonly AgentHostOptions _options = options.Value.AgentHost;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<DispatchStatus>> _echoWaiters = new();
    private TaskCompletionSource<IAhpConnection> _attached = NewAttachment();
    private long _clientSeq;

    public string ClientId { get; } = Guid.NewGuid().ToString("N");

    public void Attach(IAhpConnection connection) => _attached.TrySetResult(connection);

    public void Detach()
    {
        _ = Interlocked.Exchange(ref _attached, NewAttachment());
        foreach (var waiter in _echoWaiters.Values)
            _ = waiter.TrySetResult(DispatchStatus.Unconfirmed);
    }

    public void OnNotification(AhpNotification notification)
    {
        if (!string.Equals(notification.Method, AhpProtocol.ActionNotification, StringComparison.Ordinal)
            || notification.Parameters.ValueKind != JsonValueKind.Object
            || !notification.Parameters.TryGetProperty("origin", out var origin)
            || origin.ValueKind != JsonValueKind.Object)
            return;

        if (!origin.TryGetProperty("clientId", out var clientId)
            || !string.Equals(clientId.GetString(), ClientId, StringComparison.Ordinal)
            || !origin.TryGetProperty("clientSeq", out var seqElement)
            || !seqElement.TryGetInt64(out var seq)
            || !_echoWaiters.TryGetValue(seq, out var waiter))
            return;

        var rejected = notification.Parameters.TryGetProperty("rejectionReason", out var reason)
            && reason.ValueKind == JsonValueKind.String;
        _ = waiter.TrySetResult(rejected ? DispatchStatus.Rejected : DispatchStatus.Accepted);
    }

    public async Task<DispatchStatus> DispatchAsync(JsonObject action, CancellationToken cancellationToken)
    {
        IAhpConnection connection;
        try
        {
            connection = await _attached.Task.WaitAsync(_options.EchoTimeout, timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            return DispatchStatus.Unavailable;
        }

        var seq = Interlocked.Increment(ref _clientSeq);
        var waiter = new TaskCompletionSource<DispatchStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _echoWaiters[seq] = waiter;
        try
        {
            await connection.NotifyAsync(
                AhpProtocol.DispatchActionMethod,
                AhpActionFactory.Dispatch(_options.ChatChannel, seq, action),
                cancellationToken);

            return await waiter.Task.WaitAsync(_options.EchoTimeout, timeProvider, cancellationToken);
        }
        catch (Exception exception) when (exception is TimeoutException or WebSocketException or AhpConnectionClosedException or ObjectDisposedException)
        {
            return DispatchStatus.Unconfirmed;
        }
        finally
        {
            _ = _echoWaiters.TryRemove(seq, out _);
        }
    }

    private static TaskCompletionSource<IAhpConnection> NewAttachment() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
