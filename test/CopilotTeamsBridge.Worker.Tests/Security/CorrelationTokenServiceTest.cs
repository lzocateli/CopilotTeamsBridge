using System.Text;
using CopilotTeamsBridge.Worker.Security;
using Shouldly;
using Xunit;

namespace CopilotTeamsBridge.Worker.Tests.Security;

[Trait("Category", "Unit")]
public sealed class CorrelationTokenServiceTest
{
    private static readonly byte[] s_key = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly CorrelationTokenService _service;

    public CorrelationTokenServiceTest() => _service = new CorrelationTokenService(s_key, _time);

    [Fact]
    public void IssuedTokenIsValidOnlyForSameRequestChatAndNonce()
    {
        var interaction = TestData.ToolConfirmation();
        var issued = _service.Issue(interaction, TestData.ChatChannel, TimeSpan.FromMinutes(15));

        _service.Validate(issued.Token, interaction, TestData.ChatChannel, issued.Nonce).ShouldBe(TokenValidationStatus.Valid);
        _service.Validate(issued.Token, TestData.ToolConfirmation("tool-2"), TestData.ChatChannel, issued.Nonce).ShouldBe(TokenValidationStatus.Mismatch);
        _service.Validate(issued.Token, interaction, "ahp-chat:/other", issued.Nonce).ShouldBe(TokenValidationStatus.Mismatch);
        _service.Validate(issued.Token, interaction, TestData.ChatChannel, "other-nonce").ShouldBe(TokenValidationStatus.Mismatch);
        issued.Token.ShouldNotContain(interaction.Id);
    }

    [Fact]
    public void ExpiredTokenIsReportedAsExpired()
    {
        var interaction = TestData.ToolConfirmation();
        var issued = _service.Issue(interaction, TestData.ChatChannel, TimeSpan.FromMinutes(1));

        _time.Advance(TimeSpan.FromMinutes(2));

        _service.Validate(issued.Token, interaction, TestData.ChatChannel, issued.Nonce).ShouldBe(TokenValidationStatus.Expired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a.b.c")]
    [InlineData("!!!.???")]
    public void MalformedTokensAreRejected(string? token)
    {
        _service.Validate(token, TestData.ToolConfirmation(), TestData.ChatChannel, "nonce").ShouldBe(TokenValidationStatus.Malformed);
    }

    [Fact]
    public void TamperedOrForeignKeyTokensAreRejected()
    {
        var interaction = TestData.ToolConfirmation();
        var issued = _service.Issue(interaction, TestData.ChatChannel, TimeSpan.FromMinutes(15));
        var foreign = new CorrelationTokenService(Encoding.UTF8.GetBytes("ffffffffffffffffffffffffffffffff"), _time)
            .Issue(interaction, TestData.ChatChannel, TimeSpan.FromMinutes(15));
        var parts = issued.Token.Split('.');
        var tampered = $"{parts[0]}A.{parts[1]}";

        _service.Validate(tampered, interaction, TestData.ChatChannel, issued.Nonce).ShouldBe(TokenValidationStatus.Malformed);
        _service.Validate(foreign.Token, interaction, TestData.ChatChannel, foreign.Nonce).ShouldBe(TokenValidationStatus.Malformed);
    }

    [Fact]
    public void NonceCanBeConsumedOnlyOnce()
    {
        _service.TryConsume("n1").ShouldBeTrue();
        _service.TryConsume("n1").ShouldBeFalse();
        _service.TryConsume("n2").ShouldBeTrue();
    }

    [Fact]
    public void KeyParserRequiresBase64WithMinimumLength()
    {
        CorrelationKey.Parse(Convert.ToBase64String(new byte[32])).Length.ShouldBe(32);
        _ = Should.Throw<InvalidOperationException>(() => CorrelationKey.Parse(Convert.ToBase64String(new byte[16])));
        _ = Should.Throw<InvalidOperationException>(() => CorrelationKey.Parse("nao-e-base64"));
    }
}
