namespace CopilotTeamsBridge.Worker.Interactions;

internal enum InteractionKind
{
    InputRequest = 1,
    ToolConfirmation = 2,
}

internal enum QuestionKind
{
    Unsupported = 0,
    Text = 1,
    Numeric = 2,
    WholeNumber = 3,
    Toggle = 4,
    SingleChoice = 5,
    MultiChoice = 6,
}

internal sealed record QuestionOption(string Id, string Label);

internal sealed record InteractionQuestion(
    string Id,
    QuestionKind Kind,
    string? Title,
    string Message,
    bool Required,
    IReadOnlyList<QuestionOption> Options,
    double? Min,
    double? Max);

/// <summary>
/// Pedido nativo ainda pendente no chat do Agent Host: uma elicitation
/// (<c>chat/inputRequested</c>) ou uma tool call em <c>pending-confirmation</c>.
/// </summary>
internal sealed record PendingInteraction(
    InteractionKind Kind,
    string Id,
    string? TurnId,
    string Title,
    string Message,
    IReadOnlyList<InteractionQuestion> Questions,
    bool RequiresExternalUrl)
{
    public string Key => $"{Kind}:{Id}";
}

/// <summary>Resultados internos do contrato de resposta (contracts/interactions.md).</summary>
internal enum InteractionOutcome
{
    Accepted = 1,
    Duplicate = 2,
    Forbidden = 3,
    Expired = 4,
    StaleContext = 5,
    RuntimeRequestGone = 6,
    DeliveryUnavailable = 7,
    Invalid = 8,
    Unsupported = 9,
    Unconfirmed = 10,
    DryRun = 11,
}
