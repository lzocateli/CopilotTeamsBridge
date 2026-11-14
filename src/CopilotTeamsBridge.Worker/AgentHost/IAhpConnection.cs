using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CopilotTeamsBridge.Worker.AgentHost;

internal sealed record AhpNotification(string Method, JsonElement Parameters);

/// <summary>Conexao JSON-RPC 2.0 com o Agent Host, restrita ao loopback.</summary>
internal interface IAhpConnection : IAsyncDisposable
{
    ChannelReader<AhpNotification> Notifications { get; }

    /// <summary>Abre o WebSocket autenticado pelo token de conexao.</summary>
    /// <param name="endpoint">Endpoint loopback do Agent Host.</param>
    /// <param name="connectionToken">Token privado de conexao; nunca registrado.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns>Tarefa concluida apos o handshake.</returns>
    Task ConnectAsync(Uri endpoint, string connectionToken, CancellationToken cancellationToken);

    /// <summary>Envia uma requisicao JSON-RPC e aguarda o resultado.</summary>
    /// <param name="method">Metodo AHP.</param>
    /// <param name="parameters">Parametros do metodo.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns>Elemento <c>result</c> da resposta.</returns>
    /// <exception cref="AhpProtocolException">O host respondeu com erro JSON-RPC.</exception>
    /// <exception cref="AhpConnectionClosedException">A conexao foi encerrada antes da resposta.</exception>
    Task<JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken);

    /// <summary>Envia uma notificacao JSON-RPC sem resposta direta.</summary>
    /// <param name="method">Metodo AHP.</param>
    /// <param name="parameters">Parametros do metodo.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns>Tarefa concluida apos o envio.</returns>
    Task NotifyAsync(string method, JsonObject parameters, CancellationToken cancellationToken);
}

internal interface IAhpConnectionFactory
{
    IAhpConnection Create();
}

internal sealed class AhpConnectionFactory : IAhpConnectionFactory
{
    public IAhpConnection Create() => new AhpWebSocketConnection();
}

public sealed class AhpProtocolException : Exception
{
    public AhpProtocolException()
    {
    }

    public AhpProtocolException(string message)
        : base(message)
    {
    }

    public AhpProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AhpProtocolException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    public int Code { get; }
}

public sealed class AhpConnectionClosedException : Exception
{
    public AhpConnectionClosedException()
        : base("Conexao com o Agent Host encerrada.")
    {
    }

    public AhpConnectionClosedException(string message)
        : base(message)
    {
    }

    public AhpConnectionClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
