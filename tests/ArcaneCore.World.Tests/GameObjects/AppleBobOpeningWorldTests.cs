using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Skills;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// The Hallow's End "Apple Bob" (gameobject 180523): a type 3 chest, lock 93 (Lock.dbc 5875: one skill case of lock type 12, no required
/// skill), loot 17378 holding item 20516 x5 at 100%. The 5875 client opens it with CMSG_CAST_SPELL of spell 6477 "Opening" (OPEN_LOCK,
/// misc 12, target mask GAMEOBJECT|LOCKED) and target mask 0x0800 at the object. The report: the cast succeeds and no loot window opens.
/// These tests drive the cast through the real packet path with synthetic content so a server-side loss of the open shows up here.
/// The spell row follows the imported spell 6477 (Attributes 65936, cast time index 6, range index 12, base points 99, Targets 0x4000).
/// </summary>
public sealed class AppleBobOpeningWorldTests
{
    internal const uint Opening = 6477;
    internal const uint BobEntry = 180523;
    internal const uint BobItem = 20516;
    internal const uint BobCount = 5;
    private const uint BobSpawn = 77003;
    private const uint LootId = 17378;
    private const uint LockId = 93;

    private static ulong BobGuid() => ObjectGuid.WithEntry(HighGuid.GameObject, BobEntry, BobSpawn).Value;

