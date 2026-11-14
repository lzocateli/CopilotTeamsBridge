using System.Text.Json.Nodes;
using CopilotTeamsBridge.Worker.Interactions;

namespace CopilotTeamsBridge.Worker.Teams;

internal sealed partial class AdaptiveCardBuilder
{
    private static JsonObject BuildInput(InteractionQuestion question)
    {
        var label = Sanitize(question.Title ?? question.Message, MaxLabelLength).Text;
        var input = question.Kind switch
        {
            QuestionKind.Numeric or QuestionKind.WholeNumber => NumberInput(question),
            QuestionKind.Toggle => new JsonObject
            {
                ["type"] = "Input.Toggle",
                ["title"] = Sanitize(question.Message, MaxLabelLength).Text,
                ["valueOn"] = "true",
                ["valueOff"] = "false",
                ["value"] = "false",
            },
            QuestionKind.SingleChoice or QuestionKind.MultiChoice => ChoiceInput(question),
            _ => new JsonObject { ["type"] = "Input.Text", ["maxLength"] = 1000 },
        };

        input["id"] = question.Id;
        input["label"] = label;
        input["isRequired"] = question.Required;
        if (question.Required)
            input["errorMessage"] = "Resposta obrigatoria.";

        return input;
    }

    private static JsonObject NumberInput(InteractionQuestion question)
    {
        var input = new JsonObject { ["type"] = "Input.Number" };
        if (question.Min is { } min)
            input["min"] = min;

        if (question.Max is { } max)
            input["max"] = max;

        return input;
    }

    private static JsonObject ChoiceInput(InteractionQuestion question)
    {
        var choices = new JsonArray();
        foreach (var option in question.Options)
        {
            choices.Add(new JsonObject
            {
                ["title"] = Sanitize(option.Label, MaxLabelLength).Text,
                ["value"] = option.Id,
            });
        }

        return new JsonObject
        {
            ["type"] = "Input.ChoiceSet",
            ["style"] = "expanded",
            ["isMultiSelect"] = question.Kind == QuestionKind.MultiChoice,
            ["choices"] = choices,
        };
    }
}
