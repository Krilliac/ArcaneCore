using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Skills;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>
/// Zone scripts batch 6, the gossip registrations of <see cref="QuestNpcFeature"/> end to end: a client's CMSG_GOSSIP_HELLO and
/// CMSG_GOSSIP_SELECT_OPTION against Lady Katrana Prestor (1749), Regthar Deathgate (3389) and a Tamed Kodo (11627), with the quests from the
/// player's own journal. The three quests carry their ClassicDB z2815 objective data: 4185 "The True Masters" SpecialFlags 2 (event), 4021
/// "Counterattack!" no objective, 5561 "Kodo Roundup" ReqCreatureOrGOId1 11627 x5, SpecialFlags 0. The creatures use a friendly test faction
/// in place of their real ones (12, 125, 35), which this host's faction catalog does not hold.
/// </summary>
public sealed class ZoneScriptBatch6GossipWorldTests
{
    private const uint Katrana = KatranaPrestorGossip.Entry, Regthar = RegtharDeathgateAI.Entry, TamedKodo = AgedDyingAncientKodoAI.NpcTamedKodo;
    private const uint TrueMasters = KatranaPrestorGossip.QuestTrueMasters, Counterattack = RegtharDeathgateAI.QuestCounterattack;
    private const uint KodoRoundup = AgedDyingAncientKodoAI.QuestKodoRoundup;

    private static readonly ObjectGuid KatranaGuid = ObjectGuid.WithEntry(HighGuid.Unit, Katrana, 960_001);
    private static readonly ObjectGuid RegtharGuid = ObjectGuid.WithEntry(HighGuid.Unit, Regthar, 960_002);
    private static readonly ObjectGuid KodoGuid = ObjectGuid.WithEntry(HighGuid.Unit, TamedKodo, 960_003);

    private static WorldTestHost Start(Batch6GossipFixture fixture)
    {
        Batch6GossipTestServices.Current.Value = fixture;
        try { return WorldTestHost.Start(); }
        finally { Batch6GossipTestServices.Current.Value = null; }
    }

