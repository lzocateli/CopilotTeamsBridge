using CopilotTeamsBridge.Worker.AgentHost;
using CopilotTeamsBridge.Worker.Interactions;
using Shouldly;
using Xunit;

namespace CopilotTeamsBridge.Worker.Tests.AgentHost;

[Trait("Category", "Unit")]
public sealed class PendingInteractionTrackerTest
{
    private readonly PendingInteractionTracker _tracker = new(TestData.Wrap(TestData.Options()));

    [Fact]
    public void ResetFindsUnansweredInputAndToolAwaitingConfirmation()
    {
        var pending = _tracker.Reset(TestData.Json("""
            {"activeTurn":{"id":"turn-1","responseParts":[
              {"kind":"inputRequest","request":{"id":"req-1","message":"Qual ambiente?","questions":[{"kind":"single-select","id":"env","message":"Ambiente","required":true,"options":[{"id":"dev","label":"Dev"}]}]}},
              {"kind":"inputRequest","request":{"id":"req-0","message":"Respondida"},"response":"accept"},
              {"kind":"toolCall","toolCall":{"status":"pending-confirmation","toolCallId":"tool-1","toolName":"run","displayName":"Terminal","invocationMessage":{"markdown":"dotnet test"}}},
              {"kind":"toolCall","toolCall":{"status":"running","toolCallId":"tool-0","displayName":"Terminal"}}
            ]}}
            """));

        pending.Select(item => item.Key).ShouldBe(["InputRequest:req-1", "ToolConfirmation:tool-1"], ignoreOrder: true);
        var input = pending.Single(item => item.Kind == InteractionKind.InputRequest);
        input.TurnId.ShouldBe("turn-1");
        input.Questions.Single().Kind.ShouldBe(QuestionKind.SingleChoice);
        pending.Single(item => item.Kind == InteractionKind.ToolConfirmation).Message.ShouldBe("dotnet test");
    }

    [Fact]
    public void ResetWithoutActiveTurnClearsState()
    {
        _ = _tracker.Reset(TestData.Json("""{"activeTurn":{"id":"t","responseParts":[{"kind":"inputRequest","request":{"id":"r"}}]}}"""));

        _tracker.Reset(TestData.Json("""{"activeTurn":null}""")).ShouldBeEmpty();
        _tracker.IsPending("InputRequest:r").ShouldBeFalse();
    }

    [Fact]
    public void ToolCallLifecycleTracksConfirmationUntilResolved()
    {
        _ = _tracker.Reset(TestData.Json("{}"));
        Apply("""{"type":"chat/turnStarted","turnId":"turn-2"}""").ShouldBeEmpty();
        Apply("""{"type":"chat/toolCallStart","turnId":"turn-2","toolCallId":"tc","toolName":"run","displayName":"Terminal"}""").ShouldBeEmpty();

        var added = Apply("""{"type":"chat/toolCallReady","turnId":"turn-2","toolCallId":"tc","invocationMessage":"rm -rf obj"}""");

        added.Single().Title.ShouldBe("Terminal");
        Apply("""{"type":"chat/toolCallReady","turnId":"turn-2","toolCallId":"tc","invocationMessage":"rm -rf obj"}""").ShouldBeEmpty();
        _tracker.IsPending("ToolConfirmation:tc").ShouldBeTrue();

        _ = Apply("""{"type":"chat/toolCallConfirmed","turnId":"turn-2","toolCallId":"tc","approved":true}""");
        _tracker.IsPending("ToolConfirmation:tc").ShouldBeFalse();
    }

    [Fact]
    public void AutoConfirmedToolIsNeverPending()
    {
        _ = _tracker.Reset(TestData.Json("{}"));

        Apply("""{"type":"chat/toolCallReady","turnId":"t","toolCallId":"tc","invocationMessage":"x","confirmed":"not-needed"}""").ShouldBeEmpty();
        _tracker.IsPending("ToolConfirmation:tc").ShouldBeFalse();
    }

    [Fact]
    public void InputRequestIsRemovedByCompletionOrTurnEnd()
    {
        _ = _tracker.Reset(TestData.Json("{}"));
        _ = Apply("""{"type":"chat/turnStarted","turnId":"t1"}""");
        Apply("""{"type":"chat/inputRequested","request":{"id":"r1","message":"?"}}""").Single().TurnId.ShouldBe("t1");
        _ = Apply("""{"type":"chat/inputRequested","request":{"id":"r2","message":"?"}}""");

        _ = Apply("""{"type":"chat/inputCompleted","requestId":"r1","response":"accept"}""");
        _tracker.IsPending("InputRequest:r1").ShouldBeFalse();

        _ = Apply("""{"type":"chat/turnCancelled","turnId":"t1"}""");
        _tracker.IsPending("InputRequest:r2").ShouldBeFalse();
    }

    [Fact]
    public void IgnoresOtherChannelsRejectedActionsAndOtherMethods()
    {
        _ = _tracker.Reset(TestData.Json("{}"));
        const string action = """{"type":"chat/inputRequested","request":{"id":"r"}}""";

        _tracker.Apply(new AhpNotification("action", TestData.Json($$"""{"channel":"ahp-chat:/other","action":{{action}}}"""))).ShouldBeEmpty();
        _tracker.Apply(new AhpNotification("action", TestData.Json($$"""{"channel":"{{TestData.ChatChannel}}","rejectionReason":"x","action":{{action}}}"""))).ShouldBeEmpty();
        _tracker.Apply(new AhpNotification("other", TestData.Json($$"""{"channel":"{{TestData.ChatChannel}}","action":{{action}}}"""))).ShouldBeEmpty();
        _tracker.IsPending("InputRequest:r").ShouldBeFalse();
    }

    private IReadOnlyList<PendingInteraction> Apply(string action) =>
        _tracker.Apply(new AhpNotification("action", TestData.Json($$"""{"channel":"{{TestData.ChatChannel}}","serverSeq":1,"action":{{action}}}""")));
}
