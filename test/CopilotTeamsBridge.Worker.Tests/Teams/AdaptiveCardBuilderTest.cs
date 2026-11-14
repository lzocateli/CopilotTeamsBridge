using System.Text.Json.Nodes;
using CopilotTeamsBridge.Worker.Interactions;
using CopilotTeamsBridge.Worker.Security;
using CopilotTeamsBridge.Worker.Teams;
using Shouldly;
using Xunit;

namespace CopilotTeamsBridge.Worker.Tests.Teams;

[Trait("Category", "Unit")]
public sealed class AdaptiveCardBuilderTest
{
    private static readonly IssuedCorrelation s_correlation = new("token-opaco", "nonce", new DateTimeOffset(2025, 1, 1, 12, 15, 0, TimeSpan.Zero));
    private readonly AdaptiveCardBuilder _builder = new(TestData.Wrap(TestData.Options(options => options.MaxCardTextLength = 200)));

    [Fact]
    public void ToolConfirmationOffersApproveAndDenyWithOpaqueReference()
    {
        var result = _builder.Build(TestData.ToolConfirmation(), s_correlation);

        result.ApprovalOffered.ShouldBeTrue();
        var card = Card(result);
        card["version"]!.GetValue<string>().ShouldBe("1.4");
        Decisions(card).ShouldBe(["approve", "deny"]);
        card["actions"]!.AsArray().ShouldAllBe(action => action!["data"]!["correlationToken"]!.GetValue<string>() == "token-opaco");
        result.Payload!["type"]!.GetValue<string>().ShouldBe("message");
    }

    [Fact]
    public void TruncatedToolConfirmationOnlyOffersDeny()
    {
        var result = _builder.Build(TestData.ToolConfirmation(message: new string('x', 500)), s_correlation);

        result.ApprovalOffered.ShouldBeFalse();
        Decisions(Card(result)).ShouldBe(["deny"]);
    }

    [Fact]
    public void InputRequestRendersTypedInputs()
    {
        var interaction = TestData.Input(
            TestData.Question("env", QuestionKind.SingleChoice),
            TestData.Question("tags", QuestionKind.MultiChoice, required: false),
            TestData.Question("count", QuestionKind.WholeNumber, min: 1, max: 5),
            TestData.Question("ok", QuestionKind.Toggle),
            TestData.Question("note", QuestionKind.Text));

        var card = Card(_builder.Build(interaction, s_correlation));
        var inputs = card["body"]!.AsArray().Where(item => item!["id"] is not null).ToDictionary(item => item!["id"]!.GetValue<string>());

        inputs["env"]!["type"]!.GetValue<string>().ShouldBe("Input.ChoiceSet");
        inputs["env"]!["isMultiSelect"]!.GetValue<bool>().ShouldBeFalse();
        inputs["tags"]!["isMultiSelect"]!.GetValue<bool>().ShouldBeTrue();
        inputs["count"]!["max"]!.GetValue<double>().ShouldBe(5);
        inputs["ok"]!["type"]!.GetValue<string>().ShouldBe("Input.Toggle");
        inputs["note"]!["isRequired"]!.GetValue<bool>().ShouldBeTrue();
        Decisions(card).ShouldBe(["accept", "decline", "cancel"]);
    }

    [Fact]
    public void UnsupportedRequestsAreNotPublished()
    {
        var withUrl = TestData.Input() with { RequiresExternalUrl = true };
        var unknownQuestion = TestData.Input(TestData.Question("x", QuestionKind.Unsupported));
        var reservedId = TestData.Input(TestData.Question("correlationToken", QuestionKind.Text));

        _builder.Build(withUrl, s_correlation).Payload.ShouldBeNull();
        _builder.Build(unknownQuestion, s_correlation).Payload.ShouldBeNull();
        _builder.Build(reservedId, s_correlation).Payload.ShouldBeNull();
    }

    [Fact]
    public void SanitizeNeutralizesLinksAndControlCharacters()
    {
        var (text, truncated) = AdaptiveCardBuilder.Sanitize("[clique](https://x.test)\u0007\nfim", 200);

        text.ShouldBe("[clique] (https://x.test)\nfim");
        truncated.ShouldBeFalse();
    }

    private static JsonObject Card(CardBuildResult result) =>
        result.Payload!["attachments"]![0]!["content"]!.AsObject();

    private static List<string> Decisions(JsonObject card) =>
        [.. card["actions"]!.AsArray().Select(action => action!["data"]!["decision"]!.GetValue<string>())];
}
