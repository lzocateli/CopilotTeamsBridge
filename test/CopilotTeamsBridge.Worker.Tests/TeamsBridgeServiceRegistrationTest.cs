using CopilotTeamsBridge.Worker.Bridge;
using CopilotTeamsBridge.Worker.Configuration;
using CopilotTeamsBridge.Worker.Teams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CopilotTeamsBridge.Worker.Tests;

[Trait("Category", "Unit")]
public sealed class TeamsBridgeServiceRegistrationTest
{
    [Fact]
    public void RegistersWorkerAndHttpClientWithoutRedirects()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "UnitTest" });
        _ = builder.Services.AddTeamsBridge();
        using var provider = builder.Services.BuildServiceProvider();

        _ = builder.Services.Where(item => item.ServiceType == typeof(IHostedService) && item.ImplementationType == typeof(TeamsBridgeWorker))
            .ShouldHaveSingleItem();
        var handlers = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(WorkflowWebhookClient.HttpClientName);
        var primary = FindPrimary(handlers);
        primary.AllowAutoRedirect.ShouldBeFalse();
        primary.UseCookies.ShouldBeFalse();
    }

    [Fact]
    public void InvalidConfigurationFailsOnStart()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "UnitTest" });
        builder.Configuration["TeamsBridge:AgentHost:Endpoint"] = "ws://10.0.0.5:1/";
        _ = builder.Services.AddTeamsBridge();
        using var provider = builder.Services.BuildServiceProvider();

        _ = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TeamsBridgeOptions>>().Value);
    }

    [Fact]
    public void CommandLineOverridesFileConfiguration()
    {
        var builder = TeamsBridgeServiceRegistration.CreateBuilder(["--TeamsBridge:AgentHost:ChatChannel=ahp-chat:/from-args"]);

        builder.Configuration["TeamsBridge:AgentHost:ChatChannel"].ShouldBe("ahp-chat:/from-args");
    }

    private static SocketsHttpHandler FindPrimary(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler!;

        return handler.ShouldBeAssignableTo<SocketsHttpHandler>()!;
    }
}
