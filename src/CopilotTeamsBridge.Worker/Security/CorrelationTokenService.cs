using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotTeamsBridge.Worker.Interactions;

namespace CopilotTeamsBridge.Worker.Security;

internal enum TokenValidationStatus
{
    Valid = 1,
    Malformed = 2,
    Expired = 3,
    Mismatch = 4,
}

internal sealed record IssuedCorrelation(string Token, string Nonce, DateTimeOffset ExpiresAt);

/// <summary>
/// Emite e valida a referencia opaca do cartao (HMAC-SHA256 sobre chat, pedido, nonce e prazo)
/// e garante uso unico do nonce. A referencia nao substitui a identidade do respondente.
/// </summary>
internal interface ICorrelationTokenService
{
    IssuedCorrelation Issue(PendingInteraction interaction, string chatChannel, TimeSpan timeToLive);

    TokenValidationStatus Validate(string? token, PendingInteraction interaction, string chatChannel, string expectedNonce);

    /// <summary>Consome o nonce uma unica vez.</summary>
    /// <param name="nonce">Nonce emitido em <see cref="Issue"/>.</param>
    /// <returns><see langword="false"/> quando o nonce ja foi consumido (replay/duplicidade).</returns>
    bool TryConsume(string nonce);
}

internal sealed class CorrelationTokenService(byte[] key, TimeProvider timeProvider) : ICorrelationTokenService
{
    public const int MinimumKeyBytes = 32;

    private const string Version = "v1";
    private const char Separator = '|';
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new(StringComparer.Ordinal);

    public IssuedCorrelation Issue(PendingInteraction interaction, string chatChannel, TimeSpan timeToLive)
    {
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        var expiresAt = timeProvider.GetUtcNow().Add(timeToLive);
        var payload = BuildPayload(interaction, chatChannel, nonce, expiresAt.ToUnixTimeSeconds());
        var token = $"{Base64Url(Encoding.UTF8.GetBytes(payload))}.{Base64Url(Sign(payload))}";
        return new IssuedCorrelation(token, nonce, expiresAt);
    }

    public TokenValidationStatus Validate(string? token, PendingInteraction interaction, string chatChannel, string expectedNonce)
    {
        if (!TryReadPayload(token, out var payload))
            return TokenValidationStatus.Malformed;

        var fields = payload.Split(Separator);
        if (fields.Length != 7 || !long.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresUnix))
            return TokenValidationStatus.Malformed;

        var expected = BuildPayload(interaction, chatChannel, expectedNonce, expiresUnix);
        if (!string.Equals(expected, payload, StringComparison.Ordinal))
            return TokenValidationStatus.Mismatch;

        return timeProvider.GetUtcNow() > DateTimeOffset.FromUnixTimeSeconds(expiresUnix)
            ? TokenValidationStatus.Expired
            : TokenValidationStatus.Valid;
    }

    public bool TryConsume(string nonce)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var stale in _consumed.Where(item => now - item.Value > TimeSpan.FromHours(2)).Select(item => item.Key).ToList())
            _ = _consumed.TryRemove(stale, out _);

        return _consumed.TryAdd(nonce, now);
    }

    private bool TryReadPayload(string? token, out string payload)
    {
        payload = string.Empty;
        var parts = token?.Split('.');
        if (parts is not { Length: 2 } || !TryDecode(parts[0], out var payloadBytes) || !TryDecode(parts[1], out var signature))
            return false;

        payload = Encoding.UTF8.GetString(payloadBytes);
        return CryptographicOperations.FixedTimeEquals(signature, Sign(payload));
    }

    private static string BuildPayload(PendingInteraction interaction, string chatChannel, string nonce, long expiresUnix) =>
        string.Join(
            Separator,
            Version,
            chatChannel,
            interaction.Kind.ToString(),
            interaction.Id,
            interaction.TurnId ?? string.Empty,
            nonce,
            expiresUnix.ToString(CultureInfo.InvariantCulture));

    private byte[] Sign(string payload) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string value, out byte[] bytes)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        var buffer = new byte[padded.Length];
        if (!Convert.TryFromBase64String(padded, buffer, out var written) || written == 0)
        {
            bytes = [];
            return false;
        }

        bytes = buffer[..written];
        return true;
    }
}

internal static class CorrelationKey
{
    /// <summary>Decodifica a chave HMAC em base64 lida do arquivo privado.</summary>
    /// <param name="base64">Conteudo do arquivo.</param>
    /// <returns>Bytes da chave.</returns>
    /// <exception cref="InvalidOperationException">Conteudo invalido ou chave menor que 32 bytes.</exception>
    public static byte[] Parse(string base64)
    {
        var buffer = new byte[base64.Length];
        if (!Convert.TryFromBase64String(base64.Trim(), buffer, out var written) || written < CorrelationTokenService.MinimumKeyBytes)
            throw new InvalidOperationException("Chave de correlacao deve ser base64 com pelo menos 32 bytes.");

        return buffer[..written];
    }
}
