using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

public sealed partial class DungeonScriptExpansionTests
{
    private const float X = -16.4f, Y = -383.07f, Z = 61.78f;

    private static (InstanceFixture Fixture, Player Player, Map Map) Enter(
        Func<Map, InstanceData> script, CreatureSpawn[] spawns, GameObjectTemplate[] goTemplates = null!, GameObjectSpawn[] goSpawns = null!,
        uint[] extraCreatureEntries = null!, LockEntry[] locks = null!)
    {
        var fixture = new InstanceFixture();
        fixture.Manager.Scripts = new InstanceScriptRegistry().Register(Dungeon, script);
        var creatures = new CreatureContent(
            [.. spawns.Select(s => s.Entry).Concat(extraCreatureEntries ?? []).Distinct().Select(e => Template(e))], spawns, [], [], [], new CreatureAiContent([], []));
        var objects = new GameObjectContent(goTemplates ?? [], goSpawns ?? [], locks ?? [], [], []);
        fixture.World.MapCreated += map =>
        {
            if (map.MapId == Dungeon)
            {
                map.AddUpdater(new CreatureMapSystem(map, creatures, random: new Random(1), aiServices: new CreatureAiServices()));
                map.AddUpdater(new GameObjectMapSystem(map, objects));
            }
        };
        Player player = fixture.AddPlayer(1);
        Assert.True(fixture.EnterDungeon(player));
        fixture.Tick();
        return (fixture, player, player.Map!);
    }

    private static CreatureSpawn At(uint guid, uint entry, float dx)
        => Spawn(guid, entry, X + dx, Y, Z, mapId: Dungeon);

    private static GameObjectSpawn ObjectAt(uint guid, uint entry, float dx)
        => GameObjectTestKit.GoSpawn(guid, entry, X + dx, Y) with { MapId = Dungeon, Z = Z };

    [Fact]
    public void Maraudon_NoxxionSpecialStopsTheSpewer_AndDeathPersistsCompletion()
    {
        using var fixture = new InstanceFixture();
        Assert.True(InstanceScriptRegistry.Default.HasScript(349));
        var script = new MaraudonInstance(fixture.World.GetMap(349, 4242));
        script.Initialize();

        Assert.Equal(60_000u, script.LarvaSpewRemainingMs);
        script.SetData(MaraudonInstance.TypeNoxxion, EncounterState.Special);
        Assert.Equal(0u, script.LarvaSpewRemainingMs);
        script.SetData(MaraudonInstance.TypeNoxxion, EncounterState.Done);
        Assert.Equal(EncounterState.Done, script.GetData(MaraudonInstance.TypeNoxxion));
        Assert.Equal("3", script.GetSaveData());
    }

    [Fact]
    public void SunkenTemple_StatuesOnlyAdvanceInScriptDevOrder_AndSixthActivatesAtalarion()
    {
        using var fixture = new InstanceFixture();
        var script = new SunkenTempleInstance(fixture.World.GetMap(109, 4242));
        script.Initialize();

        Assert.False(script.ProcessStatueEvent(3095));
        Assert.True(script.ProcessStatueEvent(3094));
        Assert.False(script.ProcessStatueEvent(3094));
        foreach (uint eventId in new uint[] { 3095, 3097, 3098, 3099, 3100 })
        {
            Assert.True(script.ProcessStatueEvent(eventId));
        }

        Assert.Equal(EncounterState.Special, script.GetData(SunkenTempleInstance.TypeAtalarion));
        Assert.Equal(6, script.StatuesActivated);
    }

    [Fact]
    public void SunkenTemple_TheFlamesNeedTheSummonedShade()
    {
        using var fixture = new InstanceFixture();
        var script = new SunkenTempleInstance(fixture.World.GetMap(109, 4242));
        script.Initialize();
        script.SetData(SunkenTempleInstance.TypeAvatar, EncounterState.InProgress);
        Assert.Equal(3_000u, script.AvatarWaveRemainingMs);

        for (int i = 0; i < 4; i++)
        {
            script.SetData(SunkenTempleInstance.TypeAvatar, EncounterState.Special);
        }

        Assert.Equal(0, script.FlamesDoused);
        Assert.Equal(3_000u, script.AvatarWaveRemainingMs);
    }

