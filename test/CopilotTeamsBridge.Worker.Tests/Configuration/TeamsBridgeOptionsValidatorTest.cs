using CopilotTeamsBridge.Worker.Configuration;
using Shouldly;
using Xunit;

namespace CopilotTeamsBridge.Worker.Tests.Configuration;

[Trait("Category", "Unit")]
public sealed class TeamsBridgeOptionsValidatorTest
{
    private readonly TeamsBridgeOptionsValidator _validator = new();

    [Fact]
    public void AcceptsCompleteLoopbackConfiguration()
    {
        _validator.Validate(null, TestData.Options()).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("ws://10.0.0.5:40179/")]
    [InlineData("http://127.0.0.1:40179/")]
    [InlineData("ws://user:pass@127.0.0.1:40179/")]
    [InlineData("ws://127.0.0.1:40179/?tkn=abc")]
    [InlineData("not-a-uri")]
    public void RejectsNonLoopbackOrCredentialedEndpoints(string endpoint)
    {
        var result = _validator.Validate(null, TestData.Options(options => options.AgentHost.Endpoint = endpoint));

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotContain(endpoint);
    }

    [Fact]
    public void RejectsChatChannelOutsideAgentHostScheme()
    {
        _validator.Validate(null, TestData.Options(options => options.AgentHost.ChatChannel = "ahp-session:/x"))
            .Failed.ShouldBeTrue();
    }

    [Fact]
    public void RequiresAuthorizedResponderOutsideDryRun()
    {
        var options = TestData.Options(options => options.Responder.AllowedObjectId = "someone");

        _validator.Validate(null, options).Failed.ShouldBeTrue();

        options.DryRun = true;
        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://workflow.example.test/trigger")]
    [InlineData("https://workflow.example.test/trigger#fragment")]
    [InlineData("relative/path")]
    public void RequiresHttpsWebhookUrlOutsideDryRun(string webhookUrl)
    {
        var options = TestData.Options(options => options.Workflow.WebhookUrl = webhookUrl);

        _validator.Validate(null, options).Failed.ShouldBeTrue();

        options.DryRun = true;
        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void RequiresBearerTokenFileInBearerMode()
    {
        _validator.Validate(null, TestData.Options(options => options.Workflow.BearerTokenFile = string.Empty))
            .Failed.ShouldBeTrue();
        _validator.Validate(null, TestData.Options(options =>
        {
            options.Workflow.BearerTokenFile = string.Empty;
            options.Workflow.AuthMode = WorkflowAuthModes.UrlSecret;
        })).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 1500)]
    [InlineData(120, 1500)]
    [InlineData(15, 100)]
    [InlineData(15, 5000)]
    public void RejectsOutOfRangeLimits(int ttlMinutes, int maxText)
    {
        _validator.Validate(null, TestData.Options(options =>
        {
            options.RequestTimeToLive = TimeSpan.FromMinutes(ttlMinutes);
            options.MaxCardTextLength = maxText;
        })).Failed.ShouldBeTrue();
    }
}
