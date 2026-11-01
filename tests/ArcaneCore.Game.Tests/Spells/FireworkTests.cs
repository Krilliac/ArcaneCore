using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The Lunar Festival firework chains end to end through the real spell system, with spell_script_target rows loaded (the vmangos 5875
/// rows). The spells are shaped like their Spell.dbc rows (vmangos spell_template):
/// <list type="bullet">
/// <item>Rocket / Rocket BIG (26344-26356, 30163): effect 76 SUMMON_OBJECT_WILD at TARGET_LOCATION_SCRIPT_NEAR_CASTER (46); the row names the firework
/// creature that casts it, so the rocket object appears where that creature stands.</item>
/// <item>Cluster Rocket N/D/SE/SW/U (26300-26303, 26357, ...) and the BIG clusters (26483-26487, ...): the same effect with the cluster creature's rows.</item>
/// <item>Launcher spells (26286-26293, ...): effect 86 ACTIVATE_OBJECT (action 1, custom animation 0) at target 40 on the launcher object and effect 41
/// SUMMON_WILD of the firework creature at target 46 (the launcher's position); the two effects come in either order.</item>
/// </list>
/// References: vmangos Spell.cpp:446-600 (CheckScriptTargeting), SpellEffects.cpp EffectSummonObjectWild, EffectSummonWild, EffectActivateObject.
/// </summary>
public sealed class FireworkTests
{
    private const uint RocketBlueObject = 180854, RocketBigBlueObject = 180861, RocketPurpleObject = 180856;
    private const uint Launcher = 180868;
    private const uint SmallBlueNpc = 15879, SmallPurpleNpc = 15881, SmallRedNpc = 15882, LargeBlueNpc = 15885;
    private const uint BlueClusterNpc = 15872, LargeBlueClusterNpc = 15911, LuckyClusterNpc = 15918;
    private const uint SmallRedLauncher = 26286, SmallBlueLauncherReversed = 26291;
    private const uint ScriptNearCasterLocation = 46, ScriptNearCasterObject = 40;

    private static SpellInfo RocketSpell(uint id, uint objectEntry, bool cluster) => Spell(id,
        Effect(SpellEffectName.SummonObjectWild, 1, (SpellImplicitTarget)ScriptNearCasterLocation, misc: (int)objectEntry)) with
    {
        // Spell.dbc: rocket range index 11 (anywhere), cluster index 5; duration index 28.
        RangeIndex = cluster ? 5u : 11u,
        Range = cluster ? new SpellRange(0, 5) : new SpellRange(0, 100_000f),
        Duration = new SpellDuration(3_000, 0, 3_000),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo LauncherSpell(uint id, uint npcEntry, bool activateFirst)
    {
        SpellEffectInfo activate = Effect(SpellEffectName.ActivateObject, 0, (SpellImplicitTarget)ScriptNearCasterObject, misc: 1);
        SpellEffectInfo summon = Effect(SpellEffectName.SummonWild, 1, (SpellImplicitTarget)ScriptNearCasterLocation, misc: (int)npcEntry);
        return Spell(id, activateFirst ? new[] { activate, summon } : new[] { summon, activate }) with
        {
            RangeIndex = 5,
            Range = new SpellRange(0, 5),
            Duration = new SpellDuration(3_000, 0, 3_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
    }

    private sealed class Rig : IDisposable
    {
        public Rig(SpellInfo[] spells, SpellStore.ScriptTarget[] rows, uint[] npcEntries, uint[] objectEntries)
        {
            Kit = new PetTestKit(spells, extraTemplates: [.. npcEntries.Select(entry => CreatureTestKit.Template(entry, b => b.Faction = 35))]);
            Objects = new GameObjectMapSystem(Kit.Map, new GameObjectContent(
                [.. objectEntries.Select(entry => GameObjectTestKit.GoTemplate(entry, GameObjectType.Goober))], [], [], [], []));
            Kit.Map.AddUpdater(Objects);
            Kit.Spells.System.Store = new SpellStore(Kit.Spells.Store.All, [], [], rows);
            (Player, Session) = Kit.AddPlayer(1);
            Kit.Spells.World.RunTick(0);
            Session.Clear();
        }

        public PetTestKit Kit { get; }
        public GameObjectMapSystem Objects { get; }
        public Player Player { get; }
        public FakeSession Session { get; }

        /// <summary>The firework creature that casts the rocket spells, standing at (x, y).</summary>
        public Creature SpawnNpc(uint entry, float x, float y) => Kit.Creatures.SpawnSummoned(
            Kit.Content.FindTemplate(entry)!, HighGuid.Unit, creature =>
            {
                creature.Summon = new SummonLinks(SummonKind.Wild, Player.Guid, 1, TotemSlots.None, 0);
                return new CreatureHome(x, y, Player.Z, 0);
            });

        public GameObject SpawnObject(uint entry, float x, float y) => Objects.Summon(entry, x, y, Player.Z, 0)!;

        public IEnumerable<Creature> NpcsOf(uint entry) => Kit.Creatures.Creatures.Where(c => c.Entry == entry);

        public SpellCastResult CastFrom(Unit caster, uint spell)
            => Kit.Spells.System.CastSpell(caster, spell, SpellCastTargets.ForSelf(), triggered: true);

        public void Dispose() => Kit.Dispose();
    }

    private static SpellStore.ScriptTarget Row(uint spell, uint type, uint entry) => new(spell, type, entry, 0);

    // Rocket BLUE (26344), Rocket BLUE BIG (26351), Copy of Rocket PURPLE (30163).
    [Theory]
    [InlineData(26344u, RocketBlueObject, SmallBlueNpc)]
    [InlineData(26351u, RocketBigBlueObject, LargeBlueNpc)]
    [InlineData(30163u, RocketPurpleObject, SmallPurpleNpc)]
    public void Rocket_PutsTheRocketObjectWhereTheListedFireworkCreatureStands(uint spell, uint objectEntry, uint npcEntry)
    {
        using var rig = new Rig([RocketSpell(spell, objectEntry, cluster: false)], [Row(spell, 1, npcEntry)], [npcEntry], [objectEntry]);
        Creature npc = rig.SpawnNpc(npcEntry, 6, 4);

        Assert.Equal(SpellCastResult.CastOk, rig.CastFrom(npc, spell));

        GameObject rocket = Assert.Single(rig.Objects.GameObjects, go => go.Entry == objectEntry);
        Assert.Equal((npc.X, npc.Y, npc.Z), (rocket.X, rocket.Y, rocket.Z));
    }

    [Fact]
    public void Rocket_GoesToTheListedCreature_NotToTheCaster()
    {
        // CheckScriptTargeting takes the row's creature only: a listed creature elsewhere in range is where the rocket goes.
        using var rig = new Rig([RocketSpell(26344, RocketBlueObject, cluster: false)], [Row(26344, 1, SmallBlueNpc)],
            [SmallBlueNpc, SmallRedNpc], [RocketBlueObject]);
        Creature listed = rig.SpawnNpc(SmallBlueNpc, 20, 0);
        Creature other = rig.SpawnNpc(SmallRedNpc, 3, 0);

        Assert.Equal(SpellCastResult.CastOk, rig.CastFrom(other, 26344));

        GameObject rocket = Assert.Single(rig.Objects.GameObjects, go => go.Entry == RocketBlueObject);
        Assert.Equal((listed.X, listed.Y), (rocket.X, rocket.Y));
    }

    // Cluster Rocket BLUE N, SE, D, SW and U share the cluster creature's rows.
    [Theory]
    [InlineData(26300u)]
    [InlineData(26302u)]
    [InlineData(26301u)]
    [InlineData(26303u)]
    [InlineData(26357u)]
    public void ClusterRocket_PutsTheRocketObjectAtTheClusterCreature(uint spell)
    {
        using var rig = new Rig([RocketSpell(spell, RocketBlueObject, cluster: true)], [Row(spell, 1, BlueClusterNpc)],
            [BlueClusterNpc], [RocketBlueObject]);
        Creature cluster = rig.SpawnNpc(BlueClusterNpc, 8, 2);

        Assert.Equal(SpellCastResult.CastOk, rig.CastFrom(cluster, spell));

        GameObject rocket = Assert.Single(rig.Objects.GameObjects, go => go.Entry == RocketBlueObject);
        Assert.Equal((cluster.X, cluster.Y, cluster.Z), (rocket.X, rocket.Y, rocket.Z));
    }

    [Fact]
    public void ClusterRocket_FollowsTheClusterCreatureToEachBurstPoint()
    {
        // npc_pats_firework_guyAI moves the creature (NearTeleport) before every burst; the object lands at the new point each time.
        using var rig = new Rig([RocketSpell(26300, RocketBlueObject, cluster: true), RocketSpell(26357, RocketBlueObject, cluster: true)],
            [Row(26300, 1, BlueClusterNpc), Row(26357, 1, BlueClusterNpc)], [BlueClusterNpc], [RocketBlueObject]);
        Creature cluster = rig.SpawnNpc(BlueClusterNpc, 8, 2);
        float groundZ = cluster.Z;
        Assert.Equal(SpellCastResult.CastOk, rig.CastFrom(cluster, 26300));

        rig.Kit.Creatures.NearTeleport(cluster, 8, 2, groundZ + 8, 0);
        Assert.Equal(SpellCastResult.CastOk, rig.CastFrom(cluster, 26357));

        GameObject[] rockets = [.. rig.Objects.GameObjects.Where(go => go.Entry == RocketBlueObject).OrderBy(go => go.Z)];
        Assert.Equal(2, rockets.Length);
        Assert.Equal(groundZ, rockets[0].Z, 2);
        Assert.Equal(groundZ + 8, rockets[1].Z, 2);
    }

    // Cluster Rocket BLUE BIG D (26483), N (26484) and U (26487) of the Large Blue Firework Cluster creature.
    [Theory]
    [InlineData(26483u)]
    [InlineData(26484u)]
    [InlineData(26487u)]
    public void ClusterBigRocket_PutsTheBigRocketObjectAtTheLargeClusterCreature(uint spell)
    {
        using var rig = new Rig([RocketSpell(spell, RocketBigBlueObject, cluster: true)], [Row(spell, 1, LargeBlueClusterNpc)],
            [LargeBlueClusterNpc], [RocketBigBlueObject]);
        Creature cluster = rig.SpawnNpc(LargeBlueClusterNpc, 5, 5);

        Assert.Equal(SpellCastResult.CastOk, rig.CastFrom(cluster, spell));

        GameObject rocket = Assert.Single(rig.Objects.GameObjects, go => go.Entry == RocketBigBlueObject);
        Assert.Equal((cluster.X, cluster.Y, cluster.Z), (rocket.X, rocket.Y, rocket.Z));
    }

    [Fact]
    public void LuckyRocketCluster_ReusesTheLargeClusterSpellsThroughItsOwnRow()
    {
        // vmangos npcs_special.cpp Fireworks table: entry 15918 casts 26487 / 26484 / 26483 among others, so those spells also list 15918.
        using var rig = new Rig([RocketSpell(26484, RocketBigBlueObject, cluster: true)],
            [Row(26484, 1, LargeBlueClusterNpc), Row(26484, 1, LuckyClusterNpc)], [LuckyClusterNpc], [RocketBigBlueObject]);
        Creature lucky = rig.SpawnNpc(LuckyClusterNpc, 4, 1);

        Assert.Equal(SpellCastResult.CastOk, rig.CastFrom(lucky, 26484));

        GameObject rocket = Assert.Single(rig.Objects.GameObjects, go => go.Entry == RocketBigBlueObject);
        Assert.Equal((lucky.X, lucky.Y, lucky.Z), (rocket.X, rocket.Y, rocket.Z));
    }

    // Small Red Rocket (26286: activate, then summon) and Small Blue Rocket (26291: summon, then activate).
    [Theory]
    [InlineData(SmallRedLauncher, SmallRedNpc, true)]
    [InlineData(SmallBlueLauncherReversed, SmallBlueNpc, false)]
    public void Launcher_AnimatesTheLauncherObject_AndSummonsTheFireworkCreatureAtIt(uint spell, uint npcEntry, bool activateFirst)
    {
        using var rig = new Rig([LauncherSpell(spell, npcEntry, activateFirst)], [Row(spell, 0, Launcher)], [npcEntry], [Launcher]);
        GameObject launcher = rig.SpawnObject(Launcher, 2, 1);
        rig.Kit.Spells.World.RunTick(0);
        rig.Session.Clear();

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.Cast(rig.Player, spell));

        Assert.Contains(rig.Session.Sent, packet => packet.Opcode == WorldOpcode.SmsgGameobjectCustomAnim);
        Creature firework = Assert.Single(rig.NpcsOf(npcEntry));
        Assert.Equal((launcher.X, launcher.Y, launcher.Z), (firework.X, firework.Y, firework.Z));
        Assert.Equal(spell, firework.GetUInt32(UpdateFields.UnitCreatedBySpell));
    }

    [Fact]
    public void Launcher_UsesTheNearestListedLauncher_AndIgnoresOnesOutOfRange()
    {
        using var rig = new Rig([LauncherSpell(SmallRedLauncher, SmallRedNpc, activateFirst: true)], [Row(SmallRedLauncher, 0, Launcher)],
            [SmallRedNpc], [Launcher]);
        GameObject far = rig.SpawnObject(Launcher, 40, 0);
        GameObject near = rig.SpawnObject(Launcher, 3, 0);

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.Cast(rig.Player, SmallRedLauncher));

        Creature firework = Assert.Single(rig.NpcsOf(SmallRedNpc));
        Assert.Equal((near.X, near.Y), (firework.X, firework.Y));
        Assert.NotEqual(far.X, firework.X);
    }

    [Fact]
    public void Launcher_WithNoListedLauncherInRange_SummonsNothing()
    {
        using var rig = new Rig([LauncherSpell(SmallRedLauncher, SmallRedNpc, activateFirst: true)], [Row(SmallRedLauncher, 0, Launcher)],
            [SmallRedNpc], [Launcher]);
        rig.SpawnObject(Launcher, 60, 0);

        rig.Kit.Cast(rig.Player, SmallRedLauncher);

        Assert.Empty(rig.NpcsOf(SmallRedNpc));
        Assert.DoesNotContain(rig.Session.Sent, packet => packet.Opcode == WorldOpcode.SmsgGameobjectCustomAnim);
    }
}
