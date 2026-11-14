using System.Net;
using Microsoft.Extensions.Options;

namespace CopilotTeamsBridge.Worker.Configuration;

/// <summary>
/// Recusa configuracoes que exporiam o Agent Host fora do loopback, enviariam dados
/// por HTTP sem TLS ou aceitariam respondentes nao identificados.
/// </summary>
internal sealed class TeamsBridgeOptionsValidator : IValidateOptions<TeamsBridgeOptions>
{
    private const string ChatChannelPrefix = "ahp-chat:/";

    public ValidateOptionsResult Validate(string? name, TeamsBridgeOptions options)
    {
        var failures = new List<string>();
        ValidateAgentHost(options.AgentHost, failures);
        ValidateGeneral(options, failures);

        if (!options.DryRun)
        {
            ValidateWorkflow(options.Workflow, failures);
            ValidateResponder(options.Responder, failures);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static bool IsLoopbackWebSocket(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeWs && uri.Scheme != Uri.UriSchemeWss)
            return false;

        if (uri.UserInfo.Length != 0 || uri.Query.Length != 0)
            return false;

        return uri.IsLoopback || (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address));
    }

    private static void ValidateAgentHost(AgentHostOptions agentHost, List<string> failures)
    {
        if (!IsLoopbackWebSocket(agentHost.Endpoint))
            failures.Add("AgentHost:Endpoint deve ser ws/wss em loopback, sem userinfo ou query.");

        if (string.IsNullOrWhiteSpace(agentHost.TokenFile))
            failures.Add("AgentHost:TokenFile e obrigatorio.");

        if (!agentHost.ChatChannel.StartsWith(ChatChannelPrefix, StringComparison.Ordinal)
            || agentHost.ChatChannel.Length == ChatChannelPrefix.Length)
            failures.Add("AgentHost:ChatChannel deve referenciar o chat existente (ahp-chat:/<id>).");

        if (agentHost.EchoTimeout <= TimeSpan.Zero || agentHost.ReconnectDelay <= TimeSpan.Zero)
            failures.Add("AgentHost:EchoTimeout e AgentHost:ReconnectDelay devem ser positivos.");
    }

    private static void ValidateGeneral(TeamsBridgeOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.CorrelationKeyFile))
            failures.Add("CorrelationKeyFile e obrigatorio.");

        if (options.RequestTimeToLive < TimeSpan.FromMinutes(1) || options.RequestTimeToLive > TimeSpan.FromHours(1))
            failures.Add("RequestTimeToLive deve ficar entre 1 e 60 minutos.");

        if (options.MaxCardTextLength is < 200 or > 4000)
            failures.Add("MaxCardTextLength deve ficar entre 200 e 4000.");
    }

    private static void ValidateWorkflow(WorkflowOptions workflow, List<string> failures)
    {
        if (workflow.WebhookUri is null)
            failures.Add("Workflow:WebhookUrl deve ser uma URL HTTPS absoluta, sem userinfo ou fragmento, fora de DryRun.");

        var isBearer = string.Equals(workflow.AuthMode, WorkflowAuthModes.Bearer, StringComparison.Ordinal);
        var isUrlSecret = string.Equals(workflow.AuthMode, WorkflowAuthModes.UrlSecret, StringComparison.Ordinal);
        if (!isBearer && !isUrlSecret)
            failures.Add("Workflow:AuthMode deve ser 'bearer' ou 'url-secret'.");

        if (isBearer && string.IsNullOrWhiteSpace(workflow.BearerTokenFile))
            failures.Add("Workflow:BearerTokenFile e obrigatorio no modo bearer.");

        if (workflow.PollInterval < TimeSpan.FromSeconds(2))
            failures.Add("Workflow:PollInterval deve ser de pelo menos 2 segundos.");
    }

    private static void ValidateResponder(ResponderOptions responder, List<string> failures)
    {
        if (!Guid.TryParse(responder.AllowedObjectId, out _))
            failures.Add("Responder:AllowedObjectId deve ser o object ID Entra do respondente autorizado.");

        if (!Guid.TryParse(responder.AllowedTenantId, out _))
            failures.Add("Responder:AllowedTenantId deve ser o tenant ID Entra autorizado.");
    }
}
