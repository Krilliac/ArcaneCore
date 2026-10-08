using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Instances.Scripts.BlackrockSpire;
using ArcaneCore.Game.Instances.Scripts.Scholomance;
using ArcaneCore.Game.Instances.Scripts.Stratholme;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class ScriptDev2DungeonTests
{
    private sealed class EventProbe(Map map) : InstanceData(map)
    {
        public uint EventId { get; private set; }
        public override void OnSpellEvent(Unit caster, uint eventId) => EventId = eventId;
    }
    private sealed record ScriptRun(InstanceFixture Fixture, Player Player) : IDisposable
    {
        public Map Map => Player.Map!;
        public InstanceData Data => InstanceManager.InstanceDataOf(Map)!;
        public CreatureMapSystem Creatures => Map.FindUpdater<CreatureMapSystem>()!;
        public GameObjectMapSystem Objects => Map.FindUpdater<GameObjectMapSystem>()!;
        public Creature Creature(uint guid) => Creatures.Creatures.Single(c => c.Spawn?.Guid == guid);
        public GameObject Object(uint entry) => Objects.GameObjects.Single(go => go.Entry == entry);
        public void Kill(uint guid) => Map.Combat.Kill(Player, Creature(guid));
        public void Dispose() => Fixture.Dispose();
    }

    private static ScriptRun Enter(Func<Map, InstanceData> script, IEnumerable<(uint Guid, uint Entry, float X, float Z)> spawns,
        IEnumerable<(uint Entry, float X)> doors, IEnumerable<uint>? extraTemplates = null)
    {
        var fixture = new InstanceFixture();
        fixture.Manager.Scripts = new InstanceScriptRegistry().Register(InstanceFixture.Dungeon, script);
        CreatureSpawn[] creatureSpawns = [.. spawns.Select(s => Spawn(s.Guid, s.Entry, s.X, -383.07f, s.Z, mapId: InstanceFixture.Dungeon))];
        var content = new CreatureContent(
            [.. creatureSpawns.Select(s => s.Entry).Concat(extraTemplates ?? []).Distinct().Select(e => Template(e))], creatureSpawns, [], [], []);
        (uint Entry, float X)[] doorSpawns = [.. doors];
        var objects = new GameObjectContent(
            [.. doorSpawns.Select(d => GameObjectTestKit.GoTemplate(d.Entry, GameObjectType.Door))],
            [.. doorSpawns.Select((d, i) => GameObjectTestKit.GoSpawn(500u + (uint)i, d.Entry, d.X, -383.07f)
                with { MapId = InstanceFixture.Dungeon, Z = 61.78f })], [], [], []);
        fixture.World.MapCreated += map =>
        {
            if (map.MapId == InstanceFixture.Dungeon)
            {
                map.AddUpdater(new CreatureMapSystem(map, content, random: new Random(1)));
                map.AddUpdater(new GameObjectMapSystem(map, objects));
            }
        };

        Player player = fixture.AddPlayer(1);
        Assert.True(fixture.EnterDungeon(player));
        fixture.Tick();
        return new ScriptRun(fixture, player);
    }

    [Fact]
    public void Registry_InstallsAllFourScriptDev2DungeonMaps()
    {
        foreach (uint mapId in new uint[] { 229, 289, 329, 429 })
        {
            Assert.True(InstanceScriptRegistry.Default.HasScript(mapId));
        }
    }

    [Fact]
    public void Spire_StadiumStateSavesAndAnActiveEncounterResetsOnLoad()
    {
        using var f = new InstanceFixture();
        var spire = new BlackrockSpireInstance(f.World.GetMap(InstanceFixture.Dungeon, 42));
        spire.Initialize();
        spire.SetData(BlackrockSpireInstance.TypeStadium, EncounterState.InProgress);
        Assert.Equal(EncounterState.InProgress, spire.GetData(BlackrockSpireInstance.TypeStadium));
        spire.SetData(BlackrockSpireInstance.TypeStadium, EncounterState.Done);
        var restored = new BlackrockSpireInstance(f.World.GetMap(InstanceFixture.Dungeon, 43));
        restored.Initialize();
        restored.Load(spire.GetSaveData()!);
        Assert.Equal(EncounterState.Done, restored.GetData(BlackrockSpireInstance.TypeStadium));
        restored.Load("0 1 0 1 0 0");
        Assert.Equal(EncounterState.NotStarted, restored.GetData(BlackrockSpireInstance.TypeEmberseer));
        Assert.Equal(EncounterState.NotStarted, restored.GetData(BlackrockSpireInstance.TypeStadium));
    }

    [Fact]
    public void Scholomance_KirtonosDoorSkipsInitialAggroButAcceptsPostWipeAggro()
    {
        using var f = new InstanceFixture();
        var school = new ScholomanceInstance(f.World.GetMap(InstanceFixture.Dungeon, 44));
        school.Initialize();
        school.SetData(ScholomanceInstance.TypeKirtonos, EncounterState.InProgress);
        Assert.Equal(EncounterState.NotStarted, school.GetData(ScholomanceInstance.TypeKirtonos));
        school.SetData(ScholomanceInstance.TypeKirtonos, EncounterState.Fail);
        school.SetData(ScholomanceInstance.TypeKirtonos, EncounterState.InProgress);
        Assert.Equal(EncounterState.InProgress, school.GetData(ScholomanceInstance.TypeKirtonos));
    }

    [Fact]
    public void Stratholme_BaronRunExpiresByMapTicksAndFreezesWhenBaronEngages()
    {
        using var f = new InstanceFixture();
        var strath = new StratholmeInstance(f.World.GetMap(InstanceFixture.Dungeon, 45));
        strath.Initialize();
        strath.SetData(StratholmeInstance.TypeBaronRun, EncounterState.InProgress);
        strath.Update(44u * 60u * 1000u);
        Assert.Equal(EncounterState.InProgress, strath.GetData(StratholmeInstance.TypeBaronRun));
        strath.SetData(StratholmeInstance.TypeBaron, EncounterState.InProgress);
        strath.Update(2u * 60u * 1000u);
        Assert.Equal(EncounterState.InProgress, strath.GetData(StratholmeInstance.TypeBaronRun));
        strath.SetData(StratholmeInstance.TypeBaron, EncounterState.Fail);
        strath.Update(60u * 1000u);
        Assert.Equal(EncounterState.Fail, strath.GetData(StratholmeInstance.TypeBaronRun));
    }

    [Fact]
    public void DireMaul_TributeTierCountsTheSixGuardsSpared()
    {
        using var f = new InstanceFixture();
        var dire = new DireMaulInstance(f.World.GetMap(InstanceFixture.Dungeon, 46));
        dire.Initialize();
        Assert.Equal(6, dire.TributeGuardsSpared);
        dire.SetData(DireMaulInstance.TypeMoldar, EncounterState.Done);
        dire.SetData(DireMaulInstance.TypeFengus, EncounterState.Done);
        Assert.Equal(4, dire.TributeGuardsSpared);
        Assert.True(dire.TributeConditionMet(4));
        Assert.False(dire.TributeConditionMet(5));
        Assert.True(dire.CheckConditionCriteriaMeet(f.AddPlayer(1), 4));
    }

    [Fact]
    public void BossFactory_UsesScriptDev2AiOnlyForItsDungeonMap()
    {
        using ScriptRun run = Enter(map => new BlackrockSpireInstance(map),
            [(1, BlackrockSpireInstance.NpcGyth, -12f, 61.78f)], []);
        Creature gyth = run.Creature(1);
        Assert.IsType<GythAI>(DungeonBossAis.Create(gyth, 229));
        Assert.Null(DungeonBossAis.Create(gyth, 289));
    }

    [Fact]
    public void Spire_CreatureSystemSelectsGythAiAndPreventsDeathUntilRendDismounts()
    {
        using var f = new InstanceFixture();
        Map map = f.World.GetMap(BlackrockSpireInstance.MapId, 96);
        var template = Template(BlackrockSpireInstance.NpcGyth);
        var creatures = new CreatureMapSystem(map, Content([template], []));
        map.AddUpdater(creatures);
        Creature gyth = creatures.SpawnTemporary(template, 210.14f, -397.54f, 111.1f, 0f);

        Assert.IsType<GythAI>(gyth.AI);
        Assert.Equal(1u, gyth.InvincibilityHpThreshold);
    }

    [Fact]
    public void Spire_RoomRuneOpensOnlyAfterItsLastBlackhandDefenderDies()
    {
        using ScriptRun run = Enter(map => new BlackrockSpireInstance(map),
            [(1, 9818, -12f, 61.78f), (2, 9819, -11f, 61.78f)],
            [(175197, -10f), (BlackrockSpireInstance.GoEmberseerIn, -8f)]);
        run.Data.OnAreaTrigger(run.Player, 2046);
        Assert.Equal(EncounterState.InProgress, run.Data.GetData(BlackrockSpireInstance.TypeRoomEvent));
        run.Kill(1);
        Assert.Equal(GameObjectState.Ready, run.Object(175197).State);
        run.Kill(2);
        Assert.Equal(GameObjectState.Active, run.Object(175197).State);
        Assert.Equal(GameObjectState.Active, run.Object(BlackrockSpireInstance.GoEmberseerIn).State);
        Assert.Equal(EncounterState.Done, run.Data.GetData(BlackrockSpireInstance.TypeRoomEvent));
    }

    [Fact]
    public void Spire_FatherFlameSendsSixTimedRookeryWavesThenSolakar()
    {
        using ScriptRun run = Enter(map => new BlackrockSpireInstance(map), [],
            [(BlackrockSpireInstance.GoFatherFlame, -16f)], [10258, 10683, BlackrockSpireInstance.NpcSolakar]);
        Assert.Equal(GameObjectUseResult.Ok, run.Objects.Use(run.Player, run.Object(BlackrockSpireInstance.GoFatherFlame).Guid));
        run.Data.Update(1);
        Assert.Equal(2, run.Creatures.Creatures.Count(c => c.Spawn is null && c.Template.Entry == 10683));
        for (int i = 0; i < 6; i++)
        {
            run.Data.Update(40_000);
        }

        Assert.Contains(run.Creatures.Creatures, c => c.Template.Entry == BlackrockSpireInstance.NpcSolakar && c.Spawn is null);
        Assert.Equal(EncounterState.Special, run.Data.GetData(BlackrockSpireInstance.TypeFlamewreath));
    }

    [Fact]
    public void Spire_StadiumIntroSummonsBalconySpectatorsBeforeTheFirstWave()
    {
        using ScriptRun run = Enter(map => new BlackrockSpireInstance(map), [],
            [(BlackrockSpireInstance.GoGythEntry, -12f), (BlackrockSpireInstance.GoGythCombat, -10f),
             (BlackrockSpireInstance.GoGythExit, -8f)],
            [BlackrockSpireInstance.NpcNefarius, BlackrockSpireInstance.NpcRend,
             BlackrockSpireInstance.NpcGyth, BlackrockSpireInstance.NpcWhelp,
             BlackrockSpireInstance.NpcDragon, BlackrockSpireInstance.NpcHandler, 9819, 10317]);
        run.Data.OnAreaTrigger(run.Player, 2026);
        Assert.Equal(EncounterState.InProgress, run.Data.GetData(BlackrockSpireInstance.TypeStadium));
        run.Data.Update(7_000);
        Assert.Equal(0, ((BlackrockSpireInstance)run.Data).StadiumWave);
        run.Data.Update(5_000);
        Assert.Equal(1, ((BlackrockSpireInstance)run.Data).StadiumWave);
        Assert.Equal(12, run.Creatures.Creatures.Count(c => c.Spawn is null && c.Template.Entry is 9819 or 10317));
        Assert.Equal(4, run.Creatures.Creatures.Count(c => c.Spawn is null
            && c.Template.Entry is BlackrockSpireInstance.NpcWhelp or BlackrockSpireInstance.NpcDragon));
        run.Data.SetData(BlackrockSpireInstance.TypeStadium, EncounterState.Done);
        Assert.Equal(GameObjectState.Active, run.Object(BlackrockSpireInstance.GoGythExit).State);
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.Spawn is null && c.Template.Entry is 9819 or 10317);
    }

    [Fact]
    public void Scholomance_KirtonosDeathOpensTheGateThroughTheInstanceCallback()
    {
        using ScriptRun run = Enter(map => new ScholomanceInstance(map),
            [(1, 10506, -12f, 61.78f)], [(ScholomanceInstance.GoKirtonosGate, -11f)]);
        Assert.Equal(GameObjectState.Ready, run.Object(ScholomanceInstance.GoKirtonosGate).State);
        run.Kill(1);
        Assert.Equal(EncounterState.Done, run.Data.GetData(ScholomanceInstance.TypeKirtonos));
        Assert.Equal(GameObjectState.Active, run.Object(ScholomanceInstance.GoKirtonosGate).State);
        Assert.StartsWith("3 0 0", run.Fixture.Manager.FindSave(run.Map.InstanceId)!.Data, StringComparison.Ordinal);
    }

    [Fact]
    public void Scholomance_BrazierClosesTheGateAndSummonsKirtonosAfterFiveSeconds()
    {
        using ScriptRun run = Enter(map => new ScholomanceInstance(map), [],
            [(ScholomanceInstance.GoBrazierOfTheHerald, -16f), (ScholomanceInstance.GoKirtonosGate, -10f)],
            [ScholomanceInstance.NpcKirtonos]);
        GameObject gate = run.Object(ScholomanceInstance.GoKirtonosGate);
        run.Objects.ToggleDoorOrButton(gate); // the DB gate begins open
        Assert.Equal(GameObjectState.Active, gate.State);

        Assert.Equal(GameObjectUseResult.Ok, run.Objects.Use(run.Player, run.Object(ScholomanceInstance.GoBrazierOfTheHerald).Guid));
        Assert.Equal(GameObjectState.Ready, gate.State);
        run.Data.Update(4_999);
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.Template.Entry == ScholomanceInstance.NpcKirtonos);
        run.Data.Update(1);
        Creature kirtonos = Assert.Single(run.Creatures.Creatures, c => c.Template.Entry == ScholomanceInstance.NpcKirtonos);
        Assert.Equal(309.65f, kirtonos.X);
        run.Map.Combat.Kill(run.Player, kirtonos);
        Assert.Equal(GameObjectState.Active, gate.State);
    }

    [Fact]
    public void Scholomance_GandlingPortalEventSummonsDbGuardiansThenOpensTheRoomOnTheirDeaths()
    {
        using ScriptRun run = Enter(map => new ScholomanceInstance(map),
            [(1, ScholomanceInstance.NpcGandling, -12f, 61.78f)],
            [(177376, -10f)], [ScholomanceInstance.NpcGuardian]);
        GameObject roomDoor = run.Object(177376); // Polkelt room, event 5618
        run.Objects.ToggleDoorOrButton(roomDoor); // room starts open
        Assert.Equal(GameObjectState.Active, roomDoor.State);

        run.Data.OnSpellEvent(run.Creature(1), 5618);
        Assert.Equal(GameObjectState.Ready, roomDoor.State);
        run.Data.Update(1_999);
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.Template.Entry == ScholomanceInstance.NpcGuardian);
        run.Data.Update(1);
        Creature[] guardians = [.. run.Creatures.Creatures.Where(c => c.Template.Entry == ScholomanceInstance.NpcGuardian)];
        Assert.Equal(4, guardians.Length); // four Polkelt rows in z2815 dbscripts_on_event
        foreach (Creature guardian in guardians)
        {
            run.Map.Combat.Kill(run.Player, guardian);
        }

        Assert.Equal(GameObjectState.Active, roomDoor.State);
    }

    [Fact]
    public void SpellSendEvent_ForwardsItsMiscValueToTheMapInstanceScript()
    {
        const uint spellId = 901234;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.SendEvent, 0, misc: 5618)));
        (Player player, _) = kit.AddPlayer(1);
        var probe = new EventProbe(player.Map!);
        player.Map!.AddUpdater(probe);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(5618u, probe.EventId);
    }

    [Fact]
    public void Stratholme_BaronRunStartsFromGauntletGateUse()
    {
        using ScriptRun run = Enter(map => new StratholmeInstance(map), [],
            [(StratholmeInstance.GoGauntletGate1, -12f)]);
        Assert.Equal(GameObjectUseResult.Ok, run.Objects.Use(run.Player, run.Object(StratholmeInstance.GoGauntletGate1).Guid));
        Assert.Equal(EncounterState.InProgress, run.Data.GetData(StratholmeInstance.TypeBaronRun));
        Assert.Equal(45u * 60u * 1000u, ((StratholmeInstance)run.Data).BaronRunRemainingMs);
    }

    [Fact]
    public void Stratholme_BaronDefeatDuringTheRunCreditsYsidaAndCastsTheReward()
    {
        using var f = new InstanceFixture();
        var credited = new List<uint>();
        var cast = new List<uint>();
        f.Manager.Scripts = new InstanceScriptRegistry().Register(InstanceFixture.Dungeon, map => new StratholmeInstance(map));
        f.Manager.ScriptCreatureCredit = (_, entry, _) => credited.Add(entry);
        f.Manager.ScriptCastPlayerSpell = (_, spell) => cast.Add(spell);
        Player player = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(player));
        var strath = (StratholmeInstance)InstanceManager.InstanceDataOf(player.Map!)!;

        strath.SetData(StratholmeInstance.TypeBaronRun, EncounterState.InProgress);
        strath.SetData(StratholmeInstance.TypeBaron, EncounterState.Done);

        Assert.Equal(EncounterState.Done, strath.GetData(StratholmeInstance.TypeBaronRun));
        Assert.Equal([StratholmeInstance.NpcYsida], credited);
        Assert.Equal([StratholmeInstance.SpellYsidaFreed], cast);
    }

    [Fact]
    public void Stratholme_ThreeClearedZigguratsOpenTheSlaughterPorts()
    {
        const float entry = -16.4f;
        using ScriptRun run = Enter(map => new StratholmeInstance(map),
            [(1, StratholmeInstance.NpcAcolyte, entry + 5, 61.78f),
             (2, StratholmeInstance.NpcAcolyte, entry + 105, 61.78f),
             (3, StratholmeInstance.NpcAcolyte, entry + 205, 61.78f),
             (4, StratholmeInstance.NpcAcolyte, entry + 280, 90f), // SD2 announcer: highest
             (5, StratholmeInstance.NpcBaroness, entry + 6, 61.78f),
             (6, StratholmeInstance.NpcNerub, entry + 106, 61.78f),
             (7, StratholmeInstance.NpcPallid, entry + 206, 61.78f)],
            [(StratholmeInstance.GoZiggurat1, entry + 4),
             (StratholmeInstance.GoZiggurat2, entry + 104),
             (StratholmeInstance.GoZiggurat3, entry + 204),
             (StratholmeInstance.GoGauntletPort, entry + 8),
             (StratholmeInstance.GoSlaughterPort, entry + 10)]);

        run.Kill(5);
        run.Kill(6);
        run.Kill(7);
        run.Kill(1);
        run.Kill(2);
        run.Kill(3);

        Assert.Equal(EncounterState.Special, run.Data.GetData(StratholmeInstance.TypeBaroness));
        Assert.Equal(EncounterState.Special, run.Data.GetData(StratholmeInstance.TypeNerub));
        Assert.Equal(EncounterState.Special, run.Data.GetData(StratholmeInstance.TypePallid));
        Assert.Equal(GameObjectState.Active, run.Object(StratholmeInstance.GoGauntletPort).State);
        Assert.Equal(GameObjectState.Active, run.Object(StratholmeInstance.GoSlaughterPort).State);
    }

    [Fact]
    public void Stratholme_LastAbominationSummonsRamstein_WhoseDeathStartsTheUndeadWaves()
    {
        using ScriptRun run = Enter(map => new StratholmeInstance(map),
            [(1, StratholmeInstance.NpcBileAbom, -12f, 61.78f),
             (2, StratholmeInstance.NpcVenomAbom, -10f, 61.78f),
             (3, StratholmeInstance.NpcBaron, -8f, 61.78f)],
            [(StratholmeInstance.GoGauntletPort, -12f),
             (StratholmeInstance.GoSlaughterhouse, -10f),
             (StratholmeInstance.GoSlaughterGate, -8f)],
            [StratholmeInstance.NpcRamstein, StratholmeInstance.NpcMindless, StratholmeInstance.NpcBlackGuard]);

        run.Kill(1);
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.Template.Entry == StratholmeInstance.NpcRamstein);
        run.Kill(2);
        Creature ramstein = Assert.Single(run.Creatures.Creatures, c => c.Template.Entry == StratholmeInstance.NpcRamstein);
        Assert.Equal(EncounterState.Special, run.Data.GetData(StratholmeInstance.TypeRamstein));
        run.Map.Combat.Kill(run.Player, ramstein);
        Assert.Equal(EncounterState.Done, run.Data.GetData(StratholmeInstance.TypeRamstein));
        Assert.Equal(5, run.Creatures.Creatures.Count(c => c.Template.Entry == StratholmeInstance.NpcBlackGuard));
        run.Data.Update(500);
        Assert.Equal(1u, ((StratholmeInstance)run.Data).MindlessSummoned);
    }

    [Fact]
    public void DireMaul_GuardDeathsReduceTheTributeTierThroughTheInstanceCallback()
    {
        using ScriptRun run = Enter(map => new DireMaulInstance(map),
            [(1, DireMaulInstance.NpcMoldar, -12f, 61.78f),
             (2, DireMaulInstance.NpcFengus, -10f, 61.78f)], []);
        run.Kill(1);
        run.Kill(2);
        var dire = (DireMaulInstance)run.Data;
        Assert.Equal(4, dire.TributeGuardsSpared);
        Assert.True(dire.TributeConditionMet(4));
        Assert.NotNull(run.Fixture.Manager.FindSave(run.Map.InstanceId)!.Data);
    }
}
