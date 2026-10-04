using System.Text.Json;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// Hand-written docs that state a default, a value or a delivered/not-delivered fact are tied to the code, so the statement
/// cannot silently become false (each of these was wrong at the base of the docs lane).
/// </summary>
public sealed class DocDriftTests
{
    private static string Read(string path) => RepoRoot.ReadText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void AutocreateAccounts_IsOff_InCodeAndShippedConfig_AndTheDocsSayItIsNotRetail()
    {
        Assert.False(new AuthOptions().AutocreateAccounts);
        using JsonDocument realm = JsonDocument.Parse(Read("src/ArcaneCore.Realm/appsettings.json"));
        Assert.False(realm.RootElement.GetProperty("Auth").GetProperty("AutocreateAccounts").GetBoolean());

        foreach (string page in new[] { "README.md", "docs/M1_ACCEPTANCE.md" })
        {
            string text = Read(page);
            Assert.Contains("AutocreateAccounts", text, StringComparison.Ordinal);
            Assert.True(text.Contains("not retail", StringComparison.Ordinal) && (text.Contains("**off**", StringComparison.Ordinal) || text.Contains("`false`", StringComparison.Ordinal)),
                page + " must say Auth:AutocreateAccounts is off in the shipped config and not retail behaviour");
        }
    }

    [Fact]
    public void Hardening_DefaultsInTheTable_EqualTheOptionsDefaults()
    {
        var options = new WorldSessionOptions();
        string text = Read("docs/security/hardening.md");
        string Row(string key) => text.Split('\n').Single(l => l.StartsWith("| `" + key + "`", StringComparison.Ordinal));

        Assert.Equal(TimeSpan.Zero, options.WriterDrainGrace);
        Assert.Contains("00:00:00", Row("World:WriterDrainGrace"), StringComparison.Ordinal);
        Assert.DoesNotContain("WriterDrainGrace` (5 s)", text, StringComparison.Ordinal);

        Assert.Equal(TimeSpan.FromSeconds(10), options.PreAuthTimeout);
        Assert.Contains("00:00:10", Row("World:PreAuthTimeout"), StringComparison.Ordinal);

        Assert.Equal(8192, options.MaxQueuedWorldPackets);
        Assert.Equal(8L * 1024 * 1024, options.MaxQueuedWorldBytes);
        string queue = Row("World:MaxQueuedWorldPackets` / `MaxQueuedWorldBytes");
        Assert.Contains("8192 / 8388608", queue, StringComparison.Ordinal);
    }

    [Fact]
    public void Hardening_NotDeliveredList_NoLongerListsWhatIsDelivered()
    {
        string text = Read("docs/security/hardening.md");
        string limits = text[text.IndexOf("## Not delivered", StringComparison.Ordinal)..];
        limits = limits[..limits.IndexOf("## Open questions", StringComparison.Ordinal)];
        Assert.DoesNotContain("TimeoutSecsIfNoAuth", limits, StringComparison.Ordinal);
        Assert.DoesNotContain("inbound world-queue cap", limits, StringComparison.Ordinal);
        Assert.Contains("wrong-password throttle", limits, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("### Pre-auth deadline and inbound queue bounds", text, StringComparison.Ordinal);
    }

    [Fact]
    public void QuestsNpc_PointsToTheNpcServicesNotes_AndDoesNotClaimTheServicesAreAbsent()
    {
        string text = Read("docs/areas/quests-npc.md");
        Assert.Contains("(../integration/npc-services.md)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("these handlers never consume those\nfields or register gossip", text, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(RepoRoot.Find(), "docs", "integration", "npc-services.md")));
    }
}
