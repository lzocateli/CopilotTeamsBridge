using System.Text.Json.Nodes;
using CopilotTeamsBridge.Worker.Interactions;
using CopilotTeamsBridge.Worker.Teams;
using Shouldly;
using Xunit;

namespace CopilotTeamsBridge.Worker.Tests.Teams;

[Trait("Category", "Unit")]
public sealed class AnswerMapperTest
{
    [Fact]
    public void MapsEveryQuestionKindToAgentHostAnswers()
    {
        var interaction = TestData.Input(
            TestData.Question("env", QuestionKind.SingleChoice),
            TestData.Question("tags", QuestionKind.MultiChoice),
            TestData.Question("count", QuestionKind.WholeNumber, min: 1, max: 5),
            TestData.Question("ratio", QuestionKind.Numeric),
            TestData.Question("ok", QuestionKind.Toggle),
            TestData.Question("note", QuestionKind.Text),
            TestData.Question("extra", QuestionKind.Text, required: false));

        AnswerMapper.TryMap(interaction, TestData.Json("""
            {"env":"a","tags":"a,b,a","count":"3","ratio":"0.5","ok":"true","note":" texto "}
            """), out var answers).ShouldBeTrue();

        Value(answers, "env").ShouldBe("""{"kind":"selected","value":"a"}""");
        Value(answers, "tags").ShouldBe("""{"kind":"selected-many","value":["a","b"]}""");
        Value(answers, "count").ShouldBe("""{"kind":"number","value":3}""");
        Value(answers, "ratio").ShouldBe("""{"kind":"number","value":0.5}""");
        Value(answers, "ok").ShouldBe("""{"kind":"boolean","value":true}""");
        Value(answers, "note").ShouldBe("""{"kind":"text","value":"texto"}""");
        answers["env"]!["state"]!.GetValue<string>().ShouldBe("submitted");
        answers["extra"]!.ToJsonString().ShouldBe("""{"state":"skipped"}""");
    }

    [Theory]
    [InlineData("""{"count":"2.5"}""")]
    [InlineData("""{"count":"9"}""")]
    [InlineData("""{"count":"0"}""")]
    [InlineData("""{"count":"abc"}""")]
    [InlineData("""{}""")]
    public void RejectsInvalidOrMissingRequiredNumbers(string data)
    {
        var interaction = TestData.Input(TestData.Question("count", QuestionKind.WholeNumber, min: 1, max: 5));

        AnswerMapper.TryMap(interaction, TestData.Json(data), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("SingleChoice", "z")]
    [InlineData("MultiChoice", "a,z")]
    [InlineData("Toggle", "talvez")]
    public void RejectsValuesOutsideOriginalOptions(string kind, string value)
    {
        var interaction = TestData.Input(TestData.Question("q", Enum.Parse<QuestionKind>(kind)));

        AnswerMapper.TryMap(interaction, TestData.Json($$"""{"q":"{{value}}"}"""), out _).ShouldBeFalse();
    }

    private static string Value(JsonObject answers, string id) => answers[id]!["value"]!.ToJsonString();
}
