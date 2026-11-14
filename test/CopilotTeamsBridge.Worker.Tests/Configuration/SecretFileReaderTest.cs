using CopilotTeamsBridge.Worker.Configuration;
using Shouldly;
using Xunit;

namespace CopilotTeamsBridge.Worker.Tests.Configuration;

[Trait("Category", "Unit")]
public sealed class SecretFileReaderTest : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("teams-bridge-test-").FullName;
    private readonly SecretFileReader _reader = new();

    [Fact]
    public async Task ReadsTrimmedContentFromOwnerOnlyFile()
    {
        var path = Write("  valor  \n", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        (await _reader.ReadTextAsync(path, CancellationToken.None)).ShouldBe("valor");
        _reader.ReadText(path).ShouldBe("valor");
    }

    [Fact]
    public void RejectsFileReadableByGroup()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = Write("valor", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        Should.Throw<InvalidOperationException>(() => _reader.ReadText(path)).Message.ShouldNotContain("valor");
    }

    [Fact]
    public void RejectsRelativeMissingOrEmptyFiles()
    {
        _ = Should.Throw<InvalidOperationException>(() => _reader.ReadText("relative.token"));
        _ = Should.Throw<InvalidOperationException>(() => _reader.ReadText(Path.Combine(_directory, "missing")));
        _ = Should.Throw<InvalidOperationException>(() => _reader.ReadText(Write("  ", UnixFileMode.UserRead | UnixFileMode.UserWrite)));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string content, UnixFileMode mode)
    {
        var path = Path.Combine(_directory, Path.GetRandomFileName());
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);

        return path;
    }
}
