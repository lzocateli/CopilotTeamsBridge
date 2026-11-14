using System.Text.Json;
using CopilotTeamsBridge.Worker.Configuration;
using CopilotTeamsBridge.Worker.Interactions;
using Microsoft.Extensions.Options;

namespace CopilotTeamsBridge.Worker.Tests;

internal static class TestData
{
    public const string ChatChannel = "ahp-chat:/chat-1";
    public const string ObjectId = "11111111-1111-1111-1111-111111111111";
    public const string TenantId = "22222222-2222-2222-2222-222222222222";
    public const string WebhookUrl = "https://workflow.example.test/trigger?sig=secret";

    public static TeamsBridgeOptions Options(Action<TeamsBridgeOptions>? configure = null)
    {
        var options = new TeamsBridgeOptions
        {
            DryRun = false,
            CorrelationKeyFile = "/secrets/correlation.key",
            AgentHost = new AgentHostOptions
            {
                Endpoint = "ws://127.0.0.1:40179/",
                TokenFile = "/secrets/agent-host.token",
                ChatChannel = ChatChannel,
                EchoTimeout = TimeSpan.FromSeconds(2),
                ReconnectDelay = TimeSpan.FromMilliseconds(10),
            },
            Workflow = new WorkflowOptions
            {
                WebhookUrl = WebhookUrl,
                AllowedPollingHosts = ["poll.example.test"],
                AuthMode = WorkflowAuthModes.Bearer,
                BearerTokenFile = "/secrets/workflow.bearer",
                PollInterval = TimeSpan.FromSeconds(10),
            },
            Responder = new ResponderOptions { AllowedObjectId = ObjectId, AllowedTenantId = TenantId },
        };

        configure?.Invoke(options);
        return options;
    }

    public static IOptions<TeamsBridgeOptions> Wrap(TeamsBridgeOptions options) => Microsoft.Extensions.Options.Options.Create(options);

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static PendingInteraction ToolConfirmation(string id = "tool-1", string message = "Executar dotnet test") =>
        new(InteractionKind.ToolConfirmation, id, "turn-1", "Executar comando", message, [], RequiresExternalUrl: false);

    public static PendingInteraction Input(params InteractionQuestion[] questions) =>
        new(InteractionKind.InputRequest, "req-1", "turn-1", "Pergunta pendente", "Escolha o ambiente", questions, RequiresExternalUrl: false);

    public static InteractionQuestion Question(string id, QuestionKind kind, bool required = true, double? min = null, double? max = null) =>
        new(id, kind, "Titulo", "Mensagem", required, [new QuestionOption("a", "Opcao A"), new QuestionOption("b", "Opcao B")], min, max);
}

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan delta) => Now = Now.Add(delta);
}
