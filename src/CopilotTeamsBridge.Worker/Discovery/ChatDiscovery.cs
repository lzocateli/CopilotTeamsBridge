using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotTeamsBridge.Worker.AgentHost;
using CopilotTeamsBridge.Worker.Configuration;

namespace CopilotTeamsBridge.Worker.Discovery;

/// <summary>Chat encontrado no catalogo de uma sessao do Agent Host.</summary>
/// <param name="SessionResource">URI da sessao (<c>ahp-session:/...</c>).</param>
/// <param name="SessionTitle">Titulo da sessao exibido pelo host.</param>
/// <param name="ChatResource">URI do chat (<c>ahp-chat:/...</c>), valor de <c>AgentHost:ChatChannel</c>.</param>
/// <param name="ChatTitle">Titulo do chat, quando informado pelo host.</param>
/// <param name="IsDefault">Indica se e o <c>defaultChat</c> da sessao.</param>
internal sealed record DiscoveredChat(string SessionResource, string SessionTitle, string ChatResource, string? ChatTitle, bool IsDefault);

/// <summary>Lista as sessoes e os chats do Agent Host para descobrir o valor de <c>AgentHost:ChatChannel</c>.</summary>
internal interface IChatDiscovery
{
    /// <summary>Conecta ao Agent Host, lista as sessoes mais recentes e le o catalogo de chats de cada uma.</summary>
    /// <param name="options">Endpoint loopback e arquivo do token de conexao.</param>
    /// <param name="sessionLimit">Quantidade maxima de sessoes consultadas.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns>Chats encontrados, na ordem de sessoes devolvida pelo host (mais recentes primeiro).</returns>
    /// <exception cref="InvalidOperationException">Endpoint fora do loopback ou arquivo de token ausente.</exception>
    Task<IReadOnlyList<DiscoveredChat>> ListAsync(AgentHostOptions options, int sessionLimit, CancellationToken cancellationToken);
}

internal sealed class ChatDiscovery(IAhpConnectionFactory connectionFactory, ISecretFileReader secretReader) : IChatDiscovery
{
    public async Task<IReadOnlyList<DiscoveredChat>> ListAsync(AgentHostOptions options, int sessionLimit, CancellationToken cancellationToken)
    {
        if (!TeamsBridgeOptionsValidator.IsLoopbackWebSocket(options.Endpoint))
            throw new InvalidOperationException("AgentHost:Endpoint deve ser ws/wss em loopback, sem userinfo ou query.");

        if (string.IsNullOrWhiteSpace(options.TokenFile))
            throw new InvalidOperationException("AgentHost:TokenFile e obrigatorio.");

        var token = await secretReader.ReadTextAsync(options.TokenFile, cancellationToken);
        await using var connection = connectionFactory.Create();
        await connection.ConnectAsync(new Uri(options.Endpoint), token, cancellationToken);
        _ = await connection.RequestAsync(
            AhpProtocol.InitializeMethod,
            AhpActionFactory.Initialize(Guid.NewGuid().ToString("N"), typeof(ChatDiscovery).Assembly.GetName().Version?.ToString() ?? "0.0.0"),
            cancellationToken);

        var sessions = await connection.RequestAsync(
            AhpProtocol.ListSessionsMethod,
            new JsonObject { ["channel"] = AhpProtocol.RootChannel, ["limit"] = sessionLimit },
            cancellationToken);

        var chats = new List<DiscoveredChat>();
        foreach (var (resource, title) in ReadSessions(sessions).Take(sessionLimit))
            chats.AddRange(await ReadChatsAsync(connection, resource, title, cancellationToken));

        return chats;
    }

    private static async Task<List<DiscoveredChat>> ReadChatsAsync(
        IAhpConnection connection,
        string sessionResource,
        string sessionTitle,
        CancellationToken cancellationToken)
    {
        var subscription = await connection.RequestAsync(AhpProtocol.SubscribeMethod, AhpActionFactory.Subscribe(sessionResource), cancellationToken);
        await connection.NotifyAsync(AhpProtocol.UnsubscribeMethod, new JsonObject { ["channel"] = sessionResource }, cancellationToken);

        if (!TryGetState(subscription, out var state)
            || !state.TryGetProperty("chats", out var catalog)
            || catalog.ValueKind != JsonValueKind.Array)
            return [];

        var defaultChat = AhpStateReader.GetString(state, "defaultChat");
        return catalog.EnumerateArray()
            .Where(chat => chat.ValueKind == JsonValueKind.Object)
            .Select(chat => (Resource: AhpStateReader.GetString(chat, "resource"), Title: AhpStateReader.ReadText(chat, "title")))
            .Where(chat => chat.Resource?.StartsWith(AhpProtocol.ChatScheme, StringComparison.Ordinal) == true)
            .Select(chat => new DiscoveredChat(
                sessionResource,
                sessionTitle,
                chat.Resource!,
                string.IsNullOrEmpty(chat.Title) ? null : chat.Title,
                string.Equals(chat.Resource, defaultChat, StringComparison.Ordinal)))
            .ToList();
    }

    private static List<(string Resource, string Title)> ReadSessions(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return [];

        return [.. items.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => (Resource: AhpStateReader.GetString(item, "resource"), Title: AhpStateReader.ReadText(item, "title")))
            .Where(item => item.Resource?.StartsWith(AhpProtocol.SessionScheme, StringComparison.Ordinal) == true)
            .Select(item => (item.Resource!, item.Title))];
    }

    private static bool TryGetState(JsonElement subscription, out JsonElement state)
    {
        state = default;
        return subscription.ValueKind == JsonValueKind.Object
            && subscription.TryGetProperty("snapshot", out var snapshot)
            && snapshot.ValueKind == JsonValueKind.Object
            && snapshot.TryGetProperty("state", out state)
            && state.ValueKind == JsonValueKind.Object;
    }
}