    [Fact]
    public void BlackrockDepths_RibblysDeathCallsTheBarPatrol_WhoseFireguardYellsTwiceAndCompletesTheBar()
    {
        var (fixture, player, map) = Enter(m => new BlackrockDepthsInstance(m),
            [At(1, BlackrockDepthsInstance.NpcPlugger, 3), At(2, BlackrockDepthsInstance.NpcRibbly, 4)],
            extraCreatureEntries: [BlackrockDepthsInstance.NpcFireguardDestroyer, BlackrockDepthsInstance.NpcAnvilrageOfficer]);
        using (fixture)
        {
            CreatureMapSystem creatures = map.FindUpdater<CreatureMapSystem>()!;
            var script = (BlackrockDepthsInstance)InstanceManager.InstanceDataOf(map)!;
            map.Combat.Kill(player, creatures.Creatures.Single(c => c.Template.Entry == BlackrockDepthsInstance.NpcRibbly));

            // instance_blackrock_depths OnCreatureDeath(NPC_RIBBLY) -> SetData(TYPE_BAR, IN_PROGRESS) -> HandleBarPatrol(0).
            Assert.Equal(EncounterState.InProgress, script.GetData(BlackrockDepthsInstance.TypeBar));
            Assert.True(script.BarDoorOpen);
            Assert.Single(creatures.Creatures, c => c.Template.Entry == BlackrockDepthsInstance.NpcFireguardDestroyer);
            Assert.Equal(2, creatures.Creatures.Count(c => c.Template.Entry == BlackrockDepthsInstance.NpcAnvilrageOfficer));

            script.Update(4000); // HandleBarPatrol(1): YELL_PATROL_1, SPECIAL
            Assert.Equal(EncounterState.Special, script.GetData(BlackrockDepthsInstance.TypeBar));
            script.Update(2000); // HandleBarPatrol(2): YELL_PATROL_2, DONE
            Assert.Equal(EncounterState.Done, script.GetData(BlackrockDepthsInstance.TypeBar));
        }
    }

    [Fact]
    public void Maraudon_KillingNoxxionThroughCombat_StopsTheSpewerAndSavesDone()
    {
        var (fixture, player, map) = Enter(m => new MaraudonInstance(m), [At(1, MaraudonInstance.NpcNoxxion, 3)]);
        using (fixture)
        {
            Creature noxxion = map.FindUpdater<CreatureMapSystem>()!.Creatures.Single();
            map.Combat.Kill(player, noxxion);
            var script = (MaraudonInstance)InstanceManager.InstanceDataOf(map)!;
            Assert.Equal(EncounterState.Done, script.GetData(MaraudonInstance.TypeNoxxion));
            Assert.Equal(0u, script.LarvaSpewRemainingMs);
            Assert.Equal("3", fixture.Manager.FindSave(map.InstanceId)!.Data);
        }
    }

    [Fact]
    public void SunkenTemple_StatueGooberUsesItsClassicDbEventId_ThenProtectorDeathOpensBarrier()
    {
        uint[] entries = [148830, 148831, 148832, 148833, 148834, 148835];
        uint[] eventIds = [3094, 3095, 3097, 3098, 3099, 3100];
        GameObjectTemplate[] templates =
        [
            .. entries.Select((entry, i) => GameObjectTestKit.GoTemplate(entry, GameObjectType.Goober, (2, eventIds[i]))),
            GameObjectTestKit.GoTemplate(SunkenTempleInstance.GoJammalanBarrier, GameObjectType.Door),
        ];
        GameObjectSpawn[] objects =
        [
            .. entries.Select((entry, i) => ObjectAt(200 + (uint)i, entry, 2)),
            ObjectAt(300, SunkenTempleInstance.GoJammalanBarrier, 3),
        ];
        var (fixture, player, map) = Enter(m => new SunkenTempleInstance(m), [At(1, 5712, 3)], templates, objects);
        using (fixture)
        {
            GameObjectMapSystem gos = map.FindUpdater<GameObjectMapSystem>()!;
            var script = (SunkenTempleInstance)InstanceManager.InstanceDataOf(map)!;
            for (int i = 0; i < entries.Length; i++)
            {
                GameObject statue = gos.GameObjects.Single(g => g.Entry == entries[i]);
                Assert.Equal(GameObjectUseResult.Ok, gos.Use(player, statue.Guid));
            }
            Assert.Equal(EncounterState.Special, script.GetData(SunkenTempleInstance.TypeAtalarion));
            map.Combat.Kill(player, map.FindUpdater<CreatureMapSystem>()!.Creatures.Single(c => c.Template.Entry == 5712));
            Assert.Equal(EncounterState.Done, script.GetData(SunkenTempleInstance.TypeProtectors));
            Assert.Equal(GameObjectState.Active, gos.GameObjects.Single(g => g.Entry == SunkenTempleInstance.GoJammalanBarrier).State);
        }
    }

