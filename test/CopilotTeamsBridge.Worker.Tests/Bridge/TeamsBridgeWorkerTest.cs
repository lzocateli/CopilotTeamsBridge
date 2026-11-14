using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CopilotTeamsBridge.Worker.AgentHost;
using CopilotTeamsBridge.Worker.Bridge;
using CopilotTeamsBridge.Worker.Configuration;
using CopilotTeamsBridge.Worker.Interactions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace CopilotTeamsBridge.Worker.Tests.Bridge;

[Trait("Category", "Unit")]
public sealed class TeamsBridgeWorkerTest : IAsyncDisposable
{
    private const string Snapshot = """
        {"snapshot":{"resource":"ahp-chat:/chat-1","fromSeq":1,"state":{"activeTurn":{"id":"turn-1","responseParts":[
          {"kind":"toolCall","toolCall":{"status":"pending-confirmation","toolCallId":"tool-1","displayName":"Terminal","invocationMessage":"dotnet test"}}
        ]}}}}
        """;

    private readonly FakeConnection _connection = new();
    private readonly Mock<IInteractionBridge> _bridge = new();
    private readonly Mock<ISecretFileReader> _secrets = new();
    private readonly TaskCompletionSource<InteractionOutcome> _release = new();
    private readonly List<string> _processed = [];
    private readonly AgentHostSession _session;

    public TeamsBridgeWorkerTest()
    {
        _session = new AgentHostSession(TestData.Wrap(TestData.Options()), TimeProvider.System);
        _ = _secrets.Setup(reader => reader.ReadTextAsync("/secrets/agent-host.token", It.IsAny<CancellationToken>())).ReturnsAsync("token");
        _ = _bridge.Setup(bridge => bridge.ProcessAsync(It.IsAny<PendingInteraction>(), It.IsAny<CancellationToken>()))
            .Callback<PendingInteraction, CancellationToken>((interaction, _) =>
            {
                lock (_processed)
                    _processed.Add(interaction.Key);
            })
            .Returns(_release.Task);
    }

    [Fact]
    public async Task SubscribesToConfiguredChatAndProcessesEachPendingRequestOnce()
    {
        using var worker = Worker();
        var run = worker.RunConnectionAsync(CancellationToken.None);

        await WaitUntil(() => Processed().Count == 1);
        await _connection.PushAsync("""{"type":"chat/toolCallReady","turnId":"turn-1","toolCallId":"tool-1","invocationMessage":"dotnet test"}""");
        await _connection.PushAsync("""{"type":"chat/inputRequested","request":{"id":"req-1","message":"?"}}""");
        await WaitUntil(() => Processed().Count == 2);
        _connection.Complete();
        await run;

        Processed().ShouldBe(["ToolConfirmation:tool-1", "InputRequest:req-1"]);
        _connection.Endpoint!.ToString().ShouldBe("ws://127.0.0.1:40179/");
        _connection.Token.ShouldBe("token");
        _connection.Requests.Select(request => request.Method).ShouldBe(["initialize", "subscribe"]);
        _connection.Requests[0].Parameters["clientId"]!.GetValue<string>().ShouldBe(_session.ClientId);
        _connection.Requests[1].Parameters["channel"]!.GetValue<string>().ShouldBe(TestData.ChatChannel);
        worker.InFlightCount.ShouldBe(2);

        _release.SetResult(InteractionOutcome.Accepted);
        await WaitUntil(() => worker.InFlightCount == 0);
    }

    [Fact]
    public async Task ReconnectsAfterConnectionFailure()
    {
        var attempts = 0;
        var factory = new Mock<IAhpConnectionFactory>();
        _ = factory.Setup(item => item.Create()).Returns(() =>
        {
            _ = Interlocked.Increment(ref attempts);
            return new FakeConnection(failConnect: true);
        });
        using var worker = Worker(factory.Object);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => Volatile.Read(ref attempts) >= 3);
        await worker.StopAsync(CancellationToken.None);

        attempts.ShouldBeGreaterThanOrEqualTo(3);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private TeamsBridgeWorker Worker(IAhpConnectionFactory? factory = null)
    {
        var options = TestData.Wrap(TestData.Options());
        if (factory is null)
        {
            var mock = new Mock<IAhpConnectionFactory>();
            _ = mock.Setup(item => item.Create()).Returns(_connection);
            factory = mock.Object;
        }

        return new TeamsBridgeWorker(
            factory,
            _session,
            new PendingInteractionTracker(options),
            _bridge.Object,
            _secrets.Object,
            TimeProvider.System,
            options,
            NullLogger<TeamsBridgeWorker>.Instance);
    }

    private List<string> Processed()
    {
        lock (_processed)
            return [.. _processed];
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeConnection(bool failConnect = false) : IAhpConnection
    {
        private readonly Channel<AhpNotification> _notifications = Channel.CreateUnbounded<AhpNotification>();
        private long _serverSeq;

        public Uri? Endpoint { get; private set; }

        public string? Token { get; private set; }

        public List<(string Method, JsonObject Parameters)> Requests { get; } = [];

        public ChannelReader<AhpNotification> Notifications => _notifications.Reader;

        public Task ConnectAsync(Uri endpoint, string connectionToken, CancellationToken cancellationToken)
        {
            if (failConnect)
                throw new AhpConnectionClosedException();

            Endpoint = endpoint;
            Token = connectionToken;
            return Task.CompletedTask;
        }

        public Task<JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
        {
            Requests.Add((method, parameters));
            return Task.FromResult(TestData.Json(method == "subscribe" ? Snapshot : """{"protocolVersion":"1.0.0"}"""));
        }

        public Task NotifyAsync(string method, JsonObject parameters, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask PushAsync(string action) =>
            _notifications.Writer.WriteAsync(new AhpNotification("action", TestData.Json(
                $$"""{"channel":"{{TestData.ChatChannel}}","serverSeq":{{Interlocked.Increment(ref _serverSeq)}},"action":{{action}}}""")));

        public void Complete() => _notifications.Writer.Complete();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
