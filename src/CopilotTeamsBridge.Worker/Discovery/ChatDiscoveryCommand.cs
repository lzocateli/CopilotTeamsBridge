using System.Net.WebSockets;
using System.Text;
using CopilotTeamsBridge.Worker.AgentHost;
using CopilotTeamsBridge.Worker.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotTeamsBridge.Worker.Discovery;

/// <summary>Modo <c>--list-chats</c>: imprime os chats do Agent Host e encerra, sem iniciar o worker.</summary>
internal static class ChatDiscoveryCommand
{
    public const string Switch = "--list-chats";
    internal const int SessionLimit = 20;
    private const int MaxTitleLength = 80;

    public static bool IsRequested(string[] args) => args.Contains(Switch, StringComparer.Ordinal);

    /// <summary>Executa a descoberta com a mesma configuracao do worker (appsettings, Local, env e argumentos).</summary>
    /// <param name="args">Argumentos do processo, incluindo <see cref="Switch"/>.</param>
    /// <param name="output">Saida da listagem.</param>
    /// <param name="error">Saida de erros, sem token, porta ou excecoes internas do transporte.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns><c>0</c> com chats listados, <c>1</c> em falha e <c>2</c> sem chats encontrados.</returns>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var builder = TeamsBridgeServiceRegistration.CreateBuilder([.. args.Where(arg => !string.Equals(arg, Switch, StringComparison.Ordinal))]);
        var options = builder.Configuration.GetSection($"{TeamsBridgeOptions.SectionName}:AgentHost").Get<AgentHostOptions>() ?? new AgentHostOptions();
        using var host = builder.Build();
        var discovery = host.Services.GetRequiredService<IChatDiscovery>();

        try
        {
            var chats = await discovery.ListAsync(options, SessionLimit, cancellationToken);
            await output.WriteAsync(Format(chats));
            return chats.Count == 0 ? 2 : 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or AhpProtocolException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Falha ao listar chats: {exception.Message}");
            return 1;
        }
        catch (Exception exception) when (exception is WebSocketException or AhpConnectionClosedException)
        {
            await error.WriteLineAsync($"Falha ao conectar ao Agent Host ({exception.GetType().Name}). Confira AgentHost:Endpoint e AgentHost:TokenFile.");
            return 1;
        }
    }

    internal static string Format(IReadOnlyList<DiscoveredChat> chats)
    {
        if (chats.Count == 0)
            return "Nenhum chat encontrado nas sessoes do Agent Host." + Environment.NewLine;

        var text = new StringBuilder();
        _ = text.AppendLine("Chats do Agent Host (sessoes mais recentes primeiro; * = chat padrao da sessao):");
        foreach (var session in chats.GroupBy(chat => (chat.SessionResource, chat.SessionTitle)))
        {
            _ = text.AppendLine();
            _ = text.Append("Sessao: ").AppendLine(Sanitize(session.Key.SessionTitle, "(sem titulo)"));
            foreach (var chat in session)
            {
                _ = text.Append(chat.IsDefault ? "  * " : "    ").Append(chat.ChatResource);
                _ = text.Append("  ").AppendLine(Sanitize(chat.ChatTitle, string.Empty));
            }
        }

        return text.ToString();
    }

    private static string Sanitize(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var clean = new string([.. value.Select(character => char.IsControl(character) ? ' ' : character)]).Trim();
        return clean.Length <= MaxTitleLength ? clean : string.Concat(clean.AsSpan(0, MaxTitleLength - 3), "...");
    }
}