    [Fact]
    public void BlackrockDepths_ThirdMugUseWithPluggerPresent_TriggersHisHostileState()
    {
        const uint mug = 165738;
        var (fixture, player, map) = Enter(m => new BlackrockDepthsInstance(m), [At(1, BlackrockDepthsInstance.NpcPlugger, 3)],
            [GameObjectTestKit.GoTemplate(mug, GameObjectType.Goober)],
            [ObjectAt(200, mug, 2), ObjectAt(201, mug, 2), ObjectAt(202, mug, 2)]);
        using (fixture)
        {
            GameObjectMapSystem gos = map.FindUpdater<GameObjectMapSystem>()!;
            foreach (GameObject go in gos.GameObjects.Where(g => g.Entry == mug))
                Assert.Equal(GameObjectUseResult.Ok, gos.Use(player, go.Guid));
            var script = (BlackrockDepthsInstance)InstanceManager.InstanceDataOf(map)!;
            Assert.Equal(3, script.StolenAles);
            Assert.Equal(EncounterState.InProgress, script.GetData(BlackrockDepthsInstance.TypePlugger));
        }
    }

    [Fact]
    public void SunkenTemple_AvatarRitualSummonsTheFirstWave_ThenTransformsAndPersistsTheBossKill()
    {
        var (fixture, player, map) = Enter(m => new SunkenTempleInstance(m), [],
            [GameObjectTestKit.GoTemplate(SunkenTempleInstance.GoEvilCircle, GameObjectType.Generic)],
            [ObjectAt(900, SunkenTempleInstance.GoEvilCircle, 2)],
            [SunkenTempleInstance.NpcShadeOfHakkar, SunkenTempleInstance.NpcAvatarOfHakkar,
             SunkenTempleInstance.NpcHakkariMinion, SunkenTempleInstance.NpcBloodkeeper]);
        using (fixture)
        {
            var script = (SunkenTempleInstance)InstanceManager.InstanceDataOf(map)!;
            script.BeginAvatarEvent();
            Assert.Equal(EncounterState.InProgress, script.GetData(SunkenTempleInstance.TypeAvatar));
            script.Update(3000);
            CreatureMapSystem creatures = map.FindUpdater<CreatureMapSystem>()!;
            Assert.Contains(creatures.Creatures, c => c.Template.Entry == SunkenTempleInstance.NpcHakkariMinion);
            Assert.Contains(creatures.Creatures, c => c.Template.Entry == SunkenTempleInstance.NpcBloodkeeper);
            GameObject circle = map.FindUpdater<GameObjectMapSystem>()!.GameObjects.Single(g => g.Entry == SunkenTempleInstance.GoEvilCircle);
            Assert.True(circle.IsSpawned);
            for (int i = 0; i < 4; i++) script.SetData(SunkenTempleInstance.TypeAvatar, EncounterState.Special);
            Assert.Equal(4, script.FlamesDoused);
            // HakkarSummoned (spell 12948, sunken_templeScripts.cpp:251-268): the evil circles go when the Avatar arrives.
            fixture.Tick();
            Assert.False(circle.IsSpawned);
            Creature avatar = Assert.Single(creatures.Creatures, c => c.Template.Entry == SunkenTempleInstance.NpcAvatarOfHakkar);
            map.Combat.Kill(player, avatar);
            Assert.Equal(EncounterState.Done, script.GetData(SunkenTempleInstance.TypeAvatar));
            Assert.Equal("0 0 0 0 3", fixture.Manager.FindSave(map.InstanceId)!.Data);
        }
    }