    /// <summary>Creates the character, puts <paramref name="quests"/> in its log as incomplete, logs it in and waits for the journal and the three NPCs.</summary>
    private static async Task<WorldTestClient> EnterAsync(WorldTestHost host, Batch6GossipFixture fixture, string name, params uint[] quests)
    {
        WorldTestClient client = await host.ConnectAsync();
        string account = name.ToUpperInvariant();
        byte[] key = await host.AddAccountAsync(account);
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(name);
        var record = (await host.Accounts.FindByUsernameAsync(account))!;
        CharacterRecord character = (await host.Characters.GetByAccountAsync(record.Id)).Single();
        fixture.Characters.Seed([.. quests.Select(q => new CharacterQuestStatus(character.Id, q, (byte)QuestStatus.Incomplete, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0))]);
        await client.LoginAsync((ulong)character.Id);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name) is { } p
            && Feature(p).Services.StateOf(p) is { Loaded: true }
            && p.VisibleObjects.Contains(KatranaGuid) && p.VisibleObjects.Contains(RegtharGuid) && p.VisibleObjects.Contains(KodoGuid),
            "the quest journal to load and the three NPCs to show");
        return client;
    }

    private static QuestNpcFeature Feature(Player p) => ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>();

    private static byte[] Guid(ObjectGuid guid) => BitConverter.GetBytes(guid.Value);

    private static byte[] Select(ObjectGuid guid, uint listId) => [.. Guid(guid), .. BitConverter.GetBytes(listId)];

    /// <summary>SMSG_GOSSIP_MESSAGE (NpcPackets.GossipMessage): the npc, the text and the option texts.</summary>
    private static (ulong Npc, uint Text, List<string> Options) ReadMenu(byte[] body)
    {
        var reader = new PacketReader(body);
        ulong npc = reader.ReadUInt64();
        uint text = reader.ReadUInt32();
        uint count = reader.ReadUInt32();
        var options = new List<string>();
        for (uint i = 0; i < count; i++)
        {
            Assert.Equal(i, reader.ReadUInt32());
            reader.ReadByte(); // icon
            Assert.Equal((byte)0, reader.ReadByte()); // not coded
            options.Add(reader.ReadCString());
        }

        return (npc, text, options);
    }

    private static Task<(QuestStatus Status, uint Count)> QuestAsync(WorldTestHost host, string name, uint quest) => host.OnWorldAsync(() =>
    {
        Player p = host.World.FindOnlinePlayer(name)!;
        var data = Feature(p).Services.StateOf(p)!.Quests.Get(quest)!;
        return (data.Status, data.CreatureOrGOCount[0]);
    });

    [Fact]
    public async Task Katrana_WithTheTrueMastersIncomplete_ShowsTheFirstLine_AndTheFourLinesCompleteTheQuest_ThenTheLineIsGone()
    {
        var fixture = new Batch6GossipFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await EnterAsync(host, fixture, "Katrana", TrueMasters);

        await client.SendAsync(WorldOpcode.CmsgGossipHello, Guid(KatranaGuid));
        (ulong npc, uint text, List<string> options) = ReadMenu(await client.ReadUntilAsync(WorldOpcode.SmsgGossipMessage));
        Assert.Equal(KatranaGuid.Value, npc);
        Assert.Equal(KatranaPrestorGossip.TextMenu1, text);
        Assert.Equal([KatranaPrestorGossip.OptionKat1], options);

        (uint Text, string Option)[] next =
        [
            (KatranaPrestorGossip.TextMenu2, KatranaPrestorGossip.OptionKat2),
            (KatranaPrestorGossip.TextMenu3, KatranaPrestorGossip.OptionKat3),
            (KatranaPrestorGossip.TextMenu4, KatranaPrestorGossip.OptionKat4),
        ];
        foreach ((uint menuText, string option) in next)
        {
            await client.SendAsync(WorldOpcode.CmsgGossipSelectOption, Select(KatranaGuid, 0));
            (_, uint shown, List<string> lines) = ReadMenu(await client.ReadUntilAsync(WorldOpcode.SmsgGossipMessage));
            Assert.Equal(menuText, shown);
            Assert.Equal([option], lines);
            Assert.Equal(QuestStatus.Incomplete, (await QuestAsync(host, "Katrana", TrueMasters)).Status);
        }

        await client.SendAsync(WorldOpcode.CmsgGossipSelectOption, Select(KatranaGuid, 0));
        await client.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
        Assert.Equal(QuestStatus.Complete, (await QuestAsync(host, "Katrana", TrueMasters)).Status);

        await client.SendAsync(WorldOpcode.CmsgGossipHello, Guid(KatranaGuid));
        (_, uint again, List<string> after) = ReadMenu(await client.ReadUntilAsync(WorldOpcode.SmsgGossipMessage));
        Assert.Equal(KatranaPrestorGossip.TextMenu1, again);
        Assert.Empty(after); // the quest is no longer incomplete
    }

    [Fact]
    public async Task Regthar_WithCounterattackIncomplete_ShowsWhereIsKromzar_WithoutIt_NoLine()
    {
        var fixture = new Batch6GossipFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient with = await EnterAsync(host, fixture, "Regthar", Counterattack);
        await using WorldTestClient without = await EnterAsync(host, fixture, "Norquest");

        await with.SendAsync(WorldOpcode.CmsgGossipHello, Guid(RegtharGuid));
        (ulong npc, uint text, List<string> options) = ReadMenu(await with.ReadUntilAsync(WorldOpcode.SmsgGossipMessage));
        Assert.Equal(RegtharGuid.Value, npc);
        Assert.Equal(RegtharDeathgateGossip.TextMenu, text);
        Assert.Equal([RegtharDeathgateGossip.OptionWhereIsKromzar], options);

        await without.SendAsync(WorldOpcode.CmsgGossipHello, Guid(RegtharGuid));
        (_, uint otherText, List<string> none) = ReadMenu(await without.ReadUntilAsync(WorldOpcode.SmsgGossipMessage));
        Assert.Equal(RegtharDeathgateGossip.TextMenu, otherText);
        Assert.Empty(none);
    }

    [Fact]
    public async Task TamedKodo_AHelloWithBothBuffsAdvancesKodoRoundup_AndTakesThePlayersBuff_WithoutTheBuffsNothing()
    {
        var fixture = new Batch6GossipFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await EnterAsync(host, fixture, "Kodoherd", KodoRoundup);

        await client.SendAsync(WorldOpcode.CmsgGossipHello, Guid(KodoGuid)); // no buffs yet
        await client.ReadUntilAsync(WorldOpcode.SmsgGossipMessage);
        Assert.Equal((QuestStatus.Incomplete, 0u), await QuestAsync(host, "Kodoherd", KodoRoundup));

        (bool playerBuff, bool kodoBuff) = await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Kodoherd")!;
            SpellSystem spells = ((WorldSession)p.Session).Services.GetRequiredService<SpellFeature>().System;
            Unit kodo = (Unit)p.Map!.FindObject(KodoGuid)!;
            spells.CastSpell(p, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff, SpellCastTargets.ForUnit(p.Guid), triggered: true);
            spells.CastSpell(kodo, AgedDyingAncientKodoAI.SpellKodoKomboDespawnBuff, SpellCastTargets.ForSelf(), triggered: true);
            return (spells.HasAura(p, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff), spells.HasAura(kodo, AgedDyingAncientKodoAI.SpellKodoKomboDespawnBuff));
        });
        Assert.True(playerBuff);
        Assert.True(kodoBuff);

        await client.SendAsync(WorldOpcode.CmsgGossipHello, Guid(KodoGuid));
        var update = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgQuestupdateAddKill));
        Assert.Equal(KodoRoundup, update.ReadUInt32());
        Assert.Equal(TamedKodo, update.ReadUInt32());
        Assert.Equal(1u, update.ReadUInt32());
        Assert.Equal(5u, update.ReadUInt32());
        Assert.Equal(KodoGuid.Value, update.ReadUInt64());

        Assert.Equal((QuestStatus.Incomplete, 1u), await QuestAsync(host, "Kodoherd", KodoRoundup));
        Assert.False(await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Kodoherd")!;
            return ((WorldSession)p.Session).Services.GetRequiredService<SpellFeature>().System.HasAura(p, AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff);
        }));

        await client.SendAsync(WorldOpcode.CmsgGossipHello, Guid(KodoGuid)); // the player's buff is gone: no second credit
        await client.ReadUntilAsync(WorldOpcode.SmsgGossipMessage);
        Assert.Equal((QuestStatus.Incomplete, 1u), await QuestAsync(host, "Kodoherd", KodoRoundup));
    }

    [Fact]
    public async Task KodoRoundup_CountsTheTamedKodoByKillCreditAndByTalkCredit_ButNotAnotherEntry()
    {
        var fixture = new Batch6GossipFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await EnterAsync(host, fixture, "Kodocount", KodoRoundup);

        await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Kodocount")!;
            Feature(p).Services.KilledMonsterCredit(p, TamedKodo, KodoGuid);
        });
        Assert.Equal((QuestStatus.Incomplete, 1u), await QuestAsync(host, "Kodocount", KodoRoundup));

        // The script's seam (ScriptQuestEvents → QuestNpcServices.TalkedToCreature), what the kodo gossip calls.
        await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Kodocount")!;
            var events = new ScriptQuestEvents(((WorldSession)p.Session).Services);
            events.TalkedToCreature(p, TamedKodo, KodoGuid);
            events.TalkedToCreature(p, AgedDyingAncientKodoAI.NpcAgedKodo, KodoGuid); // not the objective
        });
        Assert.Equal((QuestStatus.Incomplete, 2u), await QuestAsync(host, "Kodocount", KodoRoundup));
    }
}

