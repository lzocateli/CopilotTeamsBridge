namespace CopilotTeamsBridge.Worker.Configuration;

/// <summary>
/// Configuracao do adaptador que publica pedidos pendentes da sessao nativa do Agent Host
/// em um Workflow do Teams e devolve a resposta validada ao mesmo pedido.
/// Tokens e chaves sao lidos de arquivos privados; a URL do webhook vem da configuracao
/// (use <c>appsettings.Local.json</c>, fora do controle de versao, ou variavel de ambiente).
/// </summary>
internal sealed class TeamsBridgeOptions
{
    public const string SectionName = "TeamsBridge";

    public AgentHostOptions AgentHost { get; set; } = new();

    public WorkflowOptions Workflow { get; set; } = new();

    public ResponderOptions Responder { get; set; } = new();

    /// <summary>Arquivo privado com a chave HMAC (minimo de 32 bytes) usada na referencia opaca do cartao.</summary>
    public string CorrelationKeyFile { get; set; } = string.Empty;

    public TimeSpan RequestTimeToLive { get; set; } = TimeSpan.FromMinutes(15);

    public int MaxCardTextLength { get; set; } = 1500;

    /// <summary>
    /// Quando verdadeiro (padrao), observa e registra pedidos sem publicar no Teams.
    /// O envio real exige <c>false</c> explicito na configuracao.
    /// </summary>
    public bool DryRun { get; set; } = true;
}

internal sealed class AgentHostOptions
{
    /// <summary>Endpoint WebSocket do Agent Host; somente loopback e aceito.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Arquivo privado com o token de conexao do Agent Host.</summary>
    public string TokenFile { get; set; } = string.Empty;

    /// <summary>Canal do chat existente, no formato <c>ahp-chat:/&lt;id&gt;</c>.</summary>
    public string ChatChannel { get; set; } = string.Empty;

    public TimeSpan EchoTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);
}

internal sealed class WorkflowOptions
{
    /// <summary>
    /// URL HTTPS do trigger <c>When a Teams webhook request is received</c>. Contem assinatura de acesso:
    /// trate como secret e nao versione o valor real.
    /// </summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Hostnames aprovados para a URL de acompanhamento devolvida pela resposta assincrona.
    /// Vazio aceita somente o host da <see cref="WebhookUrl"/>.
    /// </summary>
    public IList<string> AllowedPollingHosts { get; set; } = [];

    /// <summary><c>bearer</c> ou <c>url-secret</c> (somente para trigger <c>Anyone</c> aprovado).</summary>
    public string AuthMode { get; set; } = WorkflowAuthModes.Bearer;

    /// <summary>Arquivo privado com o bearer token obtido por fluxo autorizado.</summary>
    public string BearerTokenFile { get; set; } = string.Empty;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>URL do webhook validada (HTTPS, sem userinfo nem fragmento) ou <c>null</c> quando invalida.</summary>
    internal Uri? WebhookUri =>
        Uri.TryCreate(WebhookUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0
        && uri.Fragment.Length == 0
            ? uri
            : null;
}

internal sealed class ResponderOptions
{
    /// <summary>Object ID Entra do unico respondente autorizado.</summary>
    public string AllowedObjectId { get; set; } = string.Empty;

    /// <summary>Tenant ID Entra do respondente autorizado.</summary>
    public string AllowedTenantId { get; set; } = string.Empty;
}

internal static class WorkflowAuthModes
{
    public const string Bearer = "bearer";
    public const string UrlSecret = "url-secret";
}
