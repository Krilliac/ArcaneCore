using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Items;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>The spell system end to end: real sessions over loopback, in-memory spell tables.</summary>
public sealed class SpellWorldTests
{
    [Fact]
    public async Task Login_SendsTheStartingSpells_InSmsgInitialSpells()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("NOVICE", "Novice");

        Assert.Equal([Heal, Bolt], ParseInitialSpells(client.LoginPacket(WorldOpcode.SmsgInitialSpells)));
    }

    [Fact]
    public async Task CastSpell_Instant_StartsCastsAndHeals()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("HEALER", "Healer");
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(Heal));

        Assert.NotEmpty(await client.ReadUntilAsync(WorldOpcode.SmsgSpellStart));
        Assert.Equal(new byte[] { 0x29, 0x23, 0, 0, 0 }, await client.ReadUntilAsync(WorldOpcode.SmsgCastResult));
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellheallog);
    }

    [Fact]
    public async Task CastSpell_WithACastTime_LandsOnTheWorldTick_AndDamagesTheTarget()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient caster = await host.EnterWorldAsync("CASTER", "Caster");
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Target", race: 2);
        await FlagForPvpAsync(host, "Target");
        await caster.CollectAsync();
        await target.CollectAsync();
        ulong targetGuid = await host.PlayerStateAsync("Target", p => p.Guid.Value);
        uint health = await host.PlayerStateAsync("Target", p => p.Health);

        await caster.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(Bolt, targetGuid));

        await target.ReadUntilAsync(WorldOpcode.SmsgSpellStart);
        await caster.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        await target.ReadUntilAsync(WorldOpcode.SmsgSpellnonmeleedamagelog);
        Assert.Equal(health - 7, await host.PlayerStateAsync("Target", p => p.Health));
    }

    [Trait("Category", "Timing")]
    [Fact]
    public async Task CastSpell_WithACastTime_AdvancesOnWorldUpdated_WhenCommandQueueIsSaturated()
    {
        await using var host = WorldTestHost.Start(configure: o => { o.TickIntervalMs = 20; o.MaxCommandsPerTick = 1; });
        await using WorldTestClient caster = await host.EnterWorldAsync("SATCASTER", "Satcaster");
        await using WorldTestClient target = await host.EnterWorldAsync("SATTARGET", "Sattarget", race: 2);
        await FlagForPvpAsync(host, "Sattarget");
        await caster.CollectAsync();
        await target.CollectAsync();
        ulong targetGuid = await host.PlayerStateAsync("Sattarget", p => p.Guid.Value);
        Player casterPlayer = await host.PlayerAsync("Satcaster");
        Player targetPlayer = await host.PlayerAsync("Sattarget");
        SpellFeature spells = ((ArcaneCore.World.Net.WorldSession)casterPlayer.Session).Services.GetRequiredService<SpellFeature>();
        uint health = await host.OnWorldAsync(() =>
        {
            uint hp = targetPlayer.Health;
            Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(casterPlayer, Bolt, SpellCastTargets.ForUnit(new ObjectGuid(targetGuid)), triggered: false));
            for (int i = 0; i < 200; i++)
            {
                host.World.Post(static () => { });
            }

            return hp;
        });

        await caster.ReadUntilAsync(WorldOpcode.SmsgSpellGo, TimeSpan.FromMilliseconds(1500));
        await target.ReadUntilAsync(WorldOpcode.SmsgSpellnonmeleedamagelog, TimeSpan.FromMilliseconds(1500));
        Assert.Equal(health - 7, targetPlayer.Health);
    }

    [Fact]
    public async Task CancelCast_InterruptsTheCastInProgress()
    {
        await using var host = WorldTestHost.Start(configure: o => o.TickIntervalMs = 50);
        await using WorldTestClient caster = await host.EnterWorldAsync("QUITTER", "Quitter");
        await using WorldTestClient target = await host.EnterWorldAsync("SPARED", "Spared", race: 2);
        await FlagForPvpAsync(host, "Spared");
        await caster.CollectAsync();
        await target.CollectAsync();
        ulong targetGuid = await host.PlayerStateAsync("Spared", p => p.Guid.Value);

        await caster.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(Bolt, targetGuid));
        await caster.ReadUntilAsync(WorldOpcode.SmsgSpellStart);
        await caster.SendAsync(WorldOpcode.CmsgCancelCast, BitConverter.GetBytes(Bolt));

        byte[] result = await caster.ReadUntilAsync(WorldOpcode.SmsgCastResult);
        Assert.Equal((byte)SpellCastResult.Interrupted, result[5]);
        Assert.DoesNotContain((await target.CollectAsync(TimeSpan.FromMilliseconds(700))).Select(p => p.Opcode), op => op == WorldOpcode.SmsgSpellnonmeleedamagelog);
    }

    [Fact]
    public async Task UnknownSpell_IsIgnored()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("CHEATER", "Cheater");
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(Learnable));

        Assert.DoesNotContain((await client.CollectAsync()).Select(p => p.Opcode), op => op is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo);
    }

    [Fact]
    public async Task LearnCommand_TeachesASpell_ThatSurvivesRelogging_AndCanBeCast()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("TEACHER", "Teacher", AccountSecurity.Administrator);
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, $".learn {Learnable}");
        Assert.Equal(BitConverter.GetBytes(Learnable), await gm.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell));

        await gm.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await gm.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
        Account account = (await host.Accounts.FindByUsernameAsync("TEACHER"))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        await gm.LoginAsync((ulong)record.Id);
        Assert.Equal([Heal, Bolt, Learnable], ParseInitialSpells(gm.LoginPacket(WorldOpcode.SmsgInitialSpells)));
        await gm.CollectAsync();

        await gm.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(Learnable));
        await gm.ReadUntilAsync(WorldOpcode.SmsgSpellGo);

        await gm.SendChatAsync(ChatType.Say, Language.Common, $".unlearn {Learnable}");
        Assert.Equal(new byte[] { 0x2B, 0x23 }, await gm.ReadUntilAsync(WorldOpcode.SmsgRemovedSpell));
    }

    [Fact]
    public async Task PeriodicAura_ShowsInTheAuraFields_AndCancelAuraRemovesIt()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RENEWER", "Renewer", AccountSecurity.Administrator);
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, $".cast {Renew}");
        Assert.Equal(0, (await gm.ReadUntilAsync(WorldOpcode.SmsgUpdateAuraDuration))[0]); // positive slot 0
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Renewer")!.GetUInt32(UpdateFields.UnitFieldAura) == Renew, "aura field");

        await gm.SendAsync(WorldOpcode.CmsgCancelAura, BitConverter.GetBytes(Renew));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Renewer")!.GetUInt32(UpdateFields.UnitFieldAura) == 0, "aura removed");
    }

    [Fact]
    public async Task CastSelf_NeedsASelectedUnit_ThenCastsOnIt_AndRejectsBadSyntax()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("SELFCAST", "Selfcast", AccountSecurity.Administrator);
        await gm.CollectAsync();
        Player self = await host.PlayerAsync("Selfcast");

        await gm.SendChatAsync(ChatType.Say, Language.Common, $".cast self {Renew}");
        Assert.Equal("Select a character or creature.", (await gm.ReadChatAsync()).Text);
        Assert.Equal(0u, self.GetUInt32(UpdateFields.UnitFieldAura));

        await gm.SendAsync(WorldOpcode.CmsgSetSelection, BitConverter.GetBytes(self.Guid.Value));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Selfcast")!.Selection == self.Guid, "self selected");
        await gm.SendChatAsync(ChatType.Say, Language.Common, $".cast self {Renew} junk");
        Assert.StartsWith("Syntax: .cast self", (await gm.ReadChatAsync()).Text, StringComparison.Ordinal); // the command ran and refused it
        await gm.CollectRepliesAsync(); // the rest of the multi-line help
        Assert.Equal(0u, self.GetUInt32(UpdateFields.UnitFieldAura)); // trailing junk is a syntax error (vmangos: only "triggered" may follow)

        await gm.SendChatAsync(ChatType.Say, Language.Common, $".cast self {Renew} triggered");
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Selfcast")!.GetUInt32(UpdateFields.UnitFieldAura) == Renew, "aura from .cast self");
    }

    private static async Task<string> RunCommandAsync(WorldTestClient client, string command, Language language = Language.Common)
    {
        await client.SendChatAsync(ChatType.Say, language, command);
        return (await client.ReadChatAsync()).Text;
    }

    private static Task SelectAsync(WorldTestHost host, string caller, string target) => host.OnWorldAsync(() =>
        host.World.FindOnlinePlayer(caller)!.Selection = host.World.FindOnlinePlayer(target)!.Guid);

    [Fact]
    public async Task CastTarget_NeedsAVictim_ThenTheSelectedUnitCastsOnIt()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("CTGM", "Ctgm", AccountSecurity.Administrator);
        await using WorldTestClient caster = await host.EnterWorldAsync("CTCAST", "Ctcast");
        await using WorldTestClient victim = await host.EnterWorldAsync("CTVICT", "Ctvict", race: 2);
        await FlagForPvpAsync(host, "Ctvict");
        await gm.CollectAsync();
        await SelectAsync(host, "Ctgm", "Ctcast");
        uint health = await host.PlayerStateAsync("Ctvict", p => p.Health);

        Assert.Equal("The selected unit has no victim.", await RunCommandAsync(gm, $".cast target {Bolt} triggered"));
        Assert.Equal(health, await host.PlayerStateAsync("Ctvict", p => p.Health));

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Ctcast")!.Combat.Victim = host.World.FindOnlinePlayer("Ctvict"));
        Assert.Equal($"Cast spell {Bolt}.", await RunCommandAsync(gm, $".cast target {Bolt} triggered"));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Ctvict")!.Health == health - 7, "the bolt lands on the selected unit's victim");
    }

    [Fact]
    public async Task CastBack_MakesTheSelectedUnitCastOnTheInvoker()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("CBGM", "Cbgm", AccountSecurity.Administrator, race: 2);
        await using WorldTestClient caster = await host.EnterWorldAsync("CBCAST", "Cbcast");
        await FlagForPvpAsync(host, "Cbgm");
        await gm.CollectAsync();
        await SelectAsync(host, "Cbgm", "Cbcast");
        uint health = await host.PlayerStateAsync("Cbgm", p => p.Health);

        Assert.Equal($"Cast spell {Bolt}.", await RunCommandAsync(gm, $".cast back {Bolt} triggered", Language.Orcish));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Cbgm")!.Health == health - 7, "the selected unit's bolt lands on the invoker");
    }

    [Fact]
    public async Task CastDist_ChecksItsArguments_AndPointInFrontFollowsTheFacing()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("CDGM", "Cdgm", AccountSecurity.Administrator);
        await gm.CollectAsync();

        Assert.Equal("Spell 999999 does not exist.", await RunCommandAsync(gm, ".cast dist 999999 5"));
        Assert.StartsWith("Syntax: .cast dist", await RunCommandAsync(gm, $".cast dist {Heal}"), StringComparison.Ordinal); // the distance is required
        await gm.CollectRepliesAsync();

        (float x, float y, float z) = SpellCommands.PointInFront(10f, 20f, 5f, 0f, 3f);
        Assert.Equal((13f, 20f, 5f), (x, y, z));
        (x, y, z) = SpellCommands.PointInFront(10f, 20f, 5f, MathF.PI / 2f, 4f);
        Assert.Equal(10.0, x, 0.001);
        Assert.Equal(24.0, y, 0.001);
        Assert.Equal(5f, z);
        (x, y, _) = SpellCommands.PointInFront(10f, 20f, 5f, MathF.PI, -2f);
        Assert.Equal(12.0, x, 0.001);
        Assert.Equal(20.0, y, 0.001);
    }

    [Fact]
    public async Task CastDist_CastsAtThePointInFront_AndReplies()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("CDOKGM", "Cdokgm", AccountSecurity.Administrator);
        await gm.CollectAsync();

        Assert.Equal($"Cast spell {Heal}.", await RunCommandAsync(gm, $".cast dist {Heal} 5 triggered"));
        Assert.Equal($"Cast spell {Heal}.", await RunCommandAsync(gm, $".cast dist |cff71d5ff|Hspell:{Heal}|h[Test Heal]|h|r 5 triggered")); // the shift-click link form
    }

    [Fact]
    public async Task TheCastSubcommands_AcceptASpellShiftClickLink()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("LNKGM", "Lnkgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await SelectAsync(host, "Lnkgm", "Lnkgm");

        Assert.Equal($"Cast spell {Renew}.", await RunCommandAsync(gm, $".cast self |cff71d5ff|Hspell:{Renew}|h[Test Renew]|h|r triggered"));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Lnkgm")!.GetUInt32(UpdateFields.UnitFieldAura) == Renew, "aura from a linked .cast self");
        Assert.Equal($"Cast spell {Renew}.", await RunCommandAsync(gm, $".cast |cff71d5ff|Hspell:{Renew}|h[Test Renew]|h|r"));
    }

    [Fact]
    public async Task ClosePoint_AddsTheCastersBoundingRadius_ToTheDistanceAheadOfIt()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("CPGM", "Cpgm", AccountSecurity.Administrator);
        await gm.CollectAsync();

        (float px, float py, float o, float radius, float x, float y) = await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Cpgm")!;
            (float cx, float cy, _) = SpellCommands.ClosePoint(p, 4f);
            return (p.X, p.Y, p.Orientation, p.BoundingRadius, cx, cy);
        });

        Assert.True(radius > 0f);
        Assert.Equal(px + (MathF.Cos(o) * (4f + radius)), x, 0.001);
        Assert.Equal(py + (MathF.Sin(o) * (4f + radius)), y, 0.001);
    }

    [Fact]
    public void ClosePoint_PutsZOnTheGroundPlusFiveHundredths_WhenAHeightIsKnown_AndKeepsTheCastersZOtherwise()
    {
        var asked = new List<(float X, float Y, float Z)>();
        (float x, float y, float z) = SpellCommands.ClosePoint(10f, 20f, 50f, 0f, 0.5f, 4f, (px, py, pz) =>
        {
            asked.Add((px, py, pz));
            return 33f;
        });

        Assert.Equal(14.5, x, 0.001); // 4 yards plus the 0.5 radius, east
        Assert.Equal(20.0, y, 0.001);
        Assert.Equal(33.05, z, 0.0001); // ground + 0.05, not the caster's own 50
        (float askedX, float askedY, float askedZ) = Assert.Single(asked); // the ground is looked up at the point itself, from the caster's height
        Assert.Equal((x, y, 50f), (askedX, askedY, askedZ));

        Assert.Equal(50f, SpellCommands.ClosePoint(10f, 20f, 50f, 0f, 0.5f, 4f, (_, _, _) => TerrainTile.InvalidHeightValue).Z);
        Assert.Equal(50f, SpellCommands.ClosePoint(10f, 20f, 50f, 0f, 0.5f, 4f, (_, _, _) => float.NaN).Z);
        Assert.Equal(50f, SpellCommands.ClosePoint(10f, 20f, 50f, 0f, 0.5f, 4f, null).Z);
    }

    private static Task<string?> RunDirectAsync(WorldTestHost host, string character, string path, string args) => host.OnWorldAsync(() =>
    {
        Player player = host.World.FindOnlinePlayer(character)!;
        CommandTable table = ChatCommands.CreateTable();
        var context = new CommandContext((WorldSession)player.Session, player, table);
        ChatCommand command = table.Resolve(path, AccountSecurity.Administrator)!;
        context.Bind(command, path); // what CommandTable.Execute does
        Assert.True(command.Handler!(context, args));
        context.Complete();
        return context.Failure;
    });

    [Fact]
    public async Task ABrokenSpell_IsRefusedByCast_CastSelf_AndCastDist_ButNotByCastTargetOrCastBack()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("BRKGM", "Brkgm", AccountSecurity.Administrator);
        await using WorldTestClient other = await host.EnterWorldAsync("BRKOTH", "Brkoth");
        await gm.CollectAsync();
        // The validity check judges items by the loaded item store: with no item data it passes everything, so install one template.
        await host.OnWorldAsync(() =>
            ((WorldSession)host.World.FindOnlinePlayer("Brkgm")!.Session).Services.GetRequiredService<ItemsFeature>()
                .ReplaceTemplates(new ItemTemplateStore([new ItemTemplate { Entry = PresentItem }])));
        await SelectAsync(host, "Brkgm", "Brkgm");
        string refusal(uint spell) => $"Spell {spell} broken and not allowed to cast or learn!"; // vmangos mangos_string 483, LANG_COMMAND_SPELL_BROKEN

        foreach (uint spell in new[] { BrokenCreate, BrokenTeach })
        {
            foreach ((string path, string args) in new[] { ("cast", $"{spell}"), ("cast self", $"{spell} triggered"), ("cast dist", $"{spell} 5 triggered") })
            {
                await gm.CollectAsync();
                string? failure = await RunDirectAsync(host, "Brkgm", path, args);

                Assert.True("spell-broken" == failure, $"{path} {spell}: failure was {failure ?? "none"}");
                Assert.Equal(refusal(spell), (await gm.ReadSystemChatAsync()).Text);
                Assert.DoesNotContain((await gm.CollectAsync()).Select(p => p.Opcode), op => op is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo);
            }
        }

        // The control: vmangos checks IsSpellValid in .cast, .cast self and .cast dist only, so .cast back and .cast target let the same spell through to the cast.
        await SelectAsync(host, "Brkgm", "Brkoth");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Brkoth")!.Combat.Victim = host.World.FindOnlinePlayer("Brkgm"));
        foreach (string path in new[] { "cast back", "cast target" })
        {
            await gm.CollectAsync();
            string? failure = await RunDirectAsync(host, "Brkgm", path, $"{BrokenTeach} triggered");

            Assert.Null(failure);
            Assert.NotEqual(refusal(BrokenTeach), (await gm.ReadSystemChatAsync()).Text);
        }
    }

    [Fact]
    public async Task TheCastSubcommands_RefuseAHigherSecurityPlayer_AsTheSelectionAndAsTheVictim()
    {
        // .cast and its subcommands need retail level 5; mapping GameMaster to 6 lets a lower-security GM run them so the target check is what is under test.
        await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["World:GmCommands:SecurityMap:GameMaster"] = "6",
                ["World:GmCommands:LowerSecurity"] = "true",
            }).Build()));
        await using WorldTestClient gm = await host.EnterWorldAsync("LSGM", "Lsgm", AccountSecurity.GameMaster);
        await using WorldTestClient admin = await host.EnterWorldAsync("LSADM", "Lsadm", AccountSecurity.Administrator, race: 2);
        await using WorldTestClient other = await host.EnterWorldAsync("LSOTH", "Lsoth");
        await FlagForPvpAsync(host, "Lsadm");
        await gm.CollectAsync();
        uint health = await host.PlayerStateAsync("Lsadm", p => p.Health);
        const string refused = "You have low security level for this.";

        // The higher-security player is the selection: it is neither the caster of .cast self nor the target of .cast back's selected caster.
        await SelectAsync(host, "Lsgm", "Lsadm");
        Assert.Equal(refused, await RunCommandAsync(gm, $".cast self {Renew} triggered"));
        Assert.Equal(refused, await RunCommandAsync(gm, $".cast back {Bolt} triggered"));
        Assert.Equal(0u, await host.PlayerStateAsync("Lsadm", p => p.GetUInt32(UpdateFields.UnitFieldAura)));

        // The higher-security player is only the victim of the selected unit: .cast target may not land a spell on it either.
        await SelectAsync(host, "Lsgm", "Lsoth");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Lsoth")!.Combat.Victim = host.World.FindOnlinePlayer("Lsadm"));
        Assert.Equal(refused, await RunCommandAsync(gm, $".cast target {Bolt} triggered"));
        Assert.Equal(health, await host.PlayerStateAsync("Lsadm", p => p.Health));

        // Control: the same selected unit and victim, once the victim is the invoker's own rank or lower, is allowed (the gate, not the setup, refused above).
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Lsoth")!.Combat.Victim = host.World.FindOnlinePlayer("Lsgm"));
        Assert.Equal($"Cast spell {Renew}.", await RunCommandAsync(gm, $".cast target {Renew} triggered"));
    }

    [Fact]
    public async Task ACooldownOnEventAura_CancelledByTheClient_SendsCooldownEvent_AndRefusesARecastUntilItEnds()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("SNEAKER", "Sneaker", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await gm.SendChatAsync(ChatType.Say, Language.Common, $".learn {StealthLike}");
        await gm.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
        await gm.CollectAsync();

        await gm.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(StealthLike));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Sneaker")!.GetUInt32(UpdateFields.UnitFieldAura) == StealthLike, "stealth aura");
        Assert.DoesNotContain((await gm.CollectAsync()).Select(p => p.Opcode), op => op == WorldOpcode.SmsgCooldownEvent);

        await gm.SendAsync(WorldOpcode.CmsgCancelAura, BitConverter.GetBytes(StealthLike));

        Assert.Equal(StealthLike, BinaryPrimitives.ReadUInt32LittleEndian(await gm.ReadUntilAsync(WorldOpcode.SmsgCooldownEvent)));
        await gm.SendAsync(WorldOpcode.CmsgCastSpell, CastPayload(StealthLike));
        byte[] refused = await gm.ReadUntilAsync(WorldOpcode.SmsgCastResult);
        Assert.Equal((byte)SpellCastResult.NotReady, refused[5]);
        Assert.Equal(0u, host.World.FindOnlinePlayer("Sneaker")!.GetUInt32(UpdateFields.UnitFieldAura));
    }

    [Fact]
    public async Task SpellbookCache_GrantsDefaultsOnce_AndWritesThroughInOrder()
    {
        var store = new InMemoryCharacterSpellStore();
        await using ServiceProvider services = new ServiceCollection()
            .AddScoped<Data.Characters.Spells.ICharacterSpellStore>(_ => store)
            .BuildServiceProvider();
        var cache = new SpellbookCache(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        cache.Load([new Data.Characters.Spells.CharacterSpellRow { CharacterId = 5, Spell = 1 }]);
        cache.Start();
        Player fresh = NewPlayer(7);
        Player veteran = NewPlayer(5);

        Assert.True(cache.EnsureDefaults(fresh, [Heal, Bolt]));
        Assert.False(cache.EnsureDefaults(fresh, [Learnable]));
        Assert.False(cache.EnsureDefaults(veteran, [Heal]));
        Assert.True(cache.LearnSpell(fresh, Learnable));
        Assert.False(cache.LearnSpell(fresh, Learnable));
        Assert.True(cache.ForgetSpell(fresh, Heal));
        Assert.False(cache.ForgetSpell(fresh, Heal));
        await cache.FlushAsync();

        Assert.Equal([Bolt, Learnable], cache.GetSpells(fresh));
        Assert.Equal([Bolt, Learnable], await store.GetAsync(7));
        Assert.Equal([1u], cache.GetSpells(veteran));
        Assert.Empty(await store.GetAsync(5)); // loaded rows are not rewritten

        cache.DeleteCharacter(7);
        await cache.DisposeAsync();
        Assert.Empty(await store.GetAsync(7));
    }

    private static Player NewPlayer(int id) => new(
        new CharacterRecord { Id = id, AccountId = 1, Name = $"P{id}", Race = 1, Class = 1, Level = 1 },
        new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400),
        new NullPlayerSession());

    private static byte[] CastPayload(uint spell, ulong unit = 0)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt32(spell);
        (unit == 0 ? SpellCastTargets.ForSelf() : SpellCastTargets.ForUnit(new ObjectGuid(unit))).Write(writer);
        return writer.ToArray();
    }

    private static List<uint> ParseInitialSpells(byte[] packet)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(1));
        return [.. Enumerable.Range(0, count).Select(i => (uint)BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(3 + (i * 4))))];
    }
}
