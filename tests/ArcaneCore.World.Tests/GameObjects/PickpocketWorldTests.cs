using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// Pick Pocket end to end over the loopback daemon: the cast on a creature with pocket loot opens the picker's window as loot type 2 for the
/// creature's guid; a creature without a pocket loot id answers TARGET_NO_POCKETS; the money is the level-based pickpocket money and the items
/// go into the bags.
/// </summary>
public sealed class PickpocketWorldTests
{
    private const uint PickPocket = 9400;
    private const uint ThiefEntry = 4101;
    private const uint BareEntry = 4102;
    private const uint PocketLoot = 4101;
    private const uint Coin = 6948;

    [Fact]
    public async Task APickPocketCast_OpensTheTypeTwoWindow_WithTheMoney_AndTheItemsGoIntoTheBags()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("PICKER", "Picker");
        Player player = await host.PlayerAsync("Picker");
        Creature thief = await AddCreatureAsync(host, player, ThiefEntry, 4101);

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastAt(thief));

        var loot = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        Assert.Equal(thief.Guid.Value, loot.ReadUInt64());
        Assert.Equal((byte)2, loot.ReadByte());           // LOOT_PICKPOCKETING
        uint gold = loot.ReadUInt32();
        Assert.True(gold % 10 == 0 && gold <= 10 * ((thief.Level / 2) + (player.Level / 2)), $"pickpocket money {gold}");
        Assert.Equal((byte)1, loot.ReadByte());
        Assert.Equal((byte)0, loot.ReadByte());           // slot
        Assert.Equal(Coin, loot.ReadUInt32());

        await client.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [0]);
        await client.ReadUntilAsync(WorldOpcode.SmsgLootRemoved);
        Assert.Equal(1u, await host.OnWorldAsync(() => player.Inventory.GetItemCount(Coin)));
        await client.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(thief.Guid.Value));
        await client.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse);
    }

    [Fact]
    public async Task ACreatureWithoutPockets_AnswersTargetNoPockets()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("NOPOCKET", "Nopocket");
        Player player = await host.PlayerAsync("Nopocket");
        Creature bare = await AddCreatureAsync(host, player, BareEntry, 4102);

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastAt(bare));

        byte[] result = await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);
        Assert.Equal((PickPocket, (byte)SpellCastResultStatus.Failure, (byte)SpellCastResult.TargetNoPockets), (BitConverter.ToUInt32(result, 0), result[4], result[5]));
    }

    private static byte[] CastAt(Creature target)
    {
        var w = new PacketWriter(16);
        w.WriteUInt32(PickPocket);
        SpellCastTargets.ForUnit(target.Guid).Write(w);
        return w.ToArray();
    }

    private static async Task<Creature> AddCreatureAsync(WorldTestHost host, Player player, uint entry, uint guid)
    {
        var template = new CreatureTemplate { Entry = entry, Name = "Test Thief", MinLevel = 20, MaxLevel = 20, MinLevelHealth = 60, MaxLevelHealth = 60, Faction = 32 };
        var spawn = new CreatureSpawn { Guid = guid, Entry = entry, MapId = 0, X = player.X + 1, Y = player.Y, Z = player.Z };
        var creature = new Creature(guid, template, spawn, new CreatureContent([template], [spawn], [], [], []), new Random(1));
        await host.OnWorldAsync(() => host.World.GetMap(0).AddObject(creature));
        return creature;
    }

    private static WorldTestHost Start()
    {
        var goContent = new GameObjectContent([], [], [], [], []);
        var lootContent = new LootContent(
            [(LootTableKind.Pickpocketing, new LootStoreRow(PocketLoot, Coin, 100f, 0, 1, 1))], [],
            [], [new KeyValuePair<uint, uint>(ThiefEntry, PocketLoot)]);
        var context = new GameObjectTestContext(goContent, lootContent);

        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = Coin, Class = 15, Name = "Pocket Item", DisplayId = 8, Quality = 1, Stackable = 20 });
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

    private static void Configure(IServiceCollection services)
        => services.AddSingleton<ISpellContentStore>(new Skills.InMemorySkillSpellContentStore(new SpellContent(
            [
                new SpellTemplateRow
                {
                    Id = PickPocket, SpellName = "Test Pick Pocket", RangeIndex = 12, Targets = 0x2, Effect1 = 71, EffectImplicitTargetA1 = 6,
                },
            ],
            [], [], [new SpellRangeRow { Id = 1 }, new SpellRangeRow { Id = 12, MaxRange = 5 }], [],
            [new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = PickPocket }],
            [])));
}