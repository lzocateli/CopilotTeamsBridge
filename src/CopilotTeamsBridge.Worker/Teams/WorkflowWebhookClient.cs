using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotTeamsBridge.Worker.Configuration;
using Microsoft.Extensions.Options;

namespace CopilotTeamsBridge.Worker.Teams;

internal enum WebhookDeliveryStatus
{
    AwaitingResponse = 1,
    AcceptedWithoutReturn = 2,
    Unavailable = 3,
    Rejected = 4,
}

internal sealed record WebhookDelivery(WebhookDeliveryStatus Status, Uri? StatusLocation, int HttpStatus);

internal enum WorkflowPollStatus
{
    Running = 1,
    Completed = 2,
    Failed = 3,
}

internal sealed record WorkflowPollResult(WorkflowPollStatus Status, string? Body, TimeSpan? RetryAfter, int HttpStatus);

/// <summary>
/// Publica o cartao no trigger do Teams Workflows e acompanha a resposta assincrona
/// (HTTP 202 + <c>Location</c>) somente por trafego de saida, sem Graph e sem ingress.
/// </summary>
internal interface IWorkflowWebhookClient
{
    /// <summary>Envia o payload ao trigger aprovado, sem seguir redirects nem repetir automaticamente.</summary>
    /// <param name="payload">Envelope de mensagem com Adaptive Card.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns>Resultado da entrega; <see cref="WebhookDeliveryStatus.AwaitingResponse"/> traz a URL de acompanhamento validada.</returns>
    Task<WebhookDelivery> PostAsync(JsonObject payload, CancellationToken cancellationToken);

    /// <summary>Consulta a URL de acompanhamento devolvida pela resposta assincrona do Workflow.</summary>
    /// <param name="location">URL validada em <see cref="PostAsync"/>.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns>Estado da execucao e, quando concluida, o corpo devolvido pela acao Response.</returns>
    Task<WorkflowPollResult> PollAsync(Uri location, CancellationToken cancellationToken);
}

internal sealed class WorkflowWebhookClient(
    IHttpClientFactory httpClientFactory,
    ISecretFileReader secretReader,
    IOptions<TeamsBridgeOptions> options) : IWorkflowWebhookClient
{
    public const string HttpClientName = "TeamsWorkflow";
    private const int MaxResponseBytes = 64 * 1024;
    private readonly WorkflowOptions _options = options.Value.Workflow;

    public async Task<WebhookDelivery> PostAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        var endpoint = _options.WebhookUri
            ?? throw new InvalidOperationException("Workflow:WebhookUrl ausente ou sem HTTPS.");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        if (string.Equals(_options.AuthMode, WorkflowAuthModes.Bearer, StringComparison.Ordinal))
        {
            var token = await secretReader.ReadTextAsync(_options.BearerTokenFile, cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return ToDelivery(response);
    }

    public async Task<WorkflowPollResult> PollAsync(Uri location, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, location);
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.Accepted)
            return new WorkflowPollResult(WorkflowPollStatus.Running, null, response.Headers.RetryAfter?.Delta, status);

        if (!response.IsSuccessStatusCode)
            return new WorkflowPollResult(WorkflowPollStatus.Failed, null, null, status);

        var body = await ReadLimitedAsync(response.Content, cancellationToken);
        return body is null
            ? new WorkflowPollResult(WorkflowPollStatus.Failed, null, null, status)
            : new WorkflowPollResult(WorkflowPollStatus.Completed, body, null, status);
    }

    internal static Uri? ParseApprovedUri(string? value, IEnumerable<string> allowedHosts)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0)
            return null;

        return allowedHosts.Any(host => string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)) ? uri : null;
    }

    private WebhookDelivery ToDelivery(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500)
            return new WebhookDelivery(WebhookDeliveryStatus.Unavailable, null, status);

        if (!response.IsSuccessStatusCode)
            return new WebhookDelivery(WebhookDeliveryStatus.Rejected, null, status);

        var pollingHosts = _options.AllowedPollingHosts.Count == 0 ? [_options.WebhookUri!.Host] : _options.AllowedPollingHosts;
        var location = response.StatusCode == HttpStatusCode.Accepted
            ? ParseApprovedUri(response.Headers.Location?.ToString(), pollingHosts)
            : null;

        return location is null
            ? new WebhookDelivery(WebhookDeliveryStatus.AcceptedWithoutReturn, null, status)
            : new WebhookDelivery(WebhookDeliveryStatus.AwaitingResponse, location, status);
    }

    private static async Task<string?> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxResponseBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
            total += read;

        if (total > MaxResponseBytes)
            return null;

        var text = Encoding.UTF8.GetString(buffer, 0, total);
        return IsJsonObject(text) ? text : null;
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
