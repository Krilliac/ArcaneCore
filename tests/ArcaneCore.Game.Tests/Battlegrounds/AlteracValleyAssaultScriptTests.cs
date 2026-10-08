using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Battlegrounds.AlteracValley;
using static ArcaneCore.Game.Tests.Battlegrounds.AvScriptRig;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// The Alterac Valley assault scripts (vmangos scripts/battlegrounds/battleground_alterac.cpp) on a real creature and object system with
/// simulated time (<see cref="AvScriptRig"/>): the ground, cavalry and air assaults the collectors launch, the troops chief's attack, the
/// beacons and war riders, the world bosses and their summoners, and the collectors' menus.
/// </summary>
public sealed class AlteracValleyAssaultScriptTests
{
    // ------------------------------------------------------------------ ground assault

    [Fact]
    public void GroundAssault_TheQuartermasterSummonsTheChiefAndTenTroops_Once()
    {
        using AvScriptRig rig = Create(
            [AvNpc(NpcQuartermasterAlliance), AvNpc(NpcFieldMarshalTeravaine), AvNpc(NpcStormpikeCommando)],
            [Spawn(1, NpcQuartermasterAlliance, -240, -425, Z)], -243, -431);
        Creature quartermaster = rig.Single(NpcQuartermasterAlliance);
        Assert.IsType<AvEventAI>(quartermaster.AI);

        rig.Run(1000);
        Assert.Empty(rig.All(NpcFieldMarshalTeravaine)); // nothing without the go flag

        rig.Match.SetPlayerGoStatus(Team.Alliance, AssaultGround, true);
        rig.Run(200);

        Creature chief = rig.Single(NpcFieldMarshalTeravaine);
        Assert.Equal(-243.75f, chief.X, 0.01f);
        Assert.Equal(-431.32f, chief.Y, 0.01f);
        Assert.IsType<AvTroopsChiefAI>(chief.AI);
        Assert.Same(quartermaster, rig.Creatures.SummonerOf(chief));
        IReadOnlyList<Creature> troops = rig.All(NpcStormpikeCommando);
        Assert.Equal(10, troops.Count);
        Assert.Contains(troops, t => MathF.Abs(t.X - (-240.9f)) < 0.01f && MathF.Abs(t.Y - (-431.11f)) < 0.01f);
        Assert.Contains(troops, t => MathF.Abs(t.X - (-238.35f - 4)) < 0.01f && MathF.Abs(t.Y - (-432.42f - 4)) < 0.01f);
        Assert.False(rig.Match.PlayerGoStatus(Team.Alliance, AssaultGround)); // consumed

        rig.Match.SetPlayerGoStatus(Team.Alliance, AssaultGround, true);
        rig.Run(1000);
        Assert.Single(rig.All(NpcFieldMarshalTeravaine)); // one batch per life of the quartermaster's AI

        Creature soldier = troops[0];
        rig.Map.Combat.Kill(null, soldier);
        rig.Run(200);
        Assert.DoesNotContain(soldier, rig.Creatures.Creatures); // TEMPSUMMON_CORPSE_DESPAWN
    }