    [Fact]
    public void BlackrockDepths_TwelfthCofferStartsTheVault_AndThelrinSpellIsRegistered()
    {
        using var fixture = new InstanceFixture();
        var script = new BlackrockDepthsInstance(fixture.World.GetMap(230, 4242));
        script.Initialize();
        for (int i = 0; i < 11; i++) script.SetData(BlackrockDepthsInstance.TypeVault, EncounterState.Special);
        Assert.Equal(EncounterState.NotStarted, script.GetData(BlackrockDepthsInstance.TypeVault));
        script.SetData(BlackrockDepthsInstance.TypeVault, EncounterState.Special);
        Assert.Equal(12, script.CofferDoorsOpened);
        Assert.Equal(EncounterState.InProgress, script.GetData(BlackrockDepthsInstance.TypeVault));
        Assert.NotNull(SpellScriptRegistry.Discover(typeof(BlackrockDepthsInstance).Assembly).Find(27517));
    }

    [Fact]
    public void BlackrockDepths_RingStartsOnce_WhenTheTriggerReachesTheInstance()
    {
        var (fixture, player, map) = Enter(m => new BlackrockDepthsInstance(m), []);
        using (fixture)
        {
            var script = (BlackrockDepthsInstance)InstanceManager.InstanceDataOf(map)!;
            Assert.True(script.EnterRingOfLaw(player, player.X, player.Y, player.Z));
            Assert.False(script.EnterRingOfLaw(player, player.X, player.Y, player.Z));
            Assert.Equal(EncounterState.InProgress, script.GetData(BlackrockDepthsInstance.TypeRingOfLaw));
        }
    }

    [Fact]
    public void BlackrockDepths_DagranDeathSparedMoiraLeavesHerNeutral()
    {
        var (fixture, player, map) = Enter(m => new BlackrockDepthsInstance(m),
            [At(1, BlackrockDepthsInstance.NpcEmperor, 3), At(2, BlackrockDepthsInstance.NpcPrincess, 4)]);
        using (fixture)
        {
            CreatureMapSystem creatures = map.FindUpdater<CreatureMapSystem>()!;
            Creature emperor = creatures.Creatures.Single(c => c.Template.Entry == BlackrockDepthsInstance.NpcEmperor);
            Creature princess = creatures.Creatures.Single(c => c.Template.Entry == BlackrockDepthsInstance.NpcPrincess);
            Assert.IsType<EmperorDagranAI>(emperor.AI);
            Assert.IsType<MoiraBronzebeardAI>(princess.AI);

            map.Combat.Kill(player, emperor);

            Assert.True(princess.IsAlive);
            Assert.Equal(734u, princess.FactionTemplate);
        }
    }

    [Fact]
    public void BlackrockDepths_FlamelashAggroActivatesTheSevenRunes()
    {
        GameObjectTemplate[] runes = [.. Enumerable.Range(0, 7)
            .Select(i => GameObjectTestKit.GoTemplate(BlackrockDepthsInstance.GoFirstRune + (uint)i, GameObjectType.Door))];
        GameObjectSpawn[] spawns = [.. Enumerable.Range(0, 7)
            .Select(i => ObjectAt(400 + (uint)i, BlackrockDepthsInstance.GoFirstRune + (uint)i, 3))];
        var (fixture, player, map) = Enter(m => new BlackrockDepthsInstance(m),
            [At(1, BlackrockDepthsInstance.NpcAmbassadorFlamelash, 3)], runes, spawns);
        using (fixture)
        {
            Creature boss = map.FindUpdater<CreatureMapSystem>()!.Creatures.Single();
            Assert.IsType<AmbassadorFlamelashAI>(boss.AI);
            boss.AI!.OnAggro(player);
            Assert.All(map.FindUpdater<GameObjectMapSystem>()!.GameObjects, rune => Assert.Equal(GameObjectState.Active, rune.State));
        }
    }
}
