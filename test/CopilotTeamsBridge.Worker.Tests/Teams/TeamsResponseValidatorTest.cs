using System.Text;
using CopilotTeamsBridge.Worker.Interactions;
using CopilotTeamsBridge.Worker.Security;
using CopilotTeamsBridge.Worker.Teams;
using Shouldly;
using Xunit;

namespace CopilotTeamsBridge.Worker.Tests.Teams;

[Trait("Category", "Unit")]
public sealed class TeamsResponseValidatorTest
{
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly CorrelationTokenService _tokens;
    private readonly TeamsResponseValidator _validator;

    public TeamsResponseValidatorTest()
    {
        _tokens = new CorrelationTokenService(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"), _time);
        _validator = new TeamsResponseValidator(_tokens, TestData.Wrap(TestData.Options()));
    }

    [Fact]
    public void ApprovalFromAuthorizedResponderBuildsToolConfirmation()
    {
        var card = Publish(TestData.ToolConfirmation());

        var result = _validator.Validate(card, Body(card, "approve"), () => true);

        result.Outcome.ShouldBe(InteractionOutcome.Accepted);
        result.Action!.ToJsonString().ShouldBe("""{"type":"chat/toolCallConfirmed","turnId":"turn-1","toolCallId":"tool-1","approved":true,"confirmed":"user-action"}""");
    }

    [Fact]
    public void DenyBuildsDeniedConfirmation()
    {
        var card = Publish(TestData.ToolConfirmation());

        var action = _validator.Validate(card, Body(card, "deny"), () => true).Action!;

        action["approved"]!.GetValue<bool>().ShouldBeFalse();
        action["reason"]!.GetValue<string>().ShouldBe("denied");
    }

    [Fact]
    public void ApprovalIsInvalidWhenCardOnlyOfferedDeny()
    {
        var card = Publish(TestData.ToolConfirmation(), approvalOffered: false);

        _validator.Validate(card, Body(card, "approve"), () => true).Outcome.ShouldBe(InteractionOutcome.Invalid);
    }

    [Fact]
    public void AcceptedInputCarriesTypedAnswers()
    {
        var card = Publish(TestData.Input(TestData.Question("env", QuestionKind.SingleChoice)));

        var result = _validator.Validate(card, Body(card, "accept", ""","env":"b" """), () => true);

        result.Outcome.ShouldBe(InteractionOutcome.Accepted);
        result.Action!.ToJsonString().ShouldBe("""{"type":"chat/inputCompleted","requestId":"req-1","response":"accept","answers":{"env":{"state":"submitted","value":{"kind":"selected","value":"b"}}}}""");
    }

    [Theory]
    [InlineData("decline")]
    [InlineData("cancel")]
    public void DeclineAndCancelDoNotRequireAnswers(string decision)
    {
        var card = Publish(TestData.Input(TestData.Question("env", QuestionKind.SingleChoice)));

        var result = _validator.Validate(card, Body(card, decision), () => true);

        result.Outcome.ShouldBe(InteractionOutcome.Accepted);
        result.Action!["response"]!.GetValue<string>().ShouldBe(decision);
    }

    [Fact]
    public void OtherResponderIsForbidden()
    {
        var card = Publish(TestData.ToolConfirmation());

        _validator.Validate(card, Body(card, "approve", objectId: "33333333-3333-3333-3333-333333333333"), () => true)
            .Outcome.ShouldBe(InteractionOutcome.Forbidden);
        _validator.Validate(card, Body(card, "approve", tenantId: "33333333-3333-3333-3333-333333333333"), () => true)
            .Outcome.ShouldBe(InteractionOutcome.Forbidden);
    }

    [Fact]
    public void ReplayIsDuplicate()
    {
        var card = Publish(TestData.ToolConfirmation());

        _validator.Validate(card, Body(card, "approve"), () => true).Outcome.ShouldBe(InteractionOutcome.Accepted);
        _validator.Validate(card, Body(card, "approve"), () => true).Outcome.ShouldBe(InteractionOutcome.Duplicate);
    }

    [Fact]
    public void ExpiredStaleAndGoneRequestsAreRejected()
    {
        var card = Publish(TestData.ToolConfirmation());
        var other = Publish(TestData.ToolConfirmation("tool-9"));

        _validator.Validate(card, Body(other, "approve"), () => true).Outcome.ShouldBe(InteractionOutcome.StaleContext);
        _validator.Validate(card, Body(card, "approve"), () => false).Outcome.ShouldBe(InteractionOutcome.RuntimeRequestGone);

        _time.Advance(TimeSpan.FromMinutes(16));
        _validator.Validate(card, Body(card, "approve"), () => true).Outcome.ShouldBe(InteractionOutcome.Expired);
    }

    [Theory]
    [InlineData("nao-json")]
    [InlineData("""{"responder":{}}""")]
    [InlineData("""{"data":"x"}""")]
    public void MalformedBodiesAreInvalid(string body)
    {
        _validator.Validate(Publish(TestData.ToolConfirmation()), body, () => true).Outcome.ShouldBe(InteractionOutcome.Invalid);
    }

    [Fact]
    public void UnknownDecisionOrInvalidAnswerIsInvalid()
    {
        var tool = Publish(TestData.ToolConfirmation());
        var input = Publish(TestData.Input(TestData.Question("env", QuestionKind.SingleChoice)));

        _validator.Validate(tool, Body(tool, "accept"), () => true).Outcome.ShouldBe(InteractionOutcome.Invalid);
        _validator.Validate(input, Body(input, "accept", ""","env":"z" """), () => true).Outcome.ShouldBe(InteractionOutcome.Invalid);
    }

    private PublishedCard Publish(PendingInteraction interaction, bool approvalOffered = true) =>
        new(interaction, _tokens.Issue(interaction, TestData.ChatChannel, TimeSpan.FromMinutes(15)), approvalOffered);

    private static string Body(
        PublishedCard card,
        string decision,
        string extra = "",
        string objectId = TestData.ObjectId,
        string tenantId = TestData.TenantId) =>
        $$$"""
        {"responder":{"objectId":"{{{objectId}}}","tenantId":"{{{tenantId}}}"},
         "data":{"correlationToken":"{{{card.Correlation.Token}}}","decision":"{{{decision}}}"{{{extra}}}}}
        """;
}