internal sealed class Batch6GossipTestServices : IWorldTestServices
{
    public static readonly AsyncLocal<Batch6GossipFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<IQuestContentStore>(fixture);
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<ICharacterQuestStore>(fixture.Characters);
        services.AddSingleton<ISpellContentStore>(new InMemorySkillSpellContentStore(Batch6GossipFixture.Spells()));
        services.AddSingleton(new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0), new(Batch6GossipFixture.Friendly, 0, 0, 8, 0, 0)]));
    }
}

internal sealed class Batch6GossipFixture : IQuestContentStore, ICreatureDataStore
{
    public const uint Friendly = 900011;

    public MemoryQuestStore Characters { get; } = new();

    public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new QuestContent(
    [
        new QuestTemplate { Entry = KatranaPrestorGossip.QuestTrueMasters, Method = 2, MinLevel = 1, QuestLevel = 54, SpecialFlags = 2, Title = "The True Masters" },
        new QuestTemplate { Entry = RegtharDeathgateAI.QuestCounterattack, Method = 2, MinLevel = 1, QuestLevel = 20, Title = "Counterattack!" },
        new QuestTemplate
        {
            Entry = AgedDyingAncientKodoAI.QuestKodoRoundup, Method = 2, MinLevel = 1, QuestLevel = 34, SpecialFlags = 0, Title = "Kodo Roundup",
            ReqCreatureOrGOId1 = (int)AgedDyingAncientKodoAI.NpcTamedKodo, ReqCreatureOrGOCount1 = 5,
        },
    ], [], []));

    // Near the human start (-8949.95, -132.49, 83.53), within talking range. Npc flags as z2815: 1749 and 3389 gossip + quest giver (3), 11627 gossip (1).
    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
        [
            new CreatureTemplate { Entry = KatranaPrestorGossip.Entry, Name = "Lady Katrana Prestor", Faction = Friendly, NpcFlags = 3, DisplayIds = [49], MinLevel = 62, MaxLevel = 62 },
            new CreatureTemplate { Entry = RegtharDeathgateAI.Entry, Name = "Regthar Deathgate", Faction = Friendly, NpcFlags = 3, DisplayIds = [49], MinLevel = 28, MaxLevel = 28 },
            new CreatureTemplate { Entry = AgedDyingAncientKodoAI.NpcTamedKodo, Name = "Tamed Kodo", Faction = Friendly, NpcFlags = 1, DisplayIds = [49], MinLevel = 34, MaxLevel = 34 },
        ],
        [
            new CreatureSpawn { Guid = 960_001, Entry = KatranaPrestorGossip.Entry, MapId = 0, X = -8947.95f, Y = -132.49f, Z = 83.53f },
            new CreatureSpawn { Guid = 960_002, Entry = RegtharDeathgateAI.Entry, MapId = 0, X = -8949.95f, Y = -130.49f, Z = 83.53f },
            new CreatureSpawn { Guid = 960_003, Entry = AgedDyingAncientKodoAI.NpcTamedKodo, MapId = 0, X = -8951.95f, Y = -132.49f, Z = 83.53f },
        ], [], [], []));

    /// <summary>
    /// 18172 and 18377 as the 5875 Spell.dbc has them where the hand-in reads them: instant (CastingTimeIndex 1), DurationIndex 5 (300 s), no
    /// reagents or cost; 18172 an APPLY_AURA dummy on TARGET_UNIT_FRIEND (21), 18377 an aura on the caster (1). 18377's aura is a dummy here
    /// (the DBC's periodic trigger of 18269, which this host does not know); only its presence is read.
    /// </summary>
    public static SpellContent Spells() => new(
        [
            new SpellTemplateRow
            {
                Id = AgedDyingAncientKodoAI.SpellKodoKomboPlayerBuff, SpellName = "Kodo Kombo Player Buff", CastingTimeIndex = 1, DurationIndex = 5, RangeIndex = 6,
                Attributes = 0x100, Effect1 = 6, EffectApplyAuraName1 = 4, EffectImplicitTargetA1 = 21, EffectBaseDice1 = 1, EffectDieSides1 = 1,
            },
            new SpellTemplateRow
            {
                Id = AgedDyingAncientKodoAI.SpellKodoKomboDespawnBuff, SpellName = "Kodo Kombo Despawn Buff", CastingTimeIndex = 1, DurationIndex = 5, RangeIndex = 1,
                Attributes = 0x100, Effect1 = 6, EffectApplyAuraName1 = 4, EffectImplicitTargetA1 = 1, EffectBaseDice1 = 1, EffectDieSides1 = 1,
            },
        ],
        [new SpellCastTimeRow { Id = 1 }], [new SpellDurationRow { Id = 5, Duration = 300_000, MaxDuration = 300_000 }],
        [new SpellRangeRow { Id = 1 }, new SpellRangeRow { Id = 6, MaxRange = 100 }], [], [], []);
}
