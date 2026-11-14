using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotTeamsBridge.Worker.Interactions;

namespace CopilotTeamsBridge.Worker.Teams;

/// <summary>
/// Converte os valores enviados pelo Adaptive Card (sempre texto) em <c>ChatInputAnswer</c> do AHP,
/// rejeitando valores fora do tipo, faixa ou opcoes do pedido original.
/// </summary>
internal static class AnswerMapper
{
    /// <summary>Mapeia as respostas do cartao para o formato <c>answers</c> de <c>chat/inputCompleted</c>.</summary>
    /// <param name="interaction">Pedido original.</param>
    /// <param name="data">Objeto <c>data</c> enviado pelo cartao.</param>
    /// <param name="answers">Respostas tipadas quando validas.</param>
    /// <returns><see langword="false"/> quando alguma resposta e invalida ou obrigatoria esta ausente.</returns>
    public static bool TryMap(PendingInteraction interaction, JsonElement data, out JsonObject answers)
    {
        answers = [];
        foreach (var question in interaction.Questions)
        {
            var raw = ReadRaw(data, question.Id);
            if (string.IsNullOrWhiteSpace(raw))
            {
                if (question.Required)
                    return false;

                answers[question.Id] = new JsonObject { ["state"] = "skipped" };
                continue;
            }

            var value = MapValue(question, raw.Trim());
            if (value is null)
                return false;

            answers[question.Id] = new JsonObject { ["state"] = "submitted", ["value"] = value };
        }

        return true;
    }

    private static string? ReadRaw(JsonElement data, string id)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(id, out var element))
            return null;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            _ => null,
        };
    }

    private static JsonObject? MapValue(InteractionQuestion question, string raw) => question.Kind switch
    {
        QuestionKind.Text => Answer("text", JsonValue.Create(raw)),
        QuestionKind.Numeric or QuestionKind.WholeNumber => MapNumber(question, raw),
        QuestionKind.Toggle => MapToggle(raw),
        QuestionKind.SingleChoice => IsOption(question, raw) ? Answer("selected", JsonValue.Create(raw)) : null,
        QuestionKind.MultiChoice => MapMany(question, raw),
        _ => null,
    };

    private static JsonObject? MapNumber(InteractionQuestion question, string raw)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            return null;

        if ((question.Kind == QuestionKind.WholeNumber && !double.IsInteger(number))
            || (question.Min is { } min && number < min)
            || (question.Max is { } max && number > max))
            return null;

        return Answer("number", JsonValue.Create(number));
    }

    private static JsonObject? MapToggle(string raw)
    {
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
            return Answer("boolean", JsonValue.Create(true));

        return string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)
            ? Answer("boolean", JsonValue.Create(false))
            : null;
    }

    private static JsonObject? MapMany(InteractionQuestion question, string raw)
    {
        var selected = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (selected.Count == 0 || !selected.TrueForAll(item => IsOption(question, item)))
            return null;

        return Answer("selected-many", new JsonArray([.. selected.Select(item => JsonValue.Create(item))]));
    }

    private static bool IsOption(InteractionQuestion question, string id) =>
        question.Options.Any(option => string.Equals(option.Id, id, StringComparison.Ordinal));

    private static JsonObject Answer(string kind, JsonNode? value) => new() { ["kind"] = kind, ["value"] = value };
}