    private static WorldTestHost Start(out GameObjectTestContext context)
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[0] = LockId;
        data[1] = LootId;
        var chest = new GameObjectTemplate { Entry = BobEntry, Type = (uint)GameObjectType.Chest, DisplayId = 6405, Name = "Apple Bob", Flags = 0, Data = data };
        var spawn = new GameObjectSpawn { Guid = BobSpawn, Entry = BobEntry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f };
        // Lock.dbc 93 as imported: case 2 is a skill case of lock type 12 (OPEN_TINKERING), required skill 0.
        var lockEntry = new LockEntry(LockId, [0, 2, 0, 0, 0, 0, 0, 0], [0, 12, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0]);
        var goContent = new GameObjectContent([chest], [spawn], [lockEntry], [], []);
        var lootContent = new LootContent([(LootTableKind.GameObject, new LootStoreRow(LootId, BobItem, 100f, 0, 5, 5))], []);
        context = new GameObjectTestContext(goContent, lootContent);

        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = BobItem, Class = 0, Name = "Bag of Apples", DisplayId = 4003, Quality = 1, Stackable = 20 });
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
    {
        GatheringWorldTests.Configure(services);
        services.AddSingleton<ISpellContentStore>(new InMemorySkillSpellContentStore(new SpellContent(
            [
                new SpellTemplateRow
                {
                    Id = Opening, SpellName = "Opening", RangeIndex = 12, Targets = 0x4000, CastingTimeIndex = 6, Attributes = 65936,
                    Effect1 = 33, EffectImplicitTargetA1 = 23, EffectMiscValue1 = 12, EffectBasePoints1 = 99, EffectBaseDice1 = 1, EffectDieSides1 = 1,
                },
            ],
            [new SpellCastTimeRow { Id = 6, CastTime = 5000, MinCastTime = 5000 }], [], [new SpellRangeRow { Id = 1 }, new SpellRangeRow { Id = 12, MaxRange = 5 }], [],
            [new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = Opening }],
            [])));
    }

    /// <summary>Reads an SMSG_LOOT_RESPONSE (LootPackets.LootResponse): the source guid, then every (item id, count) in the window.</summary>
    internal static (ulong Source, List<(uint ItemId, uint Count)> Items) ReadLootResponse(byte[] payload)
    {
        var loot = new PacketReader(payload);
        ulong source = loot.ReadUInt64();
        loot.ReadByte();                                // loot type
        loot.ReadUInt32();                              // gold
        byte count = loot.ReadByte();
        var items = new List<(uint, uint)>();
        for (int i = 0; i < count; i++)
        {
            loot.ReadByte();                            // slot
            uint id = loot.ReadUInt32();
            uint stack = loot.ReadUInt32();
            loot.ReadUInt32();                          // display id
            loot.ReadUInt32();                          // random suffix
            loot.ReadUInt32();                          // random property
            loot.ReadByte();                            // slot type
            items.Add((id, stack));
        }

        return (source, items);
    }

    internal static byte[] CastAt(uint spell, ulong target)
    {
        var cast = new PacketWriter(16);
        cast.WriteUInt32(spell);
        new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = new ObjectGuid(target) }.Write(cast);
        return cast.ToArray();
    }

    [Fact]
    public async Task CastingOpeningOnAppleBob_ShowsTheLootWindowWithFiveApples()
    {
        await using WorldTestHost host = Start(out GameObjectTestContext context);
        await using WorldTestClient client = await host.EnterWorldAsync("BOBBER", "Bobber");
        ulong bob = BobGuid();
        await GameObjectWorldTests.ReadUntilGameObjectCreateAsync(client, bob);
        (GameObjectState state, uint flags) = await host.OnWorldAsync(() =>
        {
            GameObject go = context.Feature!.FindSystem(0)!.Find(new ObjectGuid(bob))!;
            return (go.State, (uint)go.Flags);
        });
        Assert.Equal(GameObjectState.Ready, state);
        Assert.Equal(0u, flags & 0x2);

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastAt(Opening, bob));
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellStart);
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        (ulong source, List<(uint ItemId, uint Count)> items) = ReadLootResponse(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        Assert.Equal(bob, source);
        Assert.Equal((BobItem, BobCount), Assert.Single(items));
    }

    /// <summary>An object script that takes over every validated opening and shows nothing, as a broken or misplaced script would.</summary>
    private sealed class OpenSwallowingAi : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, ArcaneCore.Game.Entities.Unit target) => false;

        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
        {
        }

        public bool OnUnlockedUse(GameObjectMapSystem objects, GameObject go, ArcaneCore.Game.Entities.Player user) => ++Calls > 0;

        public int Calls { get; private set; }
    }

    /// <summary>
    /// A script that takes the open is legitimate: <c>gameobject/open-no-effect</c> must stay quiet. The script counter proves the cast really reached
    /// the script, so an empty capture cannot come from a cast that never got that far.
    /// </summary>
    [Fact]
    public async Task OpeningAppleBobWhileAScriptTakesTheOpen_DoesNotReportOpenNoEffect()
    {
        await using WorldTestHost host = Start(out GameObjectTestContext context);
        await using WorldTestClient client = await host.EnterWorldAsync("BOBSWAL", "Bobswal");
        ulong bob = BobGuid();
        await GameObjectWorldTests.ReadUntilGameObjectCreateAsync(client, bob);
        var script = new OpenSwallowingAi();
        await host.OnWorldAsync(() => context.Feature!.FindSystem(0)!.RegisterAi(BobEntry, script));
        using AnomalyCapture cap = AnomalyLedger.Shared.Capture(n => n.Key == "gameobject/open-no-effect" && n.Subject == $"gameobject entry {BobEntry} spell {Opening}");

        await client.SendAsync(WorldOpcode.CmsgCastSpell, CastAt(Opening, bob));
        await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
        await host.OnWorldAsync(() => 0); // the cast's effects run on the world thread; this call is queued behind them

        Assert.Equal(1, script.Calls);
        Assert.Empty(cap.Notices);
    }

    /// <summary>The control: the same chest through CMSG_GAMEOBJ_USE (what the client sends for objects it does not open by a spell).</summary>
    [Fact]
    public async Task UsingAppleBobDirectly_ShowsTheLootWindowWithFiveApples()
    {
        await using WorldTestHost host = Start(out _);
        await using WorldTestClient client = await host.EnterWorldAsync("BOBUSE", "Bobuse");
        ulong bob = BobGuid();
        await GameObjectWorldTests.ReadUntilGameObjectCreateAsync(client, bob);

        await client.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(bob));
        (ulong source, List<(uint ItemId, uint Count)> items) = ReadLootResponse(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        Assert.Equal(bob, source);
        Assert.Equal((BobItem, BobCount), Assert.Single(items));
    }
}
