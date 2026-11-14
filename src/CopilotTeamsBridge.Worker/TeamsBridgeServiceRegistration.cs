using System.Net;
using CopilotTeamsBridge.Worker.AgentHost;
using CopilotTeamsBridge.Worker.Bridge;
using CopilotTeamsBridge.Worker.Configuration;
using CopilotTeamsBridge.Worker.Discovery;
using CopilotTeamsBridge.Worker.Security;
using CopilotTeamsBridge.Worker.Teams;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CopilotTeamsBridge.Worker;

/// <summary>Composition root do adaptador Teams do Agent Host.</summary>
internal static class TeamsBridgeServiceRegistration
{
    public const string EnvironmentPrefix = "TEAMSBRIDGE_";
    public const string LocalSettingsFile = "appsettings.Local.json";

    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        // appsettings.json acompanha o binario; o diretorio atual do processo nao e confiavel.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        // Precedencia: appsettings.json < appsettings.Local.json < TEAMSBRIDGE_* < argumentos.
        _ = builder.Configuration
            .AddJsonFile(LocalSettingsFile, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(EnvironmentPrefix)
            .AddCommandLine(args);
        _ = builder.Services.AddTeamsBridge();
        return builder;
    }

    public static IServiceCollection AddTeamsBridge(this IServiceCollection services)
    {
        _ = services.AddOptions<TeamsBridgeOptions>()
            .BindConfiguration(TeamsBridgeOptions.SectionName)
            .ValidateOnStart();
        _ = services.AddSingleton<IValidateOptions<TeamsBridgeOptions>, TeamsBridgeOptionsValidator>();

        _ = services.AddHttpClient(WorkflowWebhookClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(CreatePrimaryHandler);

        _ = services.AddSingleton(TimeProvider.System);
        _ = services.AddSingleton<ISecretFileReader, SecretFileReader>();
        _ = services.AddSingleton<ICorrelationTokenService>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<TeamsBridgeOptions>>().Value;
            var key = CorrelationKey.Parse(provider.GetRequiredService<ISecretFileReader>().ReadText(options.CorrelationKeyFile));
            return new CorrelationTokenService(key, provider.GetRequiredService<TimeProvider>());
        });
        _ = services.AddSingleton<IAhpConnectionFactory, AhpConnectionFactory>();
        _ = services.AddSingleton<IChatDiscovery, ChatDiscovery>();
        _ = services.AddSingleton<IAgentHostSession, AgentHostSession>();
        _ = services.AddSingleton<IPendingInteractionTracker, PendingInteractionTracker>();
        _ = services.AddSingleton<IAdaptiveCardBuilder, AdaptiveCardBuilder>();
        _ = services.AddSingleton<IWorkflowWebhookClient, WorkflowWebhookClient>();
        _ = services.AddSingleton<ITeamsResponseValidator, TeamsResponseValidator>();
        _ = services.AddSingleton<IInteractionBridge, InteractionBridge>();
        _ = services.AddHostedService<TeamsBridgeWorker>();
        return services;
    }

    /// <summary>A URL de acompanhamento e validada pelo cliente; redirects e cookies nunca sao usados.</summary>
    internal static SocketsHttpHandler CreatePrimaryHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
    };
}
