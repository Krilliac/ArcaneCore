using System.Security.Cryptography;
using System.Text;
using ArcaneCore.World.HotCode.Modules;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>
/// The module hash allowlist: an empty setting adds no restriction, a configured one is fail-closed
/// (missing, unreadable or malformed file and an unlisted hash all refuse) and is read on every load.
/// </summary>
public sealed class ModuleAllowlistTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-allowlist-" + Guid.NewGuid().ToString("N"));

    public ModuleAllowlistTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private string WriteList(params string[] lines)
    {
        string path = Path.Combine(_dir, "allow.txt");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoAllowlistConfigured_AddsNoRestriction(string? path)
    {
        Assert.True(ModuleAllowlist.Check(path, Hash("any")).Allowed);
    }

    [Fact]
    public void AListedHash_IsAllowed_CaseInsensitively()
    {
        string sha = Hash("module-a");
        string path = WriteList("# approved modules", string.Empty, sha.ToLowerInvariant());

        Assert.True(ModuleAllowlist.Check(path, sha).Allowed);
        Assert.True(ModuleAllowlist.Check(path, sha.ToLowerInvariant()).Allowed);
    }

    [Fact]
    public void AnUnlistedHash_IsRefused_AndTheVerdictNamesIt()
    {
        string path = WriteList(Hash("listed"));
        string other = Hash("not listed");

        AllowlistVerdict verdict = ModuleAllowlist.Check(path, other);

        Assert.False(verdict.Allowed);
        Assert.Contains(other, verdict.Detail);
        Assert.Contains("allowlist", verdict.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ATrailingLabelAndAComment_AreIgnored()
    {
        string sha = Hash("labelled");
        string path = WriteList($"{sha}  GmCommands v3   # reviewed 2026-10-03", $"  # {Hash("commented")}");

        Assert.True(ModuleAllowlist.Check(path, sha).Allowed);
        Assert.False(ModuleAllowlist.Check(path, Hash("commented")).Allowed);
    }

    [Fact]
    public void AMissingFile_FailsClosed()
    {
        AllowlistVerdict verdict = ModuleAllowlist.Check(Path.Combine(_dir, "nope.txt"), Hash("x"));

        Assert.False(verdict.Allowed);
        Assert.Contains("could not be read", verdict.Detail);
    }

    [Fact]
    public void AnEmptyFile_AllowsNothing()
    {
        string path = WriteList("# nothing approved yet");

        Assert.False(ModuleAllowlist.Check(path, Hash("x")).Allowed);
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("ABCDEF")]
    [InlineData("ZZ00000000000000000000000000000000000000000000000000000000000000")]
    public void AMalformedLine_RefusesEveryModule_EvenOneListedOnAnotherLine(string bad)
    {
        string sha = Hash("good");
        string path = WriteList(sha, bad);

        AllowlistVerdict verdict = ModuleAllowlist.Check(path, sha);

        Assert.False(verdict.Allowed);
        Assert.Contains("line 2", verdict.Detail);
    }

    [Fact]
    public void TheFile_IsReadOnEveryCheck_SoAnEditTakesEffectWithoutARestart()
    {
        string sha = Hash("late");
        string path = WriteList(Hash("other"));
        Assert.False(ModuleAllowlist.Check(path, sha).Allowed);

        WriteList(Hash("other"), sha);

        Assert.True(ModuleAllowlist.Check(path, sha).Allowed);
    }

    [Fact]
    public void ADirectoryOrOversizeFile_FailsClosed()
    {
        Assert.False(ModuleAllowlist.Check(_dir, Hash("x")).Allowed);

        string big = Path.Combine(_dir, "big.txt");
        File.WriteAllText(big, new string('#', 2 * 1024 * 1024));
        Assert.False(ModuleAllowlist.Check(big, Hash("x")).Allowed);
    }
}