    [Fact]
    public void TheAssaultOrders_SendTheChiefOnHisAttack_HeRalliesHisTroopsAtHisSecondPoint_AndStopsAtHisLast()
    {
        using AvScriptRig rig = Create(
            [AvNpc(NpcFieldMarshalTeravaine), AvNpc(NpcStormpikeCommando), AvNpc(NpcSeasonedCommando)],
            [
                Spawn(1, NpcFieldMarshalTeravaine, 0, 0, Z),
                Spawn(2, NpcStormpikeCommando, 3, 4, Z), Spawn(3, NpcStormpikeCommando, 5, -4, Z), Spawn(4, NpcSeasonedCommando, 2, 6, Z),
                Spawn(5, NpcStormpikeCommando, 120, 120, Z),
            ],
            0, 0, paths: Path(NpcFieldMarshalTeravaine, 0, 0, 48, step: 2f));
        Creature chief = rig.Single(NpcFieldMarshalTeravaine);
        var ai = Assert.IsType<AvTroopsChiefAI>(chief.AI);
        rig.Match.AddChallengeCounter(Team.Alliance, ChallengeIrondeepGround, 300);

        AvCollectorGossip.QuestRewarded(chief, Team.Alliance, QuestTroopsOrderAlliance, ItemAssaultOrdersStormpike, 1, worldBossLaunched: false);

        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Equal(0u, rig.Match.ChallengeCounter(Team.Alliance, ChallengeIrondeepGround));
        Assert.True(rig.Match.PlayerGoStatus(Team.Alliance, AssaultGround));
        Assert.True((chief.UnitFlags & UnitFlags.Pvp) != 0);
        Assert.False(chief.ScriptRun); // he walks

        rig.RunUntil(() => rig.Said().Contains(AvEventAI.SayRamRiderCommander), "the speech at the second point");
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Paused));
        Assert.Equal(2, ai.CurrentWaypointIndex - 1);
        rig.Session.Clear();

        rig.RunUntil(() => ai.SpeechDone, "the rally six seconds later");
        Assert.Equal(3, rig.Said().Count(id => id == AvEventAI.SayWarcryAlliance)); // the three soldiers within 40 yd, of both levels
        foreach (Creature soldier in rig.Creatures.Creatures.Where(c => c.Entry is NpcStormpikeCommando or NpcSeasonedCommando && c.X < 100))
        {
            Assert.Same(chief, rig.Scripts.LeaderOf(soldier));
            Assert.Equal(MovementGeneratorType.Follow, soldier.Motion.CurrentType);
        }

        Assert.Null(rig.Scripts.LeaderOf(rig.All(NpcStormpikeCommando).First(c => c.X > 100)));
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Paused));

        rig.RunUntil(() => !ai.HasEscortState(EscortAI.EscortState.Escorting), "the stop at point 48");
        Assert.Equal(2f * 49, chief.X, 0.5f);
        Assert.Equal(chief.X, chief.Home.X, 0.01f); // his home is where he stopped
        Assert.True(chief.IsAlive);
    }

    // ------------------------------------------------------------------ cavalry assault

    private static IEnumerable<(uint, uint, CreatureWaypoint)> RamPath(uint entry)
    {
        // From the commander's spawn (600, -3) down to the riders (610..618, -35..-47): point 5 is within 20 yd of all eight.
        // The riders stand at z 45 (battleground_alterac.cpp:1696-1704), the commander at 42 (classic-db spawn 150117).
        (float X, float Y)[] start = [(602, -8), (604, -13), (606, -18), (608, -23), (610, -28), (611, -33)];
        for (uint i = 0; i < start.Length; i++)
        {
            yield return (entry, EscortAI.EscortPathId, new CreatureWaypoint(i, start[i].X, start[i].Y, 44f, 0, 0));
        }

        for (uint i = 6; i <= 92; i++)
        {
            yield return (entry, EscortAI.EscortPathId, new CreatureWaypoint(i, 611 + ((i - 5) * 3f), -33, 44f, 0, 0));
        }
    }

    [Fact]
    public void CavalryAssault_TheCommanderSummonsEightRiders_RalliesThem_AndTheyGoOnAloneWhenHeDies()
    {
        using AvScriptRig rig = Create(
            [AvNpc(NpcRamRiderCommander, b => b.NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.QuestGiver)), AvNpc(NpcRamRider)],
            [Spawn(1, NpcRamRiderCommander, 600, -3, 42f)], 605, -20,
            paths: [.. RamPath(NpcRamRiderCommander), .. RamPath(NpcRamRider)]);
        Creature commander = rig.Single(NpcRamRiderCommander);
        var ai = Assert.IsType<AvEventAI>(commander.AI);
        Assert.Equal(2786u, commander.GetUInt32(UpdateFields.UnitFieldMountdisplayid)); // Reset mounts him
        rig.Match.AddChallengeCounter(Team.Alliance, ChallengeHideCavalry, 25);
        rig.Match.AddChallengeCounter(Team.Alliance, ChallengeTamedCavalry, 25);

        AvCollectorChoice choice = AvCollectorGossip.Select(rig.Match, commander, Team.Alliance, GossipActionInfoDef + ChallengeHideCavalry + 1, _ => false);

        Assert.True(choice.Close);
        Assert.Equal(0u, rig.Match.ChallengeCounter(Team.Alliance, ChallengeHideCavalry));
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
        rig.Run(200);
        IReadOnlyList<Creature> riders = rig.All(NpcRamRider);
        Assert.Equal(8, riders.Count);
        Assert.False(rig.Match.PlayerGoStatus(Team.Alliance, AssaultCavalry));
        Assert.All(riders, r => Assert.Equal(2786u, r.GetUInt32(UpdateFields.UnitFieldMountdisplayid)));

        rig.RunUntil(() => rig.Said().Contains(AvEventAI.SayRamRiderCommander), "the commander's speech at point 5");
        rig.Session.Clear();
        rig.RunUntil(() => rig.Said().Count(id => id == AvEventAI.SayWarcryAlliance) == 8, "the riders' war cries", detail: () =>
            $"said [{string.Join(",", rig.Said())}] commander at ({commander.X},{commander.Y}) wp {ai.CurrentWaypointIndex} state {ai.State} riders "
            + string.Join(";", riders.Select(r => $"({r.X},{r.Y},{r.IsAlive})")));
        Assert.All(riders, r => Assert.Same(commander, rig.Scripts.LeaderOf(r)));
        rig.RunUntil(() => ai.CurrentWaypointIndex >= 10, "the ride on");

        int at = ai.CurrentWaypointIndex;
        rig.Map.Combat.Kill(null, commander);
        foreach (Creature rider in riders)
        {
            var cavalry = Assert.IsType<AvCavalryAI>(rider.AI);
            Assert.True(cavalry.HasEscortState(EscortAI.EscortState.Escorting));
            Assert.Equal(at, cavalry.CurrentWaypointIndex);
            Assert.Null(rig.Scripts.LeaderOf(rider));
        }

        rig.Run(1000);
        Assert.All(riders, r => Assert.True(((AvCavalryAI)r.AI!).LeaderDead));
        rig.Run(AvCavalryAI.LeaderDeathDespawnMs + 1000);
        Assert.All(riders, r => Assert.DoesNotContain(r, rig.Creatures.Creatures)); // ten minutes after they saw him dead
    }

    // ------------------------------------------------------------------ air assault

    [Fact]
    public void GlobalAirAssault_TheWingCommanderBecomesHisWarRider_RisesGoesInvisible_AndLeavesTheWarRider()
    {
        using AvScriptRig rig = Create(
            [AvNpc(NpcWingCommanderGuse, b => b.NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.QuestGiver)), AvNpc(NpcWarRiderGuse)],
            [Spawn(1, NpcWingCommanderGuse, -1338.6f, -328.16f, Z)], -1335, -325, team: Team.Horde);
        Creature guse = rig.Single(NpcWingCommanderGuse);

        rig.Match.SetPlayerGoStatus(Team.Horde, AssaultAirGlobalSoldier, true);
        rig.Run(4900);
        Assert.NotEqual(AvEventAI.WarRiderDisplayId, guse.DisplayId);
        rig.Run(300);

        Assert.Equal(AvEventAI.WarRiderDisplayId, guse.DisplayId);
        Assert.Equal(0u, guse.NpcFlags & (uint)NpcFlags.QuestGiver);
        Assert.True((guse.Movement.Flags & MovementFlags.Flying) != 0);
        Assert.Contains(rig.Caster.Casts, c => c.Spell == AvEventAI.SpellVisualTransform);
        Assert.Equal(MovementGeneratorType.Point, guse.Motion.CurrentType); // up 30 yd
        Assert.Empty(rig.All(NpcWarRiderGuse));

        rig.RunUntil(() => rig.All(NpcWarRiderGuse).Count == 1, "the war rider");
        Assert.Contains(rig.Caster.Casts, c => c.Spell == AvEventAI.SpellInvisible && c.Triggered);
        Creature rider = rig.Single(NpcWarRiderGuse);
        Assert.IsType<AvWarRiderAI>(rider.AI);
        Assert.True(((AvEventAI)guse.AI!).WarRiderSummoned);
        rig.Run(20_000);
        Assert.Single(rig.All(NpcWarRiderGuse)); // only one
    }

    [Fact]
    public void AWarRider_FliesToTheEnemyBase_WandersThere_AndAttacksTheNearestEnemyWithItsSpells()
    {
        (float x, float y, float z) = AvWarRiderAI.EnemyBase(NpcWarRiderGuse)!.Value;
        using AvScriptRig rig = Create(
            [AvNpc(NpcWarRiderGuse, b => b.Faction = 14)],
            [Spawn(1, NpcWarRiderGuse, x - 30, y, z)], x + 200, y, hostility: new AlwaysHostile());
        Creature rider = rig.Single(NpcWarRiderGuse);
        var ai = Assert.IsType<AvWarRiderAI>(rider.AI);

        rig.Run(100);
        Assert.True(ai.HeadingForTheEnemyBase);
        Assert.Equal(x, rider.Home.X, 0.01f);
        Assert.Equal(MovementGeneratorType.Home, rider.Motion.CurrentType);
        Assert.True((rider.Movement.Flags & MovementFlags.Flying) != 0);

        rig.RunUntil(() => rider.Motion.CurrentType == MovementGeneratorType.Random, "the wander around the base");
        Assert.Equal(25f, ai.CasterChaseDistance);

        rig.Player.Relocate(rider.X + 20, rider.Y, rider.Z, 0, 0);
        rig.Map.OnObjectMoved(rig.Player);
        rig.RunUntil(() => ReferenceEquals(rider.Combat.Victim, rig.Player), "the attack on the nearest enemy within 50 yd");
        rig.RunUntil(() => rig.Caster.Casts.Any(c => c.Spell == AvWarRiderAI.SpellFireball), "a fireball within 30 yd");
    }

    // ------------------------------------------------------------------ beacons

    [Fact]
    public void ABeacon_BelongsToNobody_CallsItsRiderAfterAMinute_AndGoes_UnlessItIsUsedFirst()
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        GameObjectTemplate Beacon(uint entry) => new() { Entry = entry, Type = (uint)GameObjectType.Goober, DisplayId = 5311, Name = "Beacon", Data = data };
        using AvScriptRig rig = Create(
            [AvNpc(NpcWarRider), AvNpc(NpcAerieGryphon)], [], 0, 0,
            objectTemplates: [Beacon(GameObjectBeaconGuse), Beacon(GameObjectBeaconSlidore)]);

        // Planted by players: the summon gives the beacon the caster's level and makes the caster its owner.
        GameObject guse = rig.Objects.Summon(GameObjectBeaconGuse, 2, 0, Z, 0)!;
        GameObject slidore = rig.Objects.Summon(GameObjectBeaconSlidore, 0, 2, Z, 0)!;
        guse.SetUInt32(UpdateFields.GameobjectLevel, 60);
        guse.SetOwner(rig.Player.Guid);

        rig.Run(200);
        Assert.Equal(0u, guse.GetUInt32(UpdateFields.GameobjectLevel));
        Assert.Equal(84u, guse.GetUInt32(UpdateFields.GameobjectFaction));
        Assert.Equal(83u, slidore.GetUInt32(UpdateFields.GameobjectFaction));
        Assert.True(guse.OwnerGuid.IsEmpty);

        Assert.Equal(GameObjectUseResult.Ok, rig.Objects.Use(rig.Player, slidore.Guid)); // channelled away
        Assert.DoesNotContain(slidore, rig.Objects.GameObjects);

        rig.Run(58_000);
        Assert.Contains(guse, rig.Objects.GameObjects);
        Assert.Empty(rig.All(NpcWarRider));
        rig.RunUntil(() => rig.All(NpcWarRider).Count == 1, "the war rider a minute after the planting");
        Creature rider = rig.Single(NpcWarRider);
        Assert.Equal(Z + 30, rider.Z, 0.01f);
        Assert.DoesNotContain(guse, rig.Objects.GameObjects);
        Assert.Empty(rig.All(NpcAerieGryphon));
    }

    // ------------------------------------------------------------------ world bosses

    [Fact]
    public void TheIceLord_ClaimsHisEvent_EndsTheSummoning_AnnouncesHimself_AndMarchesToTheEnemyBase()
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        using AvScriptRig rig = Create(
            [AvNpc(NpcLokholar), AvNpc(NpcFrostwolfShaman), AvNpc(NpcPrimalistThurloga)],
            [Spawn(2, NpcFrostwolfShaman, 5, 5, Z), Spawn(3, NpcPrimalistThurloga, -5, 5, Z)],
            0, 0, paths: Path(NpcLokholar, 0, 0, 31),
            objectTemplates: [new GameObjectTemplate { Entry = GameObjectInvocationHorde, Type = (uint)GameObjectType.SummoningRitual, DisplayId = 456, Name = "Altar", Data = data }],
            objectSpawns: [new GameObjectSpawn { Guid = 1, Entry = GameObjectInvocationHorde, MapId = 0, X = 3, Y = 0, Z = Z }],
            team: Team.Horde);
        rig.Caster.Auras.Add((rig.Player, SpellInvocation));
        Creature thurloga = rig.Single(NpcPrimalistThurloga);
        Creature shaman = rig.Single(NpcFrostwolfShaman);

        Creature boss = rig.Creatures.SpawnTemporary(rig.Creatures.Content.FindTemplate(NpcLokholar)!, 1, 0, Z, 0);
        var ai = Assert.IsType<AvWorldBossAI>(boss.AI);
        Assert.True(rig.Match.IsActiveEvent(EventBossLokholar, 0));
        Assert.Contains((rig.Player, SpellInvocation), rig.Caster.RemovedAuras);
        Assert.DoesNotContain(rig.Objects.GameObjects, g => g.Entry == GameObjectInvocationHorde);
        Assert.Contains(shaman, rig.Caster.Interrupted);
        Assert.Contains(thurloga, rig.Caster.Interrupted);

        // A second Ice Lord while the first lives is refused.
        Creature second = rig.Creatures.SpawnTemporary(rig.Creatures.Content.FindTemplate(NpcLokholar)!, 2, 2, Z, 0);
        Assert.True(((AvWorldBossAI)second.AI!).Refused);
        rig.Run(200);
        Assert.DoesNotContain(second, rig.Creatures.Creatures);
        Assert.True(rig.Match.IsActiveEvent(EventBossLokholar, 0));

        Assert.Contains(AvWorldBossAI.SayLokholarSpawn1, rig.Said());
        Assert.Equal((-260f, -290f), (boss.Home.X, boss.Home.Y));
        rig.RunUntil(() => rig.Said().Contains(AvWorldBossAI.SayLokholarSpawn2), "the second line three seconds later", 4000);
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting)); // a second after he came

        rig.RunUntil(() => rig.Said().Contains(AvWorldBossAI.SayLokholarReachedBase), "the arrival at point 30");
        Assert.Equal(boss.X, boss.Home.X, 0.01f);
        rig.RunUntil(() => !ai.HasEscortState(EscortAI.EscortState.Escorting), "the stop at point 31");

        rig.Session.Clear();
        rig.Map.Combat.Kill(boss, rig.Player);
        Assert.Contains(AvWorldBossAI.SayLokholarKilledPlayer, rig.Said());
        Assert.Contains(rig.Caster.Casts, c => c.Spell == AvWorldBossAI.SpellSwellOfSouls && c.Triggered);

        rig.Map.Combat.Kill(null, boss);
        Assert.False(rig.Match.IsActiveEvent(EventBossLokholar, 0)); // another may come now
    }

    [Fact]
    public void Thurloga_RidesToTheSummoningPlace_PlacesTheAltar_AndLeavesTenMinutesAfterTheIceLordCame()
    {
        // 44 points from (-394, -133) to the altar's place, point 43 at (-360, -133).
        (uint, uint, CreatureWaypoint)[] path =
            [.. Enumerable.Range(0, 44).Select(i => (NpcPrimalistThurloga, EscortAI.EscortPathId, new CreatureWaypoint((uint)i, -394f + (i * (34f / 43f)), -133f, Z, 0, 0)))];
        uint[] data = new uint[GameObjectTemplate.DataCount];
        using AvScriptRig rig = Create(
            [AvNpc(NpcPrimalistThurloga, b => b.NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.QuestGiver)), AvNpc(NpcFrostwolfShaman)],
            [Spawn(1, NpcPrimalistThurloga, -395, -133, Z), Spawn(2, NpcFrostwolfShaman, -385, -130, Z), Spawn(3, NpcFrostwolfShaman, -385, -136, Z)],
            -380, -133, paths: path,
            objectTemplates: [new GameObjectTemplate { Entry = GameObjectInvocationHorde, Type = (uint)GameObjectType.SummoningRitual, DisplayId = 456, Name = "Altar", Data = data }],
            team: Team.Horde);
        Creature thurloga = rig.Single(NpcPrimalistThurloga);
        var ai = Assert.IsType<AvEventAI>(thurloga.AI);
        IReadOnlyList<Creature> shamans = rig.All(NpcFrostwolfShaman);

        AvCollectorGossip.QuestRewarded(thurloga, Team.Horde, QuestHordeBoss1, 17306, 5, worldBossLaunched: true);
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));

        rig.RunUntil(() => thurloga.GetUInt32(UpdateFields.UnitFieldMountdisplayid) == 12242, "the mount at point 6");
        Assert.All(shamans, s => Assert.Equal(1166u, s.GetUInt32(UpdateFields.UnitFieldMountdisplayid)));
        rig.RunUntil(() => ai.InvocationPlaced, "the altar at point 43");
        Assert.Equal(0u, thurloga.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
        Assert.All(shamans, s => Assert.Equal(0u, s.GetUInt32(UpdateFields.UnitFieldMountdisplayid)));
        Assert.Contains(AvEventAI.SayPrimalistThurloga, rig.Said());
        Assert.Equal(3, rig.Caster.Casts.Count(c => c.Spell == SpellInvocation && ReferenceEquals(c.Target, thurloga))); // she and her two shamans
        GameObject altar = rig.Objects.GameObjects.Single(g => g.Entry == GameObjectInvocationHorde);
        Assert.Equal(-360.139f, altar.X, 0.01f);
        Assert.False(ai.HasEscortState(EscortAI.EscortState.Escorting));

        rig.Run(5000);
        Assert.DoesNotContain(AvEventAI.SayLokholarSpawned, rig.Said());
        rig.Objects.Remove(altar); // the Ice Lord came (his script deletes the altar)
        rig.RunUntil(() => rig.Said().Contains(AvEventAI.SayLokholarSpawned), "the yell once the Ice Lord came", 1000);

        rig.Run(599_000);
        Assert.True(thurloga.IsAlive);
        rig.Run(1000);
        Assert.False(thurloga.IsAlive);
        Assert.All(shamans, s => Assert.False(s.IsAlive));
    }

    [Fact]
    public void ASummonersAdd_LeavesItsMountAndChannelForAFight_AndTakesThemUpAgainAfterIt()
    {
        using AvScriptRig rig = Create([AvNpc(NpcFrostwolfShaman, b => b.Faction = 14)], [Spawn(1, NpcFrostwolfShaman, 5, 0, Z)], 0, 0);
        Creature shaman = rig.Single(NpcFrostwolfShaman);
        Assert.Contains(rig.Caster.Casts, c => c.Spell == AvSummonerAddAI.SpellLightningShield);
        shaman.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 1166);
        rig.Caster.Auras.Add((shaman, SpellInvocation));
        rig.Caster.Casts.Clear();

        rig.Map.Combat.DealDamage(rig.Player, shaman, 1, direct: false);
        Assert.Equal(0u, shaman.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
        Assert.Contains(shaman, rig.Caster.Interrupted);

        rig.Creatures.EnterEvadeMode(shaman);
        Assert.Equal(1166u, shaman.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
        Assert.Contains(rig.Caster.Casts, c => c.Spell == SpellInvocation);
    }

    // ------------------------------------------------------------------ the collectors' menus

    private static AvScriptRig QuartermasterRig(out Creature quartermaster)
    {
        AvScriptRig rig = Create(
            [AvNpc(NpcQuartermasterAlliance, b => b.NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.QuestGiver | NpcFlags.Vendor))],
            [Spawn(1, NpcQuartermasterAlliance, 5, 0, Z)], 0, 0);
        quartermaster = rig.Single(NpcQuartermasterAlliance);
        return rig;
    }

    private static readonly (uint, uint)[] s_quartermasterQuests = [(QuestAllianceNearMine, 17522), (QuestAllianceOtherMine, 17542)];

    [Fact]
    public void TheQuartermaster_OffersTheGroundAssault_OnceTheSuppliesAreIn_ToANeutralPlayer()
    {
        using AvScriptRig rig = QuartermasterRig(out Creature quartermaster);

        AvCollectorHello waiting = AvCollectorGossip.Hello(rig.Match, quartermaster, rig.Player, Team.Alliance, s_quartermasterQuests, _ => RankNeutral);
        Assert.Equal(AvCollectorGossip.NpcTextQuartermaster, waiting.NpcTextId);
        Assert.True(waiting.ShowQuests);
        Assert.Equal(
            [new AvGossipLine(0, AvCollectorGossip.GossipGroundAssault, GossipActionInfoDef + ChallengeCount + 13 + 1), new AvGossipLine(1, GossipTextBrowseGoods, GossipActionTrade)],
            waiting.Items);

        rig.Match.AddChallengeCounter(Team.Alliance, ChallengeIrondeepGround, 280);
        rig.Session.Clear();
        AvCollectorHello ready = AvCollectorGossip.Hello(rig.Match, quartermaster, rig.Player, Team.Alliance, s_quartermasterQuests, _ => RankNeutral);
        Assert.Equal(
            [
                new AvGossipLine(0, AvCollectorGossip.GossipGroundAssault, GossipActionInfoDef + ChallengeCount + 14 + 1),
                new AvGossipLine(0, AvCollectorGossip.GossipAssaultGround, GossipActionInfoDef + ChallengeColdtoothGround + 1),
                new AvGossipLine(1, GossipTextBrowseGoods, GossipActionTrade),
            ],
            ready.Items);
        Assert.Single(CreatureAiTestSupport.Packets(rig.Session, WorldOpcode.SmsgEmote)); // the bow

        AvCollectorHello hated = AvCollectorGossip.Hello(rig.Match, quartermaster, rig.Player, Team.Alliance, s_quartermasterQuests, _ => 0);
        Assert.True(hated.Silent);

        rig.Session.Clear();
        AvCollectorChoice launch = AvCollectorGossip.Select(rig.Match, quartermaster, Team.Alliance, GossipActionInfoDef + ChallengeColdtoothGround + 1, _ => false);
        Assert.Equal(new AvCollectorChoice(true, false, 0, ItemAssaultOrdersStormpike), launch);
        Assert.True(rig.Match.PlayerGoStatus(Team.Alliance, AssaultGround));
        Assert.Equal(0u, rig.Match.ChallengeCounter(Team.Alliance, ChallengeIrondeepGround));
        Assert.Contains(AvCollectorGossip.SayPatrol, rig.Said());

        AvCollectorChoice again = AvCollectorGossip.Select(rig.Match, quartermaster, Team.Alliance, GossipActionInfoDef + ChallengeIrondeepGround + 1, _ => true);
        Assert.Equal(0u, again.GiveItem); // already has the orders

        Assert.Equal(new AvCollectorChoice(false, false, 6731, 0),
            AvCollectorGossip.Select(rig.Match, quartermaster, Team.Alliance, GossipActionInfoDef + ChallengeCount + 14 + 1, _ => false));
        Assert.Equal(new AvCollectorChoice(false, true, 0, 0),
            AvCollectorGossip.Select(rig.Match, quartermaster, Team.Alliance, GossipActionTrade, _ => false));
    }

    [Fact]
    public void AWingCommanderAwayFromHisPost_SetsOffWhenHisSideSpeaksToHim()
    {
        using AvScriptRig rig = Create(
            [AvNpc(NpcWingCommanderGuse, b => b.NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.QuestGiver))],
            [Spawn(1, NpcWingCommanderGuse, 0, 0, Z)], 2, 0, paths: Path(NpcWingCommanderGuse, 0, 0, 74), team: Team.Horde);
        Creature guse = rig.Single(NpcWingCommanderGuse);
        var ai = Assert.IsType<AvEventAI>(guse.AI);
        rig.Player.FactionTemplate = 2;

        AvCollectorHello hello = AvCollectorGossip.Hello(rig.Match, guse, rig.Player, Team.Horde, [], _ => RankNeutral);

        Assert.True(hello.Default);
        Assert.True(ai.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Equal(2u, guse.FactionTemplate);
        Assert.Equal(0u, guse.NpcFlags & (uint)NpcFlags.QuestGiver);
        Assert.True((guse.UnitFlags & UnitFlags.Pvp) != 0);
    }

    [Fact]
    public void TheSummoner_ShowsHowCloseTheOfferingIs()
    {
        using AvScriptRig rig = Create(
            [AvNpc(NpcPrimalistThurloga, b => b.NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.QuestGiver))],
            [Spawn(1, NpcPrimalistThurloga, 5, 0, Z)], 0, 0, team: Team.Horde);
        Creature thurloga = rig.Single(NpcPrimalistThurloga);

        uint Line() => Assert.Single(AvCollectorGossip.Hello(rig.Match, thurloga, rig.Player, Team.Horde, [], _ => RankNeutral).Items).BroadcastTextId;
        Assert.Equal(AvCollectorGossip.GossipThurlogaBoss1, Line());
        rig.Match.AddChallengeCounter(Team.Horde, ChallengeBloodWorldBoss, 100);
        Assert.Equal(AvCollectorGossip.GossipThurlogaBoss2, Line());
        rig.Match.AddChallengeCounter(Team.Horde, ChallengeBloodWorldBoss, 60);
        Assert.Equal(AvCollectorGossip.GossipThurlogaBoss3, Line());

        Assert.Equal(6100u, AvCollectorGossip.Select(rig.Match, thurloga, Team.Horde, GossipActionInfoDef + ChallengeBloodWorldBoss + 200 + 2, _ => false).NpcTextId);
    }
}
