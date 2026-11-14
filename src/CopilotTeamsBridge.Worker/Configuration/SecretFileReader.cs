namespace CopilotTeamsBridge.Worker.Configuration;

/// <summary>Le valores sensiveis de arquivos privados do usuario do servico.</summary>
internal interface ISecretFileReader
{
    /// <summary>Le o conteudo textual do arquivo, sem espacos nas extremidades.</summary>
    /// <param name="path">Caminho absoluto do arquivo privado.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns>Conteudo do arquivo.</returns>
    /// <exception cref="InvalidOperationException">Arquivo inexistente, vazio ou acessivel por grupo/outros.</exception>
    Task<string> ReadTextAsync(string path, CancellationToken cancellationToken);

    /// <summary>Versao sincrona de <see cref="ReadTextAsync"/>, para composicao na inicializacao.</summary>
    /// <param name="path">Caminho absoluto do arquivo privado.</param>
    /// <returns>Conteudo do arquivo.</returns>
    /// <exception cref="InvalidOperationException">Arquivo inexistente, vazio ou acessivel por grupo/outros.</exception>
    string ReadText(string path);
}

internal sealed class SecretFileReader : ISecretFileReader
{
    private const UnixFileMode ForbiddenModes =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        EnsurePrivate(path);
        return EnsureNotEmpty(await File.ReadAllTextAsync(path, cancellationToken));
    }

    public string ReadText(string path)
    {
        EnsurePrivate(path);
        return EnsureNotEmpty(File.ReadAllText(path));
    }

    private static void EnsurePrivate(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidOperationException("Arquivo de secret ausente ou com caminho relativo.");

        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ForbiddenModes) != 0)
            throw new InvalidOperationException("Arquivo de secret deve ter permissao restrita ao dono (chmod 600).");
    }

    private static string EnsureNotEmpty(string raw)
    {
        var content = raw.Trim();
        if (content.Length == 0)
            throw new InvalidOperationException("Arquivo de secret vazio.");

        return content;
    }
}
