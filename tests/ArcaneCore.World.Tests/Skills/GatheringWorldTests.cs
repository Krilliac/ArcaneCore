using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Tests.GameObjects;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Skills;

/// <summary>
/// Gathering over the loopback daemon: a mining or herbalism cast on a node opens it, loots it and raises the skill once per
/// player and node; the cast refuses a low skill, a missing target and an orange failure; skinning checks and rises the skill.
/// </summary>
public sealed class GatheringWorldTests
{
    private const uint MiningCast = 9200;
    private const uint HerbCast = 9201;
    private const uint SkinCast = 9202;
    private const uint SlowMiningCast = 9203;
    private const uint VeinEntry = 1731;
    private const uint VeinSpawn = 88001;
    private const uint VeinLoot = 1731;
    private const uint CopperOre = 2770;
    private const uint VeinLock = 1;
    private const uint HardVeinLock = 2;

    /// <summary>The skill-up roll: always 1, so a chance above zero always rises (and the float roll is always 0).</summary>
    private sealed class LowestRoll : ArcaneCore.Game.Combat.ICombatRandom
    {
        public int Next(int minInclusive, int maxInclusive) => minInclusive;

        public float NextFloat(float min, float max) => min;
    }

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int minValue, int maxValue) => Math.Clamp(value, minValue, maxValue - 1);
    }

    [Fact]
    public async Task MiningCast_OpensTheVein_LootsIt_AndRaisesTheSkillOncePerPlayerAndNode()
    {
        await using WorldTestHost host = Start(out GameObjectTestContext context, VeinLock);
        await using WorldTestClient client = await host.EnterWorldAsync("MINEONE", "Mineone");
        Player player = await host.PlayerAsync("Mineone");
        Game.Spells.SpellSystem spells = Spells(host);
        spells.Random = new FixedRandom(int.MaxValue);
        await host.OnWorldAsync(() => player.Skills!.Set(SkillIds.Mining, 1, 75, 1));

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastAtVein(MiningCast));
        var loot = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        Assert.Equal(VeinGuid(), loot.ReadUInt64());

        await host.WaitForWorldAsync(() => player.Skills!.GetValuePure(SkillIds.Mining) == 2, "the skill rises");
        GameObject vein = (await host.OnWorldAsync(() => context.Feature!.FindSystem(0)!.Find(new ObjectGuid(VeinGuid()))))!;
        Assert.Contains(player.Guid, vein.SkillupSet);

        // Release the loot window, then cast again: the node remembers the player, so there is no second skill-up.
        await client.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(VeinGuid()));
        await client.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse);
        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastAtVein(MiningCast));
        await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse);
        await host.OnWorldAsync(() => { });
        Assert.Equal((ushort)2, await host.OnWorldAsync(() => player.Skills!.GetValuePure(SkillIds.Mining)));
    }

    [Fact]
    public async Task ATooLowSkill_RefusesTheCast_WithLowCastlevel_AndTheNodeStaysClosed()
    {
        await using WorldTestHost host = Start(out GameObjectTestContext context, HardVeinLock);
        await using WorldTestClient client = await host.EnterWorldAsync("MINELOW", "Minelow");
        Player player = await host.PlayerAsync("Minelow");
        await host.OnWorldAsync(() => player.Skills!.Set(SkillIds.Mining, 10, 75, 1));

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastAtVein(MiningCast));
        byte[] result = await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);
        Assert.Equal((MiningCast, (byte)SpellCastResultStatus.Failure, (byte)SpellCastResult.LowCastlevel), (BitConverter.ToUInt32(result, 0), result[4], result[5]));
        GameObject vein = (await host.OnWorldAsync(() => context.Feature!.FindSystem(0)!.Find(new ObjectGuid(VeinGuid()))))!;
        Assert.Null(vein.Loot);
        Assert.Equal((ushort)10, await host.OnWorldAsync(() => player.Skills!.GetValuePure(SkillIds.Mining)));
    }

    [Fact]
    public async Task AnOrangeGather_CanFailWhenTheCastLands_AndAFailureLootsNothing()
    {
        await using WorldTestHost host = Start(out GameObjectTestContext context, HardVeinLock);
        await using WorldTestClient client = await host.EnterWorldAsync("MINEFAIL", "Minefail");
        Player player = await host.PlayerAsync("Minefail");
        Game.Spells.SpellSystem spells = Spells(host);
        await host.OnWorldAsync(() => player.Skills!.Set(SkillIds.Mining, 50, 75, 1));

        // Required 50 against irand(25, 87): a roll of 25 fails, 87 succeeds. The prepare check never rolls.
        spells.Random = new FixedRandom(0);
        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastAtVein(MiningCast));
        byte[] failure = await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);
        Assert.Equal((byte)SpellCastResult.TryAgain, failure[5]);
        GameObject vein = (await host.OnWorldAsync(() => context.Feature!.FindSystem(0)!.Find(new ObjectGuid(VeinGuid()))))!;
        Assert.Null(vein.Loot);
        Assert.Equal((ushort)50, await host.OnWorldAsync(() => player.Skills!.GetValuePure(SkillIds.Mining)));

        spells.Random = new FixedRandom(int.MaxValue);
        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastAtVein(MiningCast));
        await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse);
    }

    [Fact]
    public async Task TheOrangeFailure_HappensWhenTheCastBarEnds_NotWhenItStarts()
    {
        await using WorldTestHost host = Start(out _, HardVeinLock);
        await using WorldTestClient client = await host.EnterWorldAsync("MINESLOW", "Mineslow");
        Player player = await host.PlayerAsync("Mineslow");
        Game.Spells.SpellSystem spells = Spells(host);
        await host.OnWorldAsync(() => player.Skills!.Set(SkillIds.Mining, 50, 75, 1));
        spells.Random = new FixedRandom(0);                                 // every orange roll fails

        // vmangos only rolls once the spell is its caster's current spell (Spell.cpp:3403 prepare, then :3482), so the bar
        // starts (SMSG_SPELL_START) and the failure comes with the landing check.
        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastAtVein(SlowMiningCast));
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellStart);
        byte[] failure = await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);
        Assert.Equal((byte)SpellCastResult.TryAgain, failure[5]);
    }

    [Fact]
    public async Task APlayerWithoutTheProfession_CannotOpenAVein_AndAWrongTargetIsABadTarget()
    {
        await using WorldTestHost host = Start(out _, VeinLock);
        await using WorldTestClient client = await host.EnterWorldAsync("NOSKILL", "Noskill");

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastAtVein(MiningCast));
        byte[] result = await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);
        Assert.Equal((byte)SpellCastResult.LowCastlevel, result[5]);   // skill 0 against requirement 1

        // No game object in the target block: BAD_TARGETS (the effect needs TARGET_GAMEOBJECT).
        var noTarget = new PacketWriter(6);
        noTarget.WriteUInt32(MiningCast);
        noTarget.WriteUInt16(0);
        await client.SendAsync(WorldOpcode.CmsgCastSpell, noTarget.ToArray());
        byte[] bad = await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);
        Assert.Equal((byte)SpellCastResult.BadTargets, bad[5]);
    }

    [Fact]
    public async Task Skinning_NeedsASkinnableCorpse_ASkillAgainstTheLevel_AndRaisesTheSkill()
    {
        await using WorldTestHost host = Start(out _, VeinLock);
        await using WorldTestClient client = await host.EnterWorldAsync("SKINNER", "Skinner");
        Player player = await host.PlayerAsync("Skinner");
        Game.Spells.SpellSystem spells = Spells(host);
        spells.Random = new FixedRandom(int.MaxValue);
        var template = new CreatureTemplate { Entry = 4001, Name = "Test Boar", MinLevel = 20, MaxLevel = 20, MinLevelHealth = 60, MaxLevelHealth = 60, Faction = 32, Rank = (uint)CreatureRank.Normal };
        var spawn = new CreatureSpawn { Guid = 4001, Entry = template.Entry, MapId = 0, X = player.X + 1, Y = player.Y, Z = player.Z };
        var boar = new Creature(4001, template, spawn, new CreatureContent([template], [spawn], [], [], []), new Random(1));
        await host.OnWorldAsync(() =>
        {
            host.World.GetMap(0).AddObject(boar);
            boar.Health = 0;
        });
        await host.OnWorldAsync(() => player.Skills!.Set(SkillIds.Skinning, 40, 75, 1));

        SpellCastResult Cast() => host.OnWorldAsync(() => spells.CastSpell(player, SkinCast, SpellCastTargets.ForUnit(boar.Guid), triggered: false)).GetAwaiter().GetResult();

        Assert.Equal(SpellCastResult.TargetUnskinnable, Cast());            // not skinnable yet
        boar.UnitFlags |= UnitFlags.Skinnable;
        Assert.Equal(SpellCastResult.TargetNotLooted, Cast());              // nobody tapped it and the 5 s head start of a tapper has not run out (IsSkinnableBy)
        boar.SkinningForOthersMs = 0;
        Assert.Equal(SpellCastResult.LowCastlevel, Cast());                 // skill 40 < (20 - 10) * 10 = 100

        await host.OnWorldAsync(() => player.Skills!.Set(SkillIds.Skinning, 120, 150, 2));   // skill >= 100: needs level * 5 = 100
        Assert.Equal(SpellCastResult.CastOk, Cast());
        Assert.Equal((ushort)121, await host.OnWorldAsync(() => player.Skills!.GetValuePure(SkillIds.Skinning)));   // red level 100, skill 120: orange, halved once by the steps = 500 per mille; the roll is the lowest (1)
    }

    private static ulong VeinGuid() => ObjectGuid.WithEntry(HighGuid.GameObject, VeinEntry, VeinSpawn).Value;

    private static Game.Spells.SpellSystem Spells(WorldTestHost host) => host.WorldServices.GetRequiredService<global::ArcaneCore.World.Spells.SpellFeature>().System;

    private static byte[] CastAtVein(uint spell)
    {
        var w = new PacketWriter(16);
        w.WriteUInt32(spell);
        new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = new ObjectGuid(VeinGuid()) }.Write(w);
        return w.ToArray();
    }

    private static WorldTestHost Start(out GameObjectTestContext context, uint veinLock)
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[0] = veinLock;
        data[1] = VeinLoot;
        var vein = new GameObjectTemplate { Entry = VeinEntry, Type = (uint)GameObjectType.Chest, DisplayId = 311, Name = "Copper Vein", Data = data };
        // Human start is (-8949.95, -132.49, 83.53): the vein is 2 yd away.
        var spawn = new GameObjectSpawn { Guid = VeinSpawn, Entry = VeinEntry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f };
        var goContent = new GameObjectContent([vein], [spawn], [Lock(VeinLock, LockTypeMining, 1), Lock(HardVeinLock, LockTypeMining, 50)], [], []);
        var lootContent = new LootContent([(LootTableKind.GameObject, new LootStoreRow(VeinLoot, CopperOre, 100f, 0, 1, 1))], []);
        context = new GameObjectTestContext(goContent, lootContent);

        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = CopperOre, Class = 7, Name = "Copper Ore", DisplayId = 4, Quality = 1, Stackable = 20 });
        GameObjectTestStore.Current.Value = context;
        try
        {
            using (items.Use())
            {
                return WorldTestHost.Start(configureServices: Configure);
            }
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }
    }

    private const uint LockTypeMining = 3;

    private static LockEntry Lock(uint id, uint lockType, uint skill)
    {
        uint[] types = new uint[LockEntry.Cases];
        uint[] indexes = new uint[LockEntry.Cases];
        uint[] skills = new uint[LockEntry.Cases];
        (types[0], indexes[0], skills[0]) = (2, lockType, skill);
        return new LockEntry(id, types, indexes, skills);
    }

    private static void Configure(IServiceCollection services)
    {
        services.AddSingleton(new SkillCatalog(
            [
                new SkillLineRecord(SkillIds.Mining, SkillCategories.Profession, "Mining", 0),
                new SkillLineRecord(SkillIds.Skinning, SkillCategories.Profession, "Skinning", 0),
                new SkillLineRecord(SkillIds.Herbalism, SkillCategories.Profession, "Herbalism", 0),
            ],
            [
                new SkillRaceClassInfoRecord(SkillIds.Mining, 0, 0, 0, 0, 21),
                new SkillRaceClassInfoRecord(SkillIds.Skinning, 0, 0, 0, 0, 21),
                new SkillRaceClassInfoRecord(SkillIds.Herbalism, 0, 0, 0, 0, 21),
            ],
            [new SkillTierRecord(21, Enumerable.Repeat(0u, 16).ToArray(), Enumerable.Repeat(75u, 16).ToArray())],
            []));
        services.AddSingleton<ISpellContentStore>(new InMemorySkillSpellContentStore(new SpellContent(
            [
                Gather(MiningCast, 3),
                Gather(HerbCast, 2),
                Slow(Gather(SlowMiningCast, 3)),
                new SpellTemplateRow
                {
                    Id = SkinCast, SpellName = "Test Skinning", RangeIndex = 12, Effect1 = 95, EffectImplicitTargetA1 = 25, Targets = 0x402,
                    EffectBaseDice1 = 1, EffectDieSides1 = 1,
                },
            ],
            [new SpellCastTimeRow { Id = 2, CastTime = 500, MinCastTime = 500 }], [], [new SpellRangeRow { Id = 1 }, new SpellRangeRow { Id = 12, MaxRange = 5 }], [],
            [
                new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = MiningCast },
                new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = HerbCast },
                new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = SkinCast },
                new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = SlowMiningCast },
            ],
            [])));
        services.AddSingleton<ICharacterSkillStore, InMemoryCharacterSkillStore>();
        services.AddSingleton<ArcaneCore.Game.Combat.ICombatRandom>(new LowestRoll());
    }

    private static SpellTemplateRow Slow(SpellTemplateRow row)
    {
        row.CastingTimeIndex = 2;
        return row;
    }

    private static SpellTemplateRow Gather(uint id, int lockType) => new()
    {
        Id = id, SpellName = $"Test Gather {lockType}", RangeIndex = 12, Targets = 0x4000,
        Effect1 = 33, EffectImplicitTargetA1 = 23, EffectMiscValue1 = lockType, EffectBasePoints1 = -1, EffectBaseDice1 = 1, EffectDieSides1 = 1,
    };
}
