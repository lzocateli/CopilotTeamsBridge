using System.Globalization;
using System.Text.Json;
using CopilotTeamsBridge.Worker.Interactions;

namespace CopilotTeamsBridge.Worker.AgentHost;

/// <summary>Converte fragmentos do estado/acoes AHP nos modelos minimos do adaptador.</summary>
internal static class AhpStateReader
{
    public static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool HasProperty(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    /// <summary>Le um <c>StringOrMarkdown</c>: string simples ou objeto com <c>markdown</c>.</summary>
    public static string ReadText(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return string.Empty;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Object => GetString(value, "markdown") ?? GetString(value, "plain") ?? string.Empty,
            _ => string.Empty,
        };
    }

    public static PendingInteraction? ReadInputRequest(JsonElement request, string? turnId)
    {
        var id = GetString(request, "id");
        if (string.IsNullOrEmpty(id))
            return null;

        var questions = new List<InteractionQuestion>();
        if (request.TryGetProperty("questions", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
                questions.Add(ReadQuestion(item));
        }

        return new PendingInteraction(
            InteractionKind.InputRequest,
            id,
            turnId,
            "Pergunta pendente do Copilot",
            GetString(request, "message") ?? string.Empty,
            questions,
            HasProperty(request, "url"));
    }

    public static PendingInteraction? ReadToolConfirmation(JsonElement toolCall, string? turnId, string? displayName)
    {
        var id = GetString(toolCall, "toolCallId");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(turnId))
            return null;

        var title = ReadText(toolCall, "confirmationTitle");
        if (title.Length == 0)
            title = GetString(toolCall, "displayName") ?? displayName ?? "Confirmacao de ferramenta";

        return new PendingInteraction(
            InteractionKind.ToolConfirmation,
            id,
            turnId,
            title,
            ReadText(toolCall, "invocationMessage"),
            [],
            RequiresExternalUrl: false);
    }

    private static InteractionQuestion ReadQuestion(JsonElement item)
    {
        var options = new List<QuestionOption>();
        if (item.TryGetProperty("options", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in values.EnumerateArray())
                options.Add(new QuestionOption(GetString(option, "id") ?? string.Empty, GetString(option, "label") ?? string.Empty));
        }

        return new InteractionQuestion(
            GetString(item, "id") ?? string.Empty,
            MapKind(GetString(item, "kind")),
            GetString(item, "title"),
            GetString(item, "message") ?? string.Empty,
            item.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True,
            options,
            ReadNumber(item, "min"),
            ReadNumber(item, "max"));
    }

    private static QuestionKind MapKind(string? kind) => kind switch
    {
        "text" => QuestionKind.Text,
        "number" => QuestionKind.Numeric,
        "integer" => QuestionKind.WholeNumber,
        "boolean" => QuestionKind.Toggle,
        "single-select" => QuestionKind.SingleChoice,
        "multi-select" => QuestionKind.MultiChoice,
        _ => QuestionKind.Unsupported,
    };

    private static double? ReadNumber(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    public static string FormatNumber(double value) => value.ToString(CultureInfo.InvariantCulture);
}
