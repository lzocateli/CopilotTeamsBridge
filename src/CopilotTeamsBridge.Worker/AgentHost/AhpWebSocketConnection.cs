using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CopilotTeamsBridge.Worker.AgentHost;

/// <summary>
/// Cliente AHP minimo sobre <see cref="ClientWebSocket"/> e <see cref="System.Text.Json"/>,
/// sem dependencias adicionais. O token de conexao vai apenas na URI do upgrade e nunca e registrado.
/// </summary>
internal sealed class AhpWebSocketConnection : IAhpConnection
{
    private const int MaxMessageBytes = 32 * 1024 * 1024;

    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<AhpNotification> _notifications =
        Channel.CreateUnbounded<AhpNotification>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private Task _receiveLoop = Task.CompletedTask;
    private long _nextId;
    private volatile bool _closed;

    public ChannelReader<AhpNotification> Notifications => _notifications.Reader;

    public async Task ConnectAsync(Uri endpoint, string connectionToken, CancellationToken cancellationToken)
    {
        var builder = new UriBuilder(endpoint)
        {
            Query = $"{AhpProtocol.TokenQueryParameter}={Uri.EscapeDataString(connectionToken)}",
        };

        await _socket.ConnectAsync(builder.Uri, cancellationToken);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_lifetime.Token), CancellationToken.None);
    }

    public async Task<JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        if (_closed)
            _ = completion.TrySetException(new AhpConnectionClosedException());

        try
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            };

            await SendAsync(message, cancellationToken);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task;
        }
        finally
        {
            _ = _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
        };

        return SendAsync(message, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        // Cancelar um ReceiveAsync pendente aborta o ClientWebSocket; por isso o fechamento
        // ordenado vem antes do cancelamento, que fica apenas como limite de tempo.
        if (_socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
                await _receiveLoop.WaitAsync(timeout.Token);
            }
            catch (WebSocketException)
            {
                // Encerramento best-effort: o host pode ter fechado a conexao antes.
            }
            catch (OperationCanceledException)
            {
                // Encerramento best-effort limitado por timeout.
            }
        }

        await _lifetime.CancelAsync();
        await _receiveLoop;
        _socket.Dispose();
        _sendLock.Dispose();
        _lifetime.Dispose();
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _ = _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer.GetMemory(16 * 1024), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                buffer.Advance(result.Count);
                if (buffer.WrittenCount > MaxMessageBytes)
                    throw new AhpProtocolException("Mensagem AHP acima do limite aceito.");

                if (!result.EndOfMessage)
                    continue;

                Route(buffer.WrittenMemory);
                buffer.Clear();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Encerramento solicitado.
        }
        catch (Exception exception) when (exception is WebSocketException or AhpProtocolException or JsonException)
        {
            failure = exception;
        }
        finally
        {
            Complete(failure);
        }
    }

    private void Route(ReadOnlyMemory<byte> message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
        {
            CompleteRequest(idElement.GetInt64(), root);
            return;
        }

        if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
        {
            var parameters = root.TryGetProperty("params", out var value) ? value.Clone() : default;
            _ = _notifications.Writer.TryWrite(new AhpNotification(method.GetString()!, parameters));
        }
    }

    private void CompleteRequest(long id, JsonElement root)
    {
        if (!_pending.TryGetValue(id, out var completion))
            return;

        if (root.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsed) ? parsed : 0;
            var text = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
            _ = completion.TrySetException(new AhpProtocolException(code, text ?? "Erro JSON-RPC sem mensagem."));
            return;
        }

        _ = completion.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
    }

    private void Complete(Exception? failure)
    {
        _closed = true;
        _ = _notifications.Writer.TryComplete(failure is null ? null : new AhpConnectionClosedException("Conexao AHP interrompida.", failure));
        foreach (var completion in _pending.Values)
            _ = completion.TrySetException(new AhpConnectionClosedException());
    }
}
