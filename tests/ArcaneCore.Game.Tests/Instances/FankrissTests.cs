using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class FankrissTests
{
    private sealed class Arena : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public Map Map { get; }
        public TempleOfAhnQirajInstance Raid { get; }
        public CreatureMapSystem Creatures { get; }
        public FakeCaster Casts { get; } = new();
        public Player Tank { get; }
        public Player Other { get; }
        public Creature Boss { get; }
        public FankrissAI Ai => Assert.IsType<FankrissAI>(Boss.AI);

        public Arena()
        {
            WorldMaps.Of(World).Load(new MapContent(
                [new MapTemplate(531, 0, MapType.Raid, 0, 40, 0, -1, 0, 0, "Temple of Ahn'Qiraj", "")], [], [], [], []));
            Map = World.GetMap(531);
            Raid = new TempleOfAhnQirajInstance(Map);
            Map.AddUpdater(Raid);
            uint[] entries = [15510, 15630, 15962];
            var content = new CreatureContent(entries.Select(e => Template(e, b => b.MinLevelHealth = b.MaxLevelHealth = 10000)),
                [Spawn(1, 15510, -8075, 1193, -92, 531)], [], [], []);
            Creatures = new CreatureMapSystem(Map, content, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = Casts, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, -8072, 1193, new FakeSession(), 531);
            Tank.Relocate(-8072, 1193, -92, 0, 0);
            Other = TestWorld.CreatePlayer(2, -8069, 1193, new FakeSession(), 531);
            Other.Relocate(-8069, 1193, -92, 0, 0);
            World.AddPlayer(Tank);
            World.AddPlayer(Other);
            World.RunTick(0);
            Boss = Assert.Single(Creatures.Creatures, c => c.Entry == 15510);
        }

        public void Pull()
        {
            Assert.True(Ai.AttackStart(Tank));
            Boss.Combat.Threat.AddThreat(Other, 50);
            Casts.Casts.Clear();
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void IdlePullPointStartsTheEncounterAndDeathSavesIt()
    {
        using var a = new Arena();
        a.Ai.OnUpdate(1);
        Assert.Equal(EncounterState.InProgress, a.Raid.GetData(TempleOfAhnQirajInstance.Fankriss));
        a.Map.Combat.Kill(a.Tank, a.Boss);
        Assert.Equal(EncounterState.Done, a.Raid.GetData(TempleOfAhnQirajInstance.Fankriss));
        var loaded = new TempleOfAhnQirajInstance(a.Map);
        loaded.Load(a.Raid.GetSaveData()!);
        Assert.Equal(EncounterState.Done, loaded.GetData(TempleOfAhnQirajInstance.Fankriss));
    }

    [Fact]
    public void WebsSpawnHatchlingsAtAllThreeAlcovesAndWormsEnrage()
    {
        using var a = new Arena();
        a.Pull();
        a.Ai.OnUpdate(8000);
        Assert.Contains(a.Casts.Casts, c => c.Spell == 25646);
        a.Ai.OnUpdate(40_000);
        Assert.Contains(a.Casts.Casts, c => c.Spell is 720 or 731 or 1121);
        Creature[] hatchlings = [.. a.Creatures.Creatures.Where(c => c.Entry == 15962)];
        Assert.InRange(hatchlings.Length, 6, 20);
        Assert.Contains(hatchlings, c => c.X == -8043.01f);
        Assert.Contains(hatchlings, c => c.X == -8003.00f);
        Assert.Contains(hatchlings, c => c.X == -8022.68f);
        Creature worm = Assert.Single(a.Creatures.Creatures, c => c.Entry == 15630);
        Assert.IsType<SpawnOfFankrissAI>(worm.AI).OnUpdate(26_000);
        Assert.Contains(a.Casts.Casts, c => c.Spell == 26662 && ReferenceEquals(c.Target, worm));

        var hatchlingAi = Assert.IsType<FankrissHatchlingAI>(hatchlings[0].AI);
        Assert.False(hatchlingAi.AttackStart(a.Tank));
        hatchlingAi.OnUpdate(2500);
        Assert.NotNull(hatchlings[0].Combat.Victim);
        a.Ai.OnUpdate(60_000);
        Assert.True(a.Creatures.Creatures.Count(c => c.Entry == 15630) >= 2);
        Assert.InRange(a.Creatures.Creatures.Count(c => c.Entry == 15962), 6, 20);
    }

    [Fact]
    public void EvadeFailsAndResetsTheEncounter()
    {
        using var a = new Arena();
        a.Pull();
        a.Ai.OnUpdate(40_000);
        a.Ai.OnEvade();
        a.Ai.OnReachedHome();
        Assert.Equal(EncounterState.Fail, a.Raid.GetData(TempleOfAhnQirajInstance.Fankriss));
    }

    [Fact]
    public void WebSpellThroughTheCreatureCasterTeleportsToItsDatabaseAlcove()
    {
        var web = Spell(720,
            Effect(SpellEffectName.TeleportUnits, 0, SpellImplicitTarget.UnitEnemy,
                targetB: SpellImplicitTarget.LocationDatabase),
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModStun)) with
        {
            RangeIndex = 4, Range = new SpellRange(0, 100), Duration = new SpellDuration(10_000, 0, 10_000),
        };
        using var kit = new SpellTestKit(web);
        kit.System.Store = new SpellStore(kit.Store.All, [],
            [(720, new SpellTargetPosition(531, -8043.6f, 1254.1f, -84.3f, 0))]);
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        Map map = kit.World.GetMap(531);
        var raid = new TempleOfAhnQirajInstance(map);
        map.AddUpdater(raid);
        var content = Content([Template(15510)], []);
        var creatures = new CreatureMapSystem(map, content, random: new Random(1),
            aiServices: new CreatureAiServices { Spells = new SpellSystemCreatureCaster(kit.System), Hostility = new AlwaysHostile() });
        map.AddUpdater(creatures);
        Player player = TestWorld.CreatePlayer(1, -8075, 1193, new FakeSession(), 531);
        player.Relocate(-8075, 1193, -92, 0, kit.Now);
        kit.World.AddPlayer(player);
        relations.Hostile.Add(player.Guid);
        kit.World.RunTick(0);
        Creature boss = creatures.SpawnTemporary(content.FindTemplate(15510)!, -8075, 1193, -92, 0);

        Assert.Equal(CreatureCastResult.Ok, creatures.CastSpell(boss, 720, player, triggered: false));
        Assert.Equal(-8043.6f, player.X);
        Assert.Equal(1254.1f, player.Y);
        Assert.True(kit.System.HasAura(player, 720));
    }
}
