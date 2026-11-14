using System.Text;
using System.Text.Json.Nodes;
using CopilotTeamsBridge.Worker.AgentHost;
using CopilotTeamsBridge.Worker.Bridge;
using CopilotTeamsBridge.Worker.Configuration;
using CopilotTeamsBridge.Worker.Interactions;
using CopilotTeamsBridge.Worker.Security;
using CopilotTeamsBridge.Worker.Teams;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace CopilotTeamsBridge.Worker.Tests.Bridge;

[Trait("Category", "Unit")]
public sealed class InteractionBridgeTest
{
    private static readonly Uri s_location = new("https://poll.example.test/runs/1");
    private readonly Mock<IWorkflowWebhookClient> _webhook = new();
    private readonly Mock<IPendingInteractionTracker> _tracker = new();
    private readonly Mock<IAgentHostSession> _session = new();
    private readonly CorrelationTokenService _tokens = new(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"), TimeProvider.System);
    private IssuedCorrelation? _lastIssued;

    public InteractionBridgeTest()
    {
        _ = _tracker.Setup(tracker => tracker.IsPending(It.IsAny<string>())).Returns(true);
        _ = _webhook.Setup(client => client.PostAsync(It.IsAny<JsonObject>(), It.IsAny<CancellationToken>()))
            .Callback<JsonObject, CancellationToken>((payload, _) => _lastIssued = ReadIssued(payload))
            .ReturnsAsync(new WebhookDelivery(WebhookDeliveryStatus.AwaitingResponse, s_location, 202));
    }

    [Fact]
    public async Task ApprovedResponseIsDispatchedToSameRequest()
    {
        JsonObject? dispatched = null;
        _ = _session.Setup(session => session.DispatchAsync(It.IsAny<JsonObject>(), It.IsAny<CancellationToken>()))
            .Callback<JsonObject, CancellationToken>((action, _) => dispatched = action)
            .ReturnsAsync(DispatchStatus.Accepted);
        _ = _webhook.SetupSequence(client => client.PollAsync(s_location, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowPollResult(WorkflowPollStatus.Running, null, null, 202))
            .ReturnsAsync(() => Completed("approve"));

        var outcome = await Bridge().ProcessAsync(TestData.ToolConfirmation(), CancellationToken.None);

        outcome.ShouldBe(InteractionOutcome.Accepted);
        dispatched!["toolCallId"]!.GetValue<string>().ShouldBe("tool-1");
        dispatched["approved"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Theory]
    [InlineData("Rejected", "RuntimeRequestGone")]
    [InlineData("Unconfirmed", "Unconfirmed")]
    [InlineData("Unavailable", "Unconfirmed")]
    public async Task DispatchStatusMapsToOutcome(string dispatch, string expected)
    {
        _ = _session.Setup(session => session.DispatchAsync(It.IsAny<JsonObject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enum.Parse<DispatchStatus>(dispatch));
        _ = _webhook.Setup(client => client.PollAsync(s_location, It.IsAny<CancellationToken>())).ReturnsAsync(() => Completed("deny"));

        (await Bridge().ProcessAsync(TestData.ToolConfirmation(), CancellationToken.None)).ShouldBe(Enum.Parse<InteractionOutcome>(expected));
    }

    [Fact]
    public async Task RequestResolvedElsewhereStopsPollingWithoutDispatch()
    {
        _ = _tracker.Setup(tracker => tracker.IsPending(It.IsAny<string>())).Returns(false);

        (await Bridge().ProcessAsync(TestData.ToolConfirmation(), CancellationToken.None)).ShouldBe(InteractionOutcome.RuntimeRequestGone);
        _webhook.Verify(client => client.PollAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()), Times.Never);
        _session.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ForbiddenResponseIsNeverDispatched()
    {
        _ = _webhook.Setup(client => client.PollAsync(s_location, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Completed("approve", objectId: "33333333-3333-3333-3333-333333333333"));

        (await Bridge().ProcessAsync(TestData.ToolConfirmation(), CancellationToken.None)).ShouldBe(InteractionOutcome.Forbidden);
        _session.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DryRunNeverCallsWorkflow()
    {
        (await Bridge(options => options.DryRun = true).ProcessAsync(TestData.ToolConfirmation(), CancellationToken.None))
            .ShouldBe(InteractionOutcome.DryRun);
        _webhook.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnsupportedRequestIsNotPublished()
    {
        var withUrl = TestData.Input() with { RequiresExternalUrl = true };

        (await Bridge().ProcessAsync(withUrl, CancellationToken.None)).ShouldBe(InteractionOutcome.Unsupported);
        _webhook.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("AcceptedWithoutReturn")]
    [InlineData("Unavailable")]
    [InlineData("Rejected")]
    public async Task DeliveryWithoutReturnPathIsUnavailable(string status)
    {
        _ = _webhook.Setup(client => client.PostAsync(It.IsAny<JsonObject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebhookDelivery(Enum.Parse<WebhookDeliveryStatus>(status), null, 200));

        (await Bridge().ProcessAsync(TestData.ToolConfirmation(), CancellationToken.None)).ShouldBe(InteractionOutcome.DeliveryUnavailable);
    }

    [Fact]
    public async Task TransportFailureOnPostIsUnavailable()
    {
        _ = _webhook.Setup(client => client.PostAsync(It.IsAny<JsonObject>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("falha"));

        (await Bridge().ProcessAsync(TestData.ToolConfirmation(), CancellationToken.None)).ShouldBe(InteractionOutcome.DeliveryUnavailable);
    }

    [Fact]
    public async Task FailedRunIsUnavailable()
    {
        _ = _webhook.Setup(client => client.PollAsync(s_location, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowPollResult(WorkflowPollStatus.Failed, null, null, 500));

        (await Bridge().ProcessAsync(TestData.ToolConfirmation(), CancellationToken.None)).ShouldBe(InteractionOutcome.DeliveryUnavailable);
    }

    [Fact]
    public async Task NoResponseBeforeDeadlineExpires()
    {
        _ = _webhook.Setup(client => client.PollAsync(s_location, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowPollResult(WorkflowPollStatus.Running, null, null, 202));

        var outcome = await Bridge(options => options.RequestTimeToLive = TimeSpan.FromMilliseconds(80))
            .ProcessAsync(TestData.ToolConfirmation(), CancellationToken.None);

        outcome.ShouldBe(InteractionOutcome.Expired);
        _session.VerifyNoOtherCalls();
    }

    [Fact]
    public void FingerprintDoesNotExposeKey()
    {
        var fingerprint = InteractionBridge.Fingerprint("ToolConfirmation:tool-1");

        fingerprint.Length.ShouldBe(12);
        fingerprint.ShouldNotContain("tool");
    }

    private InteractionBridge Bridge(Action<TeamsBridgeOptions>? configure = null)
    {
        var options = TestData.Wrap(TestData.Options(options =>
        {
            options.Workflow.PollInterval = TimeSpan.FromMilliseconds(5);
            configure?.Invoke(options);
        }));

        return new InteractionBridge(
            _tokens,
            new AdaptiveCardBuilder(options),
            _webhook.Object,
            new TeamsResponseValidator(_tokens, options),
            _tracker.Object,
            _session.Object,
            TimeProvider.System,
            options,
            NullLogger<InteractionBridge>.Instance);
    }

    private WorkflowPollResult Completed(string decision, string objectId = TestData.ObjectId) =>
        new(WorkflowPollStatus.Completed, $$$"""
            {"responder":{"objectId":"{{{objectId}}}","tenantId":"{{{TestData.TenantId}}}"},
             "data":{"correlationToken":"{{{_lastIssued!.Token}}}","decision":"{{{decision}}}"}}
            """, null, 200);

    private static IssuedCorrelation ReadIssued(JsonObject payload)
    {
        var token = payload["attachments"]![0]!["content"]!["actions"]![0]!["data"]!["correlationToken"]!.GetValue<string>();
        return new IssuedCorrelation(token, string.Empty, DateTimeOffset.MaxValue);
    }
}
