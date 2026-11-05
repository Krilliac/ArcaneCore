using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The periodic DUMMY auras of <c>GenericDummyAuraCases.cs</c> that name a tick period: Melodious Rapture Visual 21051 (2 s), Frostwolf Aura 21827
/// and Alterac Ram Aura 21863 (2 s each), Intoxicating Venom 24596 (1 s). Each test pins the tick (nothing before the period, the effect once it
/// has passed), so a registration that lost its period or its tick fails. vmangos Aura::PeriodicDummyTick, SpellAuras.cpp:6446-6457 (21051),
/// :6475-6512 (21827, 21863) and :6513-6523 (24596).
/// </summary>
public sealed class GenericDummyAuraTickTests
{
    private const uint Disarm = GenericDummyAuraModule.Disarm, FallDown = GenericDummyAuraModule.FallDown;

    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo DummyAura(uint id, SpellImplicitTarget target = SpellImplicitTarget.UnitCaster, int durationMs = 60_000) =>
        Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, target, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(durationMs, 0, durationMs),
        });

    private static SpellTestKit MakeKit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    /// <summary><see cref="Random.Next(int)"/> that always answers one value, so the 7% / 7% rolls of Intoxicating Venom are decided by the test.</summary>
    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int maxValue) => value;
    }

    private sealed class MapUnits : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(3, true, false)]
    [InlineData(10, false, true)]
    [InlineData(50, false, false)]
    public void IntoxicatingVenom_RollsEverySecondInCombat_SevenPercentDisarmAndSevenPercentFallDown(int roll, bool disarmed, bool fellDown)
    {
        using SpellTestKit kit = MakeKit(DummyAura(GenericDummyAuraModule.IntoxicatingVenom), DummyAura(Disarm), DummyAura(FallDown));
        kit.System.Random = new FixedRandom(roll);
        (Player player, _) = kit.AddPlayer(1);
        player.UnitFlags |= UnitFlags.InCombat;

        kit.System.CastSpell(player, GenericDummyAuraModule.IntoxicatingVenom, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(900);
        Assert.False(kit.System.HasAura(player, Disarm));
        Assert.False(kit.System.HasAura(player, FallDown));

        kit.Advance(600); // the 1 s tick has passed
        Assert.Equal(disarmed, kit.System.HasAura(player, Disarm));
        Assert.Equal(fellDown, kit.System.HasAura(player, FallDown));
    }

    [Fact]
    public void IntoxicatingVenom_DoesNothingOutOfCombat()
    {
        using SpellTestKit kit = MakeKit(DummyAura(GenericDummyAuraModule.IntoxicatingVenom), DummyAura(Disarm), DummyAura(FallDown));
        kit.System.Random = new FixedRandom(0);
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, GenericDummyAuraModule.IntoxicatingVenom, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(3_000);

        Assert.False(kit.System.HasAura(player, Disarm));
        Assert.False(kit.System.HasAura(player, FallDown));
    }

    [Theory]
    [InlineData(GenericDummyAuraModule.FrostwolfAura)]
    [InlineData(GenericDummyAuraModule.AlteracRamAura)]
    public void MountAura_EndsAtTheTwoSecondTickWhenItsCasterCreatureIsGone(uint spellId)
    {
        // The caster here is the player, not a creature: the tick treats the mount as gone and ends the aura (holder.Duration = 1).
        using SpellTestKit kit = MakeKit(DummyAura(spellId));
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(1_500);
        Assert.True(kit.System.HasAura(player, spellId));

        kit.Advance(1_000); // the 2 s tick has passed
        Assert.False(kit.System.HasAura(player, spellId));
    }

    private sealed class Scene : IDisposable
    {
        public Scene()
        {
            Kit = MakeKit(DummyAura(GenericDummyAuraModule.MelodiousRaptureVisual, SpellImplicitTarget.Unit));
            Kit.System.Units = new MapUnits();
            Kit.System.CombatRules = SpellCombatRules.Neutral;
            (Player, _) = Kit.AddPlayer(1);
            Map map = Kit.World.GetMap(0);
            CreatureTemplate[] templates = [CreatureTestKit.Template(13016, b =>
            {
                b.Faction = 14;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
            })];
            Creatures = new CreatureMapSystem(map, CreatureTestKit.Content(templates, [CreatureTestKit.Spawn(1, 13016, 3f, 0, Player.Z)]),
                random: new Random(1));
            map.AddUpdater(Creatures);
            Kit.World.RunTick(0);
            Rat = Assert.Single(Creatures.Creatures);
        }

        public SpellTestKit Kit { get; }

        public Player Player { get; }

        public CreatureMapSystem Creatures { get; }

        public Creature Rat { get; }

        /// <summary>Runs the spell clock and the map clock together, in 100 ms steps.</summary>
        public void Run(uint ms)
        {
            for (uint done = 0; done < ms; done += 100)
            {
                Kit.Advance(100);
                Kit.World.RunTick(100);
            }
        }

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void MelodiousRaptureVisual_DespawnsARatThatIsNotFollowing_AtTheTwoSecondTick()
    {
        using var scene = new Scene();
        scene.Kit.System.CastSpell(scene.Player, GenericDummyAuraModule.MelodiousRaptureVisual,
            SpellCastTargets.ForUnit(scene.Rat.Guid), triggered: true);

        scene.Run(1_500);
        Assert.True(scene.Rat.IsAlive);

        scene.Run(1_000); // the 2 s tick, then the 1 ms despawn delay
        Assert.False(scene.Rat.IsAlive);
    }

    [Fact]
    public void MelodiousRaptureVisual_KeepsARatThatStillFollows()
    {
        using var scene = new Scene();
        scene.Rat.Motion.MoveFollow(scene.Player, 3f, 0f);
        scene.Kit.System.CastSpell(scene.Player, GenericDummyAuraModule.MelodiousRaptureVisual,
            SpellCastTargets.ForUnit(scene.Rat.Guid), triggered: true);

        scene.Run(4_500);

        Assert.True(scene.Rat.IsAlive);
    }
}
