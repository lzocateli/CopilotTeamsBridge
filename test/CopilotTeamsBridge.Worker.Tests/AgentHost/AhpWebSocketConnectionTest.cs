using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using CopilotTeamsBridge.Worker.AgentHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace CopilotTeamsBridge.Worker.Tests.AgentHost;

/// <summary>
/// Exercita o cliente AHP contra um servidor WebSocket em loopback que simula o Agent Host.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AhpWebSocketConnectionTest : IAsyncLifetime
{
    private const string Token = "test-connection-token";

    private WebApplication? _server;
    private Uri? _endpoint;
    private string? _receivedToken;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        _ = builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        _server = builder.Build();
        _ = _server.UseWebSockets();
        _server.Run(HandleAsync);
        await _server.StartAsync();

        var address = _server.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
        _endpoint = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/");
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
    }

    [Fact]
    public async Task RequestReturnsResultAndSendsTokenOnlyInUpgradeQuery()
    {
        await using var connection = new AhpWebSocketConnection();
        await connection.ConnectAsync(_endpoint!, Token, CancellationToken.None);

        var result = await connection.RequestAsync("ping", new JsonObject { ["value"] = 7 }, CancellationToken.None);

        result.GetProperty("value").GetInt32().ShouldBe(7);
        _receivedToken.ShouldBe(Token);
    }

    [Fact]
    public async Task JsonRpcErrorBecomesProtocolException()
    {
        await using var connection = new AhpWebSocketConnection();
        await connection.ConnectAsync(_endpoint!, Token, CancellationToken.None);

        var exception = await Should.ThrowAsync<AhpProtocolException>(
            () => connection.RequestAsync("fail", [], CancellationToken.None));

        exception.Code.ShouldBe(-32000);
    }

    [Fact]
    public async Task NotificationsAreRoutedToChannel()
    {
        await using var connection = new AhpWebSocketConnection();
        await connection.ConnectAsync(_endpoint!, Token, CancellationToken.None);

        await connection.NotifyAsync("notifyMe", [], CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var notification = await connection.Notifications.ReadAsync(timeout.Token);

        notification.Method.ShouldBe("action");
        notification.Parameters.GetProperty("type").GetString().ShouldBe("chat/turnStarted");
    }

    [Fact]
    public async Task ServerCloseCompletesNotificationsAndFailsPendingRequests()
    {
        await using var connection = new AhpWebSocketConnection();
        await connection.ConnectAsync(_endpoint!, Token, CancellationToken.None);

        _ = await Should.ThrowAsync<AhpConnectionClosedException>(
            () => connection.RequestAsync("close", [], CancellationToken.None));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await connection.Notifications.Completion.WaitAsync(timeout.Token);
        _ = await Should.ThrowAsync<AhpConnectionClosedException>(
            () => connection.RequestAsync("ping", [], CancellationToken.None));
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        _receivedToken = context.Request.Query[AhpProtocol.TokenQueryParameter];
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
                return;
            }

            var request = JsonNode.Parse(buffer.AsSpan(0, received.Count))!.AsObject();
            var method = request["method"]!.GetValue<string>();
            var id = request["id"]?.GetValue<long>();
            switch (method)
            {
                case "close":
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
                    return;
                case "fail":
                    await SendAsync(socket, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "falha" } });
                    break;
                case "notifyMe":
                    await SendAsync(socket, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "action", ["params"] = new JsonObject { ["type"] = "chat/turnStarted" } });
                    break;
                default:
                    await SendAsync(socket, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = request["params"]!.DeepClone() });
                    break;
            }
        }
    }

    private static Task SendAsync(WebSocket socket, JsonObject message) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
}
