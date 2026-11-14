using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotTeamsBridge.Worker.AgentHost;
using CopilotTeamsBridge.Worker.Configuration;
using CopilotTeamsBridge.Worker.Discovery;
using Moq;
using Shouldly;
using Xunit;

namespace CopilotTeamsBridge.Worker.Tests.Discovery;

[Trait("Category", "Unit")]
public sealed class ChatDiscoveryTest
{
    private const string Sessions = """
        {"items":[
          {"resource":"ahp-session:/s1","title":"Sessao um"},
          {"resource":"ahp-terminal:/t1","title":"ignorada"},
          {"resource":"ahp-session:/s2","title":{"markdown":"Sessao dois"}}
        ]}
        """;

    private const string SessionOne = """
        {"snapshot":{"resource":"ahp-session:/s1","state":{"defaultChat":"ahp-chat:/c2","chats":[
          {"resource":"ahp-chat:/c1","title":"Primeiro"},
          {"resource":"ahp-chat:/c2","title":"Principal"},
          {"resource":"ahp-session:/invalido"}
        ]}}}
        """;

    private readonly Mock<IAhpConnection> _connection = new();
    private readonly Mock<IAhpConnectionFactory> _factory = new();
    private readonly Mock<ISecretFileReader> _secrets = new();
    private readonly List<(string Method, string? Channel)> _requests = [];

    public ChatDiscoveryTest()
    {
        _ = _factory.Setup(factory => factory.Create()).Returns(_connection.Object);
        _ = _secrets.Setup(reader => reader.ReadTextAsync("/secrets/agent-host.token", It.IsAny<CancellationToken>())).ReturnsAsync("token");
        _ = _connection.Setup(connection => connection.RequestAsync(It.IsAny<string>(), It.IsAny<JsonObject>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string method, JsonObject parameters, CancellationToken _) =>
            {
                var channel = parameters["channel"]?.GetValue<string>();
                _requests.Add((method, channel));
                return (method, channel) switch
                {
                    ("listSessions", _) => Json(Sessions),
                    ("subscribe", "ahp-session:/s1") => Json(SessionOne),
                    ("subscribe", _) => Json("""{"snapshot":{"state":{}}}"""),
                    _ => Json("{}"),
                };
            });
    }

    [Fact]
    public async Task ListsChatsOfEachSessionAndMarksDefault()
    {
        var chats = await Discovery().ListAsync(TestData.Options().AgentHost, 20, CancellationToken.None);

        chats.Select(chat => (chat.ChatResource, chat.ChatTitle, chat.IsDefault)).ShouldBe(
        [
            ("ahp-chat:/c1", "Primeiro", false),
            ("ahp-chat:/c2", "Principal", true),
        ]);
        chats.ShouldAllBe(chat => chat.SessionTitle == "Sessao um");
        _requests.ShouldBe(
        [
            ("initialize", "ahp-root://"),
            ("listSessions", "ahp-root://"),
            ("subscribe", "ahp-session:/s1"),
            ("subscribe", "ahp-session:/s2"),
        ]);
        _connection.Verify(connection => connection.NotifyAsync("unsubscribe", It.IsAny<JsonObject>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _connection.Verify(connection => connection.ConnectAsync(new Uri(TestData.Options().AgentHost.Endpoint), "token", It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task HonorsSessionLimit()
    {
        _ = await Discovery().ListAsync(TestData.Options().AgentHost, 1, CancellationToken.None);

        _requests.Count(request => request.Method == "subscribe").ShouldBe(1);
    }

    [Fact]
    public async Task RefusesNonLoopbackEndpointWithoutConnecting()
    {
        var options = TestData.Options(options => options.AgentHost.Endpoint = "ws://10.0.0.5:40179/").AgentHost;

        _ = await Should.ThrowAsync<InvalidOperationException>(() => Discovery().ListAsync(options, 20, CancellationToken.None));
        _factory.Verify(factory => factory.Create(), Times.Never);
    }

    [Fact]
    public void FormatGroupsBySessionAndSanitizesTitles()
    {
        var text = ChatDiscoveryCommand.Format(
        [
            new DiscoveredChat("ahp-session:/s1", "Linha\nquebrada", "ahp-chat:/c1", null, false),
            new DiscoveredChat("ahp-session:/s1", "Linha\nquebrada", "ahp-chat:/c2", "Principal", true),
        ]);

        text.ShouldContain("Sessao: Linha quebrada");
        text.ShouldContain("  * ahp-chat:/c2  Principal");
        text.ShouldContain("    ahp-chat:/c1");
        ChatDiscoveryCommand.Format([]).ShouldStartWith("Nenhum chat encontrado");
    }

    private ChatDiscovery Discovery() => new(_factory.Object, _secrets.Object);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
