using System.Buffers.Binary;
using System.Collections.Concurrent;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Gm.FirstLogin;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.AntiCheat;

/// <summary>
/// AntiCheat:SpellHoneypot through the real CMSG_CAST_SPELL handler: flag off keeps the retail NotKnown signal (weight 10) only; flag on
/// refuses a trap id before the spell system and records one Spell/60 finding instead; GM mode and staff are never flagged.
/// </summary>
public sealed class SpellHoneypotWorldTests
{
    private const uint NeverCastSpell = 482; // DeliberatelyInertSpells: NeverCast
    private const uint GmToolSpell = 27204; // GmFirstLoginToolCatalog: QADebug Instant Cast
    private const uint WarlockDetectInvisibility = 2970; // GmFirstLoginToolCatalog Moderator entry, and the trainable Warlock rank
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    private static AntiCheatFeature FeatureOf(WorldTestHost host) => host.WorldServices.GetRequiredService<AntiCheatFeature>();

    private static ConcurrentQueue<(string Player, AntiCheatFinding Finding)> Watch(WorldTestHost host)
    {
        var seen = new ConcurrentQueue<(string, AntiCheatFinding)>();
        FeatureOf(host).FindingRecorded += (player, finding, _, _) => seen.Enqueue((player.Name, finding));
        return seen;
    }

    private static Task ApplyAsync(WorldTestHost host, bool honeypot, AccountSecurity exemptSecurity = AccountSecurity.Moderator)
    {
        var options = new AntiCheatOptions { ExemptSecurity = exemptSecurity };
        options.SpellHoneypot.Enabled = honeypot;
        return host.OnWorldAsync(() => FeatureOf(host).ApplyOptions(options));
    }

    /// <summary>A world host whose spell store holds the three ids used here (none in the character's spellbook), so an unlearned cast reaches the spell system's NotKnown refusal.</summary>
    private static WorldTestHost StartHost() => WorldTestHost.Start(configureServices: services =>
        services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(new SpellContent(
            new[] { NeverCastSpell, GmToolSpell, WarlockDetectInvisibility }.Select(id => new SpellTemplateRow { Id = id, SpellName = "Honeypot test " + id, RangeIndex = 1 }).ToList(),
            [], [], [new SpellRangeRow { Id = 1 }], [], [], []))));

    private static byte[] CastPayload(uint spellId)
    {
        byte[] payload = new byte[6]; // u32 spell, u16 target flags (self)
        BinaryPrimitives.WriteUInt32LittleEndian(payload, spellId);
        return payload;
    }

    [Fact]
    public void TheCatalog_ContainsTheToolIdUsedHere() => Assert.Contains(GmToolSpell, GmFirstLoginToolCatalog.All.Select(s => s.Id));

    [Fact]
    public async Task FlagOff_ATrapCast_KeepsTheRetailNotKnownSignalOnly()
    {
        await using WorldTestHost host = StartHost();
        await ApplyAsync(host, honeypot: false);
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = Watch(host);
        await using WorldTestClient client = await host.EnterWorldAsync("TRAPOFF", "Trapoff");

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(NeverCastSpell));
        await host.WaitForWorldAsync(() => !seen.IsEmpty, "the NotKnown signal");
        await Task.Delay(Settle);

        (string Player, AntiCheatFinding Finding) only = Assert.Single(seen);
        Assert.Equal(AntiCheatViolation.Spell, only.Finding.Type);
        Assert.Equal(10f, only.Finding.Weight);
        Assert.NotEqual(CastSpellHoneypot.Detail, only.Finding.Detail);
    }

    [Theory]
    [InlineData(NeverCastSpell)]
    [InlineData(GmToolSpell)]
    public async Task FlagOn_ANonGmTrapCast_IsRefusedAndRecordedOnceAsSpell60(uint spellId)
    {
        await using WorldTestHost host = StartHost();
        await ApplyAsync(host, honeypot: true);
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = Watch(host);
        await using WorldTestClient client = await host.EnterWorldAsync("TRAPON", "Trapon");
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(spellId));
        await host.WaitForWorldAsync(() => !seen.IsEmpty, "the honeypot signal");
        List<(WorldOpcode Opcode, byte[] Payload)> reply = await client.CollectAsync(Settle);

        // One finding (the honeypot's, not the weight-10 NotKnown one on top), and nothing from the spell system.
        (string Player, AntiCheatFinding Finding) only = Assert.Single(seen);
        Assert.Equal("Trapon", only.Player);
        Assert.Equal(AntiCheatViolation.Spell, only.Finding.Type);
        Assert.Equal(60f, only.Finding.Weight);
        Assert.Equal(CastSpellHoneypot.Detail, only.Finding.Detail);
        Assert.DoesNotContain(reply, p => p.Opcode is WorldOpcode.SmsgCastResult or WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo);
    }

    [Fact]
    public async Task FlagOn_ANonGmCastOfTheTrainableWarlockRank_IsNotATrap()
    {
        await using WorldTestHost host = StartHost();
        await ApplyAsync(host, honeypot: true);
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = Watch(host);
        await using WorldTestClient client = await host.EnterWorldAsync("TRAPWL", "Trapwl");

        // Spell 2970 is in the GM tool catalog but also a normal class spell: only the ordinary NotKnown signal (weight 10) may come, never the honeypot's.
        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(WarlockDetectInvisibility));
        await host.WaitForWorldAsync(() => !seen.IsEmpty, "the NotKnown signal");
        await Task.Delay(Settle);

        (string Player, AntiCheatFinding Finding) only = Assert.Single(seen);
        Assert.Equal(10f, only.Finding.Weight);
        Assert.NotEqual(CastSpellHoneypot.Detail, only.Finding.Detail);
    }

    [Fact]
    public async Task FlagOn_StaffAccount_IsNotFlagged()
    {
        await using WorldTestHost host = StartHost();
        await ApplyAsync(host, honeypot: true);
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = Watch(host);
        await using WorldTestClient staff = await host.EnterWorldAsync("TRAPSTAFF", "Trapstaff", AccountSecurity.GameMaster);

        await staff.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(NeverCastSpell));
        await staff.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(GmToolSpell));
        await Task.Delay(Settle);

        Assert.Empty(seen);
    }

    [Fact]
    public async Task FlagOn_AGameMasterInGmMode_IsNotFlagged_AndIsFlaggedWithGmModeOff()
    {
        await using WorldTestHost host = StartHost();
        await ApplyAsync(host, honeypot: true, exemptSecurity: AccountSecurity.Administrator);
        ConcurrentQueue<(string Player, AntiCheatFinding Finding)> seen = Watch(host);
        await using WorldTestClient gm = await host.EnterWorldAsync("TRAPGM", "Trapgm", AccountSecurity.GameMaster);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gm on");
        await gm.ReadChatAsync();
        await gm.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(NeverCastSpell));
        await Task.Delay(Settle);
        Assert.Empty(seen);

        // Control: the same account with GM mode off is below the exempt level and is flagged, so the silence above is the exemption.
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gm off");
        await gm.ReadChatAsync();
        await gm.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(NeverCastSpell));
        await host.WaitForWorldAsync(() => !seen.IsEmpty, "the honeypot signal with GM mode off");
        Assert.Contains(seen, s => s.Finding.Weight == 60f && s.Finding.Detail == CastSpellHoneypot.Detail);
    }
}
