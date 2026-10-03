using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Fishing;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Skills;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Player = ArcaneCore.Game.Entities.Player;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// Fishing end to end over the loopback daemon: the fishing spell (TRANS_DOOR, target 39) summons an owned bobber on water, a land cast answers
/// NOT_FISHABLE, the owner's click on a ready bobber opens personal fishing loot (wire type 3), the catch is taken into the bags and releasing the
/// window removes the bobber. Water is a stub terrain (the daemon has no ADT data in tests); the zone is the unknown area 0, whose base skill
/// and fishing table the test defines.
/// </summary>
public sealed class FishingWorldTests
{
    private const uint FishingCast = 9300;
    private const uint BobberEntry = 35591;
    private const uint RawSmallfish = 6291;

    private sealed class StubWater(bool water) : IFishingTerrain
    {
        public bool IsSwimmable(Map map, float x, float y, float z, float radius, out LiquidData data)
        {
            data = water ? new LiquidData(1, LiquidTypeFlags.Water, z + 1.0f, z - 40.0f) : default;
            return water;
        }
    }

    private static byte[] CastSelf(uint spell)
    {
        var w = new PacketWriter(6);
        w.WriteUInt32(spell);
        w.WriteUInt16(0);
        return w.ToArray();
    }

    private static GameObject? Bobber(WorldTestHost host, GameObjectTestContext context)
        => context.Feature!.FindSystem(0)!.GameObjects.SingleOrDefault(g => g.Type == GameObjectType.FishingNode);

    [Fact]
    public async Task ACastOnWater_SummonsTheBobber_TheCatchOpensType3Loot_AndReleasingRemovesTheBobber()
    {
        await using WorldTestHost host = Start(out GameObjectTestContext context, water: true);
        await using WorldTestClient client = await host.EnterWorldAsync("FISHER", "Fisher");
        Player player = await host.PlayerAsync("Fisher");
        await host.OnWorldAsync(() => player.Skills!.Set(SkillIds.Fishing, 150, 300, 1));

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastSelf(FishingCast));
        await client.ReadUntilAsync(WorldOpcode.MsgChannelStart);
        GameObject bobber = (await host.OnWorldAsync(() => Bobber(host, context)))!;
        Assert.Equal(player.Guid, bobber.OwnerGuid);
        Assert.Equal(bobber.Guid.Value, await host.OnWorldAsync(() => player.GetUInt64(UpdateFields.UnitFieldChannelObject)));

        // The 3 s channel is shorter than every bite time, so the bobber is ready at once (readyAt = max(0, duration - lastSec)).
        await host.WaitForWorldAsync(() => bobber.LootState == GameObjectLootState.Ready, "the bobber bites");
        await client.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(bobber.Guid.Value));
        var loot = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        Assert.Equal(bobber.Guid.Value, loot.ReadUInt64());
        Assert.Equal((byte)3, loot.ReadByte());      // LOOT_FISHING
        Assert.Equal(0u, loot.ReadUInt32());
        Assert.Equal((byte)1, loot.ReadByte());
        Assert.Equal((byte)0, loot.ReadByte());      // slot
        Assert.Equal(RawSmallfish, loot.ReadUInt32());

        await host.WaitForWorldAsync(() => player.GetUInt32(UpdateFields.UnitChannelSpell) == 0, "the channel ends with the click");

        await client.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [0]);
        await client.ReadUntilAsync(WorldOpcode.SmsgLootRemoved);
        Assert.Equal(1u, await host.OnWorldAsync(() => player.Inventory.GetItemCount(RawSmallfish)));

        await client.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(bobber.Guid.Value));
        await client.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse);
        await host.WaitForWorldAsync(() => Bobber(host, context) is null, "the bobber is removed");
    }

    [Fact]
    public async Task ACastOnLand_AnswersNotFishable_AndLeavesNoBobber()
    {
        await using WorldTestHost host = Start(out GameObjectTestContext context, water: false);
        await using WorldTestClient client = await host.EnterWorldAsync("LANDER", "Lander");

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastSelf(FishingCast));

        byte[] ok = await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);
        Assert.Equal(((uint)FishingCast, (byte)SpellCastResultStatus.Success), (BitConverter.ToUInt32(ok, 0), ok[4])); // the cast itself succeeded: vmangos refuses inside the effect
        byte[] result = await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);
        Assert.Equal((FishingCast, (byte)SpellCastResultStatus.Failure, (byte)SpellCastResult.NotFishable), (BitConverter.ToUInt32(result, 0), result[4], result[5]));
        Assert.Null(await host.OnWorldAsync(() => Bobber(host, context)));
    }

    private static WorldTestHost Start(out GameObjectTestContext context, bool water)
    {
        var bobber = new GameObjectTemplate { Entry = BobberEntry, Type = (uint)GameObjectType.FishingNode, DisplayId = 668, Name = "Fishing Bobber", Data = new uint[GameObjectTemplate.DataCount] };
        var goContent = new GameObjectContent([bobber], [], [], [], []);
        // Area 0 is both the zone and the sub-zone of the test map: its base skill 55 and its fishing table.
        var lootContent = new LootContent(
            [(LootTableKind.Fishing, new LootStoreRow(0, RawSmallfish, 100f, 0, 1, 1))], [],
            [new KeyValuePair<uint, int>(0, 55)], []);
        context = new GameObjectTestContext(goContent, lootContent);

        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = RawSmallfish, Class = 7, Name = "Raw Smallfish", DisplayId = 6, Quality = 1, Stackable = 20 });
        GameObjectTestStore.Current.Value = context;
        try
        {
            using (items.Use())
            {
                return WorldTestHost.Start(configureServices: services => Configure(services, water));
            }
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }
    }

    private static void Configure(IServiceCollection services, bool water)
    {
        services.AddSingleton<IFishingTerrain>(new StubWater(water));
        services.AddSingleton(new SkillCatalog(
            [new SkillLineRecord(SkillIds.Fishing, SkillCategories.Secondary, "Fishing", 0)],
            [new SkillRaceClassInfoRecord(SkillIds.Fishing, 0, 0, 0, 0, 21)],
            [new SkillTierRecord(21, Enumerable.Repeat(0u, 16).ToArray(), Enumerable.Repeat(75u, 16).ToArray())],
            []));
        services.AddSingleton<ISpellContentStore>(new Skills.InMemorySkillSpellContentStore(new SpellContent(
            [
                new SpellTemplateRow
                {
                    Id = FishingCast, SpellName = "Test Fishing", RangeIndex = 1, AttributesEx = 0x4, DurationIndex = 3, ChannelInterruptFlags = 0x8,
                    Effect1 = 50, EffectImplicitTargetA1 = 39, EffectMiscValue1 = (int)BobberEntry, EffectRadiusIndex1 = 7,
                },
            ],
            [], [new SpellDurationRow { Id = 3, Duration = 3000, MaxDuration = 3000 }], [new SpellRangeRow { Id = 1 }],
            [new SpellRadiusRow { Id = 7, Radius = 15.0f }],
            [new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = FishingCast }],
            [])));
        services.AddSingleton<ICharacterSkillStore, Skills.InMemoryCharacterSkillStore>();
    }
}