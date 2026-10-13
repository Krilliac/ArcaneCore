using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Uldaman;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>mob_archaeras_add (spec 70-mob_archaeras_add): the dormant stone minions of Archaedas, through the real engine paths.</summary>
public sealed class ArchaerasAddTests
{
    private const uint Stoned = 10255, Visual = 10254, Dwarf = 10259, Whirlwind = 17207, Trample = 5568, Reconstruct = 10260, GuardsDestroyed = 10666;
    private const uint Guardian = 7076, Hallshaper = 7077, Custodian = 7309, Warder = 10120, Archaedas = 2748;

    private static CreatureAiEvent Row(uint id, uint entry, EventAiEventType type, uint flags, uint spell, int target = 0, int castFlags = 0,
        int p1 = 0, int p2 = 0, int p3 = 0, int p4 = 0)
        => new()
        {
            Id = id, CreatureId = entry, EventType = (byte)type, Chance = 100, Flags = flags, Param1 = p1, Param2 = p2, Param3 = p3, Param4 = p4,
            Action1 = new CreatureAiAction((byte)EventAiActionType.Cast, (int)spell, target, castFlags),
        };

    /// <summary>The creature_ai_scripts rows of the fixture for the four entries (spec 5).</summary>
    private static CreatureAiEvent[] Rows()
    {
        var rows = new List<CreatureAiEvent>();
        uint id = 1;
        foreach (uint entry in new[] { Guardian, Hallshaper, Custodian, Warder })
        {
            rows.Add(Row(id++, entry, EventAiEventType.Spawned, 0, Stoned));
            rows.Add(Row(id++, entry, EventAiEventType.ReachedHome, 0, Stoned));
            rows.Add(Row(id++, entry, EventAiEventType.Death, 0, GuardsDestroyed, castFlags: 2));
        }

        rows.Add(Row(id++, Guardian, EventAiEventType.TimerInCombat, 1025, Whirlwind, p1: 5000, p2: 9000, p3: 7000, p4: 14000));
        rows.Add(Row(id, Warder, EventAiEventType.TimerInCombat, 1025, Trample, p1: 4000, p2: 10000, p3: 13000, p4: 22000));
        return [.. rows];
    }

    private static CreatureTemplate AddTemplate(uint entry) => Template(entry, t =>
    {
        t.Name = $"add {entry}";
        t.MinLevel = 44;
        t.MaxLevel = 45;
        t.Faction = entry == Warder ? 416u : 470u;
        t.UnitFlags = 32768;
        t.AIName = "EventAI";
        t.MinLevelHealth = t.MaxLevelHealth = 100_000;
    }) with { ScriptName = ArchaerasAddAI.ScriptName };

    private static DungeonNpcRig Rig(params (uint Guid, uint Entry, float Dx)[] minions)
    {
        uint[] entries = [.. minions.Select(m => m.Entry).Distinct()];
        var templates = entries.Select(AddTemplate).Append(Template(Archaedas, t => t.MinLevelHealth = t.MaxLevelHealth = 100_000)).ToList();
        var spawns = minions.Select(m => Spawn(m.Guid, m.Entry, DungeonNpcRig.EntranceX + m.Dx, DungeonNpcRig.EntranceY, DungeonNpcRig.EntranceZ, InstanceFixture.Dungeon))
            .Append(Spawn(9000, Archaedas, DungeonNpcRig.EntranceX + 12, DungeonNpcRig.EntranceY + 12, DungeonNpcRig.EntranceZ, InstanceFixture.Dungeon));
        return new DungeonNpcRig(map => new UldamanInstance(map), templates, spawns, new CreatureAiContent(Rows(), []));
    }

    private static void StartArchaedasFight(DungeonNpcRig rig)
    {
        Creature archaedas = rig.Creature(Archaedas);
        archaedas.UnitFlags &= ~UnitFlags.NotSelectable;
        Assert.True(rig.Creatures.AttackStart(archaedas, rig.Player));
        Assert.Equal(EncounterState.InProgress, rig.Data.GetData(UldamanInstance.TypeArchaedas));
    }

    [Fact]
    public void TheFourEntries_ResolveAsAScriptNameAi()
    {
        var content = new CreatureContent([.. new[] { Guardian, Hallshaper, Custodian, Warder }.Select(AddTemplate)], [], [], [], []);
        var catalog = new ScriptCatalog(new CreatureAiFactory(), content);
        foreach (uint entry in new[] { Guardian, Hallshaper, Custodian, Warder })
        {
            ScriptResolution r = catalog.Resolve(entry, 70);
            Assert.True(r.ScriptNameResolved);
            Assert.Equal(ScriptKind.ScriptName, r.Kind);
        }
    }

    [Fact]
    public void AFreshMinion_IsStonedInertAndIgnoresAPlayerNextToIt_WhileAnAwakeOneAggroes()
    {
        using DungeonNpcRig rig = Rig((1, Guardian, 3), (2, Hallshaper, -3), (3, Custodian, 2), (4, Warder, -2));
        rig.Run(10_000);
        foreach (uint entry in new[] { Guardian, Hallshaper, Custodian, Warder })
        {
            Creature c = rig.Creature(entry);
            Assert.IsAssignableFrom<StonedEventAI>(c.AI);
            Assert.Contains((c, Stoned), rig.Spells.Auras);
            Assert.Equal(StonedDormancy.DormantFlags, c.UnitFlags & StonedDormancy.DormantFlags);
            Assert.True(c.AiImmobilized);
            Assert.Null(c.Combat.Victim);
            Assert.False(rig.Map.Combat.Hooks.CanAttack(rig.Player, c));
            Assert.Single(rig.Spells.Of(c, Stoned)); // the SPAWNED row
        }

        // control: without the aura the same minion is attackable and free to move (the check can fail)
        Creature guardian = rig.Creature(Guardian);
        rig.Spells.RemoveAuras(guardian, Stoned);
        rig.Run(200);
        Assert.True(rig.Map.Combat.Hooks.CanAttack(rig.Player, guardian));
        Assert.False(guardian.AiImmobilized);
    }

    [Fact]
    public void ArchaedasAwaken_WakesTheMinionAtOnce_AndTheSpellHitAddsNoSecondSequence()
    {
        using DungeonNpcRig rig = Rig((1, Custodian, 6));
        StartArchaedasFight(rig);
        Creature custodian = rig.Creature(Custodian);
        Assert.Null(custodian.Combat.Victim);
        rig.Run(10_100); // ArchaedasAi: first Awaken Earthen Dwarf after 10 s
        Assert.Single(rig.Spells.Of(rig.Creature(Archaedas), Dwarf));
        Assert.Same(rig.Player, custodian.Combat.Victim);
        Assert.DoesNotContain((custodian, Stoned), rig.Spells.Auras);
        Assert.Equal(UnitFlags.None, custodian.UnitFlags & StonedDormancy.DormantFlags);
        Assert.False(((ArchaerasAddAI)custodian.AI!).IsWaking);

        rig.Spells.Hit(rig.Creature(Archaedas), custodian, Dwarf); // a later hit on an awake minion
        rig.Run(6000);
        Assert.Empty(rig.Spells.Of(custodian, Visual));
    }

    [Fact]
    public void ASpellHitWithoutAnExternalAwaken_CastsTheVisualAfterOneSecondAndJoinsAfterFour_WhenTheEncounterIsInProgress()
    {
        using DungeonNpcRig rig = Rig((1, Hallshaper, 6));
        rig.Data.SetData(UldamanInstance.TypeArchaedas, EncounterState.InProgress);
        Creature minion = rig.Creature(Hallshaper);
        rig.Spells.Hit(rig.Creature(Archaedas), minion, Dwarf);
        Assert.True(((ArchaerasAddAI)minion.AI!).IsWaking);
        rig.Run(950);
        Assert.Empty(rig.Spells.Of(minion, Visual));
        rig.Run(100);
        Assert.Single(rig.Spells.Of(minion, Visual));
        Assert.Contains((minion, Stoned), rig.Spells.Auras);
        rig.Run(2800); // 3.85 s in total
        Assert.Null(minion.Combat.Victim);
        rig.Run(300); // 4.15 s
        Assert.Same(rig.Player, minion.Combat.Victim);
        Assert.DoesNotContain((minion, Stoned), rig.Spells.Auras);
    }

    [Fact]
    public void ASpellHit_WhenTheEncounterIsNotInProgress_ReturnsTheMinionToDormancy()
    {
        using DungeonNpcRig rig = Rig((1, Hallshaper, 6));
        Creature minion = rig.Creature(Hallshaper);
        rig.Spells.Hit(rig.Creature(Archaedas), minion, Dwarf);
        rig.Run(5000);
        Assert.Single(rig.Spells.Of(minion, Visual));
        Assert.Contains((minion, Stoned), rig.Spells.Auras);
        Assert.Null(minion.Combat.Victim);
        Assert.False(((ArchaerasAddAI)minion.AI!).IsWaking);
    }

    [Fact]
    public void AwakenVaultWarder_JoinsAtOnce_ButNeverWakesAHallwayWarder()
    {
        using DungeonNpcRig rig = Rig((7000322, Warder, 5), (7000320, Warder, -5));
        Creature room = rig.BySpawn(7000322), hallway = rig.BySpawn(7000320);
        Creature archaedas = rig.Creature(Archaedas);
        rig.Spells.Hit(archaedas, hallway, 10258);
        rig.Spells.Hit(archaedas, room, 10258);
        rig.Run(100);
        Assert.Same(rig.Player, room.Combat.Victim);
        Assert.Null(hallway.Combat.Victim);
        Assert.Contains((hallway, Stoned), rig.Spells.Auras);

        // Archaedas' own awaken of a hallway pick wakes a sleeping room warder instead; with none left it wakes nothing
        ((ArchaedasAi)archaedas.AI!).Awaken(hallway);
        Assert.Contains((hallway, Stoned), rig.Spells.Auras);
        Assert.True(UldamanInstance.IsHallwayWarder(hallway));
        Assert.False(UldamanInstance.IsHallwayWarder(room));
    }

    [Theory]
    [InlineData(true, 7000322u)]
    [InlineData(true, 7000323u)]
    [InlineData(false, 7000322u)]
    [InlineData(false, 7000323u)]
    public void ACastWithAHallwayPickAndARoomPick_WakesBothRoomWarders_AndNeverTheHallwayWarder(bool hallwayFirst, uint roomPickGuid)
    {
        // Spec 70-mob_archaeras_add 11.4 / check 11. 10258 picks two of the four warders; the per-target script runs once per pick. The hallway
        // pick's substitute must not swallow the room pick (it used to: the nearest warder was woken twice and the other one slept on).
        using DungeonNpcRig rig = Rig((7000320, Warder, -30), (7000322, Warder, 5), (7000323, Warder, -5));
        StartArchaedasFight(rig);
        Creature hallway = rig.BySpawn(7000320), nearRoom = rig.BySpawn(7000322), farRoom = rig.BySpawn(7000323);
        var ai = (ArchaedasAi)rig.Creature(Archaedas).AI!;
        Creature roomPick = rig.BySpawn(roomPickGuid);
        object cast = new();
        Assert.Contains((nearRoom, Stoned), rig.Spells.Auras);
        Assert.Contains((farRoom, Stoned), rig.Spells.Auras);

        if (hallwayFirst)
        {
            ai.Awaken(hallway, cast);
            ai.Awaken(roomPick, cast);
        }
        else
        {
            ai.Awaken(roomPick, cast);
            ai.Awaken(hallway, cast);
        }

        rig.Run(100);
        Assert.Same(rig.Player, nearRoom.Combat.Victim);
        Assert.Same(rig.Player, farRoom.Combat.Victim);
        Assert.DoesNotContain((nearRoom, Stoned), rig.Spells.Auras);
        Assert.DoesNotContain((farRoom, Stoned), rig.Spells.Auras);
        Assert.Null(hallway.Combat.Victim);
        Assert.Contains((hallway, Stoned), rig.Spells.Auras);
    }

    [Fact]
    public void TwoHallwayPicksInOneCast_WakeTheTwoRoomWarders_NotTheSameOneTwice()
    {
        using DungeonNpcRig rig = Rig((7000320, Warder, -30), (7000321, Warder, -31), (7000322, Warder, 5), (7000323, Warder, -5));
        StartArchaedasFight(rig);
        var ai = (ArchaedasAi)rig.Creature(Archaedas).AI!;
        object cast = new();
        ai.Awaken(rig.BySpawn(7000320), cast);
        ai.Awaken(rig.BySpawn(7000321), cast);
        rig.Run(100);
        Assert.Same(rig.Player, rig.BySpawn(7000322).Combat.Victim);
        Assert.Same(rig.Player, rig.BySpawn(7000323).Combat.Victim);
        Assert.Null(rig.BySpawn(7000320).Combat.Victim);
        Assert.Null(rig.BySpawn(7000321).Combat.Victim);
    }

    [Fact]
    public void AnAwakeGuardianCastsWhirlwindBetween5And9Seconds_ThenEvery7To14()
    {
        using DungeonNpcRig rig = Rig((1, Guardian, 3));
        Creature g = rig.Creature(Guardian);
        rig.Run(200);
        rig.Spells.RemoveAuras(g, Stoned);
        Assert.True(g.AI!.AttackStart(rig.Player));
        long start = rig.Spells.NowMs;
        rig.Run(40_000);
        long[] at = [.. rig.Spells.Of(g, Whirlwind).Select(c => c.AtMs - start)];
        Assert.True(at.Length >= 3, string.Join(',', at));
        Assert.InRange(at[0], 4900, 9600);
        Assert.InRange(at[1] - at[0], 6900, 14600);
        Assert.InRange(at[2] - at[1], 6900, 14600);
    }

    [Fact]
    public void AnAwakeVaultWarderCastsTrampleBetween4And10Seconds_ThenEvery13To22()
    {
        using DungeonNpcRig rig = Rig((1, Warder, 3));
        Creature w = rig.Creature(Warder);
        rig.Run(200);
        rig.Spells.RemoveAuras(w, Stoned);
        Assert.True(w.AI!.AttackStart(rig.Player));
        long start = rig.Spells.NowMs;
        rig.Run(60_000);
        long[] at = [.. rig.Spells.Of(w, Trample).Select(c => c.AtMs - start)];
        Assert.True(at.Length >= 2, string.Join(',', at));
        Assert.InRange(at[0], 3900, 10600);
        Assert.InRange(at[1] - at[0], 12900, 22600);
    }

    /// <summary>Runs <paramref name="ms"/> with Archaedas pinned at <paramref name="health"/>: the 5 s out-of-combat regeneration would otherwise heal him past the threshold mid-run.</summary>
    private static void HoldHealth(DungeonNpcRig rig, Creature archaedas, uint health, uint ms)
    {
        for (uint done = 0; done < ms; done += 50)
        {
            archaedas.Health = health;
            rig.Tick();
        }
    }

    [Fact]
    public void TheCustodianReconstructsArchaedasEveryTenSeconds_OnlyBelowHalfHealth()
    {
        using DungeonNpcRig rig = Rig((1, Custodian, 3));
        Creature c = rig.Creature(Custodian);
        Creature archaedas = rig.Creature(Archaedas);
        rig.Run(200);
        rig.Spells.RemoveAuras(c, Stoned);
        Assert.True(c.AI!.AttackStart(rig.Player));
        HoldHealth(rig, archaedas, archaedas.MaxHealth / 2, 25_000); // exactly 50 %: not below
        Assert.Empty(rig.Spells.Of(c, Reconstruct));

        long start = rig.Spells.NowMs;
        HoldHealth(rig, archaedas, 1, 30_000);
        long[] at = [.. rig.Spells.Of(c, Reconstruct).Select(x => x.AtMs - start)];
        Assert.True(at.Length >= 2, string.Join(',', at));
        Assert.InRange(at[1] - at[0], 9900, 10100);
    }

    [Fact]
    public void EveryMinionCastsEarthenGuardsDestroyedTriggeredOnItsOwnDeath()
    {
        using DungeonNpcRig rig = Rig((1, Guardian, 3), (2, Hallshaper, -3), (3, Custodian, 2), (4, Warder, -2));
        rig.Run(200);
        foreach (uint entry in new[] { Guardian, Hallshaper, Custodian, Warder })
        {
            Creature c = rig.Creature(entry);
            rig.Spells.RemoveAuras(c, Stoned);
            rig.Map.Combat.Kill(rig.Player, c);
            Assert.Contains(rig.Spells.Of(c, GuardsDestroyed), x => x.Triggered && ReferenceEquals(x.Target, c));
        }
    }

    [Fact]
    public void AnAwakeMinionWhoseTargetDies_RetargetsInsteadOfWalkingHome_AndEvadesWhenNothingIsLeft()
    {
        using DungeonNpcRig rig = Rig((1, Hallshaper, 2));
        Creature m = rig.Creature(Hallshaper);
        Player second = rig.AddPlayerAt(2, DungeonNpcRig.EntranceX + 3, DungeonNpcRig.EntranceY);
        rig.Run(200);
        rig.Spells.RemoveAuras(m, Stoned);
        Assert.True(m.AI!.AttackStart(rig.Player));
        rig.Map.Combat.Kill(m, rig.Player);
        rig.Run(1500);
        Assert.Same(second, m.Combat.Victim);

        rig.Map.Combat.Kill(m, second);
        rig.Run(15_000); // nothing is left: it evades, walks home and the REACHED_HOME row casts Stoned again
        Assert.Null(m.Combat.Victim);
        Assert.Contains((m, Stoned), rig.Spells.Auras);
        Assert.Equal(StonedDormancy.DormantFlags, m.UnitFlags & StonedDormancy.DormantFlags);
    }

    [Fact]
    public void AfterAWipe_AnAwakeMinionThatReachesHomeIsStonedAndDormantAgain()
    {
        // Spec 70-mob_archaeras_add check 9: the encounter resets, the awake minion is sent home and the REACHED_HOME row casts Stoned again.
        using DungeonNpcRig rig = Rig((1, Guardian, 3));
        Creature g = rig.Creature(Guardian);
        rig.Run(200);
        rig.Spells.RemoveAuras(g, Stoned);
        Assert.True(g.AI!.AttackStart(rig.Player));
        rig.Run(200);
        Assert.DoesNotContain((g, Stoned), rig.Spells.Auras);
        Assert.Equal(UnitFlags.None, g.UnitFlags & StonedDormancy.DormantFlags);

        rig.Creatures.NearTeleport(g, g.X + 8f, g.Y, g.Z, g.Orientation); // pulled away from its post
        rig.Creatures.EnterEvadeMode(g);
        rig.Run(15_000);
        Assert.Null(g.Combat.Victim);
        Assert.Contains((g, Stoned), rig.Spells.Auras);
        Assert.Equal(StonedDormancy.DormantFlags, g.UnitFlags & StonedDormancy.DormantFlags);
        Assert.True(g.AiImmobilized);
        Assert.False(rig.Map.Combat.Hooks.CanAttack(rig.Player, g));
    }

    [WorldFixtureFact]
    public void TheEmbeddedRows_AreTheFixtureRows()
    {
        CreatureAiEvent[] fixture = WorldFixtureRows.AiEvents(Guardian, Hallshaper, Custodian, Warder);
        CreatureAiEvent[] mine = Rows();
        Assert.Equal(mine.Length, fixture.Length);
        string Key(CreatureAiEvent e) => $"{e.CreatureId}/{e.EventType}/{e.Flags}/{e.Param1},{e.Param2},{e.Param3},{e.Param4}/{e.Action1}";
        Assert.Equal(mine.Select(Key).Order(), fixture.Select(Key).Order());
    }
}
