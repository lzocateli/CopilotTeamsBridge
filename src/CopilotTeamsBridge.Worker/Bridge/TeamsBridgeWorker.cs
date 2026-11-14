using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using CopilotTeamsBridge.Worker.AgentHost;
using CopilotTeamsBridge.Worker.Configuration;
using CopilotTeamsBridge.Worker.Interactions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotTeamsBridge.Worker.Bridge;

/// <summary>
/// Mantem a conexao loopback com o Agent Host, observa o chat configurado e encaminha
/// cada pedido pendente uma unica vez ao <see cref="IInteractionBridge"/>, reconectando quando necessario.
/// </summary>
internal sealed partial class TeamsBridgeWorker(
    IAhpConnectionFactory connectionFactory,
    IAgentHostSession session,
    IPendingInteractionTracker tracker,
    IInteractionBridge bridge,
    ISecretFileReader secretReader,
    TimeProvider timeProvider,
    IOptions<TeamsBridgeOptions> options,
    ILogger<TeamsBridgeWorker> logger) : BackgroundService
{
    private readonly AgentHostOptions _options = options.Value.AgentHost;
    private readonly ILogger<TeamsBridgeWorker> _logger = logger;
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);

    internal int InFlightCount => _inFlight.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarting(options.Value.DryRun);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(stoppingToken);
                LogDisconnected("closed");
            }
            catch (Exception exception) when (IsConnectionFailure(exception))
            {
                LogDisconnected(exception.GetType().Name);
            }
            finally
            {
                session.Detach();
            }

            await Task.Delay(_options.ReconnectDelay, timeProvider, stoppingToken);
        }
    }

    internal async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        var token = await secretReader.ReadTextAsync(_options.TokenFile, cancellationToken);
        await using var connection = connectionFactory.Create();
        await connection.ConnectAsync(new Uri(_options.Endpoint), token, cancellationToken);
        _ = await connection.RequestAsync(
            AhpProtocol.InitializeMethod,
            AhpActionFactory.Initialize(session.ClientId, typeof(TeamsBridgeWorker).Assembly.GetName().Version?.ToString() ?? "0.0.0"),
            cancellationToken);
        var subscription = await connection.RequestAsync(
            AhpProtocol.SubscribeMethod,
            AhpActionFactory.Subscribe(_options.ChatChannel),
            cancellationToken);

        StartAll(tracker.Reset(ReadSnapshotState(subscription)), cancellationToken);
        session.Attach(connection);
        LogConnected();

        await foreach (var notification in connection.Notifications.ReadAllAsync(cancellationToken))
        {
            session.OnNotification(notification);
            StartAll(tracker.Apply(notification), cancellationToken);
        }
    }

    private void StartAll(IReadOnlyList<PendingInteraction> interactions, CancellationToken cancellationToken)
    {
        foreach (var interaction in interactions)
        {
            if (_inFlight.ContainsKey(interaction.Key))
                continue;

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inFlight.TryAdd(interaction.Key, gate.Task))
                continue;

            _ = Task.Run(() => ProcessAsync(interaction, gate, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ProcessAsync(PendingInteraction interaction, TaskCompletionSource gate, CancellationToken cancellationToken)
    {
        try
        {
            _ = await bridge.ProcessAsync(interaction, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Encerramento do servico; o pedido permanece pendente no host.
        }
        finally
        {
            _ = _inFlight.TryRemove(interaction.Key, out _);
            _ = gate.TrySetResult();
        }
    }

    private static JsonElement ReadSnapshotState(JsonElement subscription) =>
        subscription.ValueKind == JsonValueKind.Object
        && subscription.TryGetProperty("snapshot", out var snapshot)
        && snapshot.ValueKind == JsonValueKind.Object
        && snapshot.TryGetProperty("state", out var state)
            ? state
            : default;

    private static bool IsConnectionFailure(Exception exception) =>
        exception is WebSocketException or AhpProtocolException or AhpConnectionClosedException
            or InvalidOperationException or IOException or JsonException or UriFormatException;

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "TeamsBridge iniciado (DryRun={DryRun}).")]
    private partial void LogStarting(bool dryRun);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "Conectado ao Agent Host e inscrito no chat configurado.")]
    private partial void LogConnected();

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "Conexao com o Agent Host encerrada ({Reason}); nova tentativa em breve.")]
    private partial void LogDisconnected(string reason);
}
