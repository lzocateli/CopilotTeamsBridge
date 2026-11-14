using System.Text.Json.Nodes;
using System.Threading.Channels;
using CopilotTeamsBridge.Worker.AgentHost;
using Shouldly;
using Xunit;

namespace CopilotTeamsBridge.Worker.Tests.AgentHost;

[Trait("Category", "Unit")]
public sealed class AgentHostSessionTest
{
    private readonly AgentHostSession _session = new(
        TestData.Wrap(TestData.Options(options => options.AgentHost.EchoTimeout = TimeSpan.FromMilliseconds(300))),
        TimeProvider.System);

    [Fact]
    public async Task DispatchIsAcceptedOnlyAfterMatchingEcho()
    {
        await using var connection = new RecordingConnection(sent => _session.OnNotification(Echo(_session.ClientId, sent, rejected: false)));
        _session.Attach(connection);

        var status = await _session.DispatchAsync(AhpActionFactory.ToolCallConfirmed("t", "tc", approved: true), CancellationToken.None);

        status.ShouldBe(DispatchStatus.Accepted);
        var sent = connection.Sent.Single();
        sent.Method.ShouldBe("dispatchAction");
        sent.Parameters["channel"]!.GetValue<string>().ShouldBe(TestData.ChatChannel);
        sent.Parameters["action"]!["type"]!.GetValue<string>().ShouldBe("chat/toolCallConfirmed");
    }

    [Fact]
    public async Task RejectedEchoIsReported()
    {
        await using var connection = new RecordingConnection(sent => _session.OnNotification(Echo(_session.ClientId, sent, rejected: true)));
        _session.Attach(connection);

        (await _session.DispatchAsync([], CancellationToken.None)).ShouldBe(DispatchStatus.Rejected);
    }

    [Fact]
    public async Task EchoFromAnotherClientDoesNotConfirm()
    {
        await using var connection = new RecordingConnection(sent => _session.OnNotification(Echo("other-client", sent, rejected: false)));
        _session.Attach(connection);

        (await _session.DispatchAsync([], CancellationToken.None)).ShouldBe(DispatchStatus.Unconfirmed);
    }

    [Fact]
    public async Task DispatchWithoutConnectionIsUnavailable()
    {
        (await _session.DispatchAsync([], CancellationToken.None)).ShouldBe(DispatchStatus.Unavailable);
    }

    [Fact]
    public async Task DetachReleasesWaitersAsUnconfirmed()
    {
        await using var connection = new RecordingConnection(_ => _session.Detach());
        _session.Attach(connection);

        (await _session.DispatchAsync([], CancellationToken.None)).ShouldBe(DispatchStatus.Unconfirmed);
    }

    private static AhpNotification Echo(string clientId, (string Method, JsonObject Parameters) sent, bool rejected)
    {
        var seq = sent.Parameters["clientSeq"]!.GetValue<long>();
        var rejection = rejected ? ""","rejectionReason":"stale" """ : string.Empty;
        return new AhpNotification("action", TestData.Json($$"""
            {"channel":"{{TestData.ChatChannel}}","serverSeq":7,"action":{},"origin":{"clientId":"{{clientId}}","clientSeq":{{seq}}}{{rejection}}}
            """));
    }

    internal sealed class RecordingConnection(Action<(string Method, JsonObject Parameters)>? onNotify = null) : IAhpConnection
    {
        private readonly Channel<AhpNotification> _notifications = Channel.CreateUnbounded<AhpNotification>();

        public List<(string Method, JsonObject Parameters)> Sent { get; } = [];

        public ChannelReader<AhpNotification> Notifications => _notifications.Reader;

        public Task ConnectAsync(Uri endpoint, string connectionToken, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<System.Text.Json.JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken) =>
            Task.FromResult(TestData.Json("{}"));

        public Task NotifyAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
        {
            Sent.Add((method, parameters));
            onNotify?.Invoke((method, parameters));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
