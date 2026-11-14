using CopilotTeamsBridge.Worker.Interactions;
using CopilotTeamsBridge.Worker.Teams;
using Microsoft.Extensions.Logging;

namespace CopilotTeamsBridge.Worker.Bridge;

internal sealed partial class InteractionBridge
{
    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "Pedido {Kind} {Fingerprint} finalizado com resultado {Outcome}.")]
    private partial void LogOutcome(InteractionKind kind, string fingerprint, InteractionOutcome outcome);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Warning, Message = "Entrega ao Workflow sem retorno utilizavel: {Status} (HTTP {HttpStatus}).")]
    private partial void LogDelivery(WebhookDeliveryStatus? status, int httpStatus);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Warning, Message = "Falha de transporte com o Workflow: {ExceptionType}.")]
    private partial void LogTransportFailure(string exceptionType);
}
