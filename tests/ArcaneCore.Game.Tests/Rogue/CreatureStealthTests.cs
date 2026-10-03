using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stealth;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Rogue;

/// <summary>
/// Whether a creature sees a stealthed player (vmangos CallAIMoveLOS -> IsVisibleForOrDetect with detect = true -> CanDetectStealthOf,
/// creature constants: 5/6 yard base and per level; sniffed: a level 4 creature aggroes a level 1 rogue at about 3.3 yards).
/// </summary>
public sealed class CreatureStealthTests
{
    private const uint Stealth = 910501;

    private static (SpellTestKit Kit, StealthServices Services, StealthRegistry Registry, Player Rogue, CombatTestUnit Mob) Setup(float mobX, byte mobLevel = 4, float mobOrientation = MathF.PI)
    {
        var kit = new SpellTestKit(Spell(Stealth, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModStealth))
            with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 });
        var registry = new StealthRegistry();
        kit.System.RegisterAura(AuraType.ModStealth, StealthAuras.Handler(registry));
        var services = new StealthServices(kit.System, registry, StealthOptions.Default);
        (Player rogue, _) = kit.AddPlayer(1);
        rogue.Level = 1;
        Map map = kit.World.GetMap(0);
        var mob = new CombatTestUnit(mobLevel);
        mob.Spawn(map, mobX, 0, 83.5f, mobOrientation);
        return (kit, services, registry, rogue, mob);
    }

    [Fact]
    public void AnUnstealthedPlayer_IsAlwaysSeen_AndNoAlertIsRaised()
    {
        (SpellTestKit kit, StealthServices services, _, Player rogue, CombatTestUnit mob) = Setup(50f);
        using (kit)
        {
            Assert.True(services.CanCreatureSee(mob, rogue, out bool alert));
            Assert.False(alert);
        }
    }

    [Fact]
    public void ALevel4Creature_SeesALevel1StealthedRogueOnlyInsideAbout3_3Yards_AndAlertsUpToFiveBeyond()
    {
        (SpellTestKit kit, StealthServices services, _, Player rogue, CombatTestUnit mob) = Setup(3.2f);
        using (kit)
        {
            kit.System.CastSpell(rogue, Stealth, SpellCastTargets.ForSelf(), triggered: true);

            Assert.True(services.CanCreatureSee(mob, rogue, out bool alert));
            Assert.False(alert);

            mob.Relocate(4f, 0, 83.5f, MathF.PI, 0);
            Assert.False(services.CanCreatureSee(mob, rogue, out alert));
            Assert.True(alert); // inside the 5 yard band beyond 3.33

            mob.Relocate(8.3f, 0, 83.5f, MathF.PI, 0);
            Assert.False(services.CanCreatureSee(mob, rogue, out alert));
            Assert.True(alert); // 8.33 is the edge of the band

            mob.Relocate(9f, 0, 83.5f, MathF.PI, 0);
            Assert.False(services.CanCreatureSee(mob, rogue, out alert));
            Assert.False(alert);
        }
    }

    [Fact]
    public void RemovingStealth_MakesTheSameDistanceVisibleAgain()
    {
        (SpellTestKit kit, StealthServices services, _, Player rogue, CombatTestUnit mob) = Setup(8f);
        using (kit)
        {
            kit.System.CastSpell(rogue, Stealth, SpellCastTargets.ForSelf(), triggered: true);
            Assert.False(services.CanCreatureSee(mob, rogue, out _));

            kit.System.RemoveAuras(rogue, Stealth);
            Assert.True(services.CanCreatureSee(mob, rogue, out _));
        }
    }

    [Fact]
    public void ACreatureFacingAwayLosesNineYards_SoItOnlyNoticesTheRogueUnderOnePointFive()
    {
        // The rogue is at the origin; the mob stands 2 yards east of it.
        (SpellTestKit kit, StealthServices services, _, Player rogue, CombatTestUnit mob) = Setup(2.0f, mobOrientation: 0f);
        using (kit)
        {
            kit.System.CastSpell(rogue, Stealth, SpellCastTargets.ForSelf(), triggered: true);

            // Facing east, away from the rogue: 3.33 - 9 < 0, nothing is noticed beyond the collision distance.
            Assert.False(services.CanCreatureSee(mob, rogue, out _));
            mob.Relocate(1.4f, 0, 83.5f, 0f, 0);
            Assert.True(services.CanCreatureSee(mob, rogue, out _));

            // Turned toward the rogue at 2 yards it sees it (3.33).
            mob.Relocate(2.0f, 0, 83.5f, MathF.PI, 0);
            Assert.True(services.CanCreatureSee(mob, rogue, out _));
        }
    }

    [Fact]
    public void AStunnedCreatureNeverSees_AndAJustStealthedUnitIsNotSeen()
    {
        (SpellTestKit kit, StealthServices services, StealthRegistry registry, Player rogue, CombatTestUnit mob) = Setup(1.0f);
        using (kit)
        {
            kit.System.CastSpell(rogue, Stealth, SpellCastTargets.ForSelf(), triggered: true);
            Assert.True(services.CanCreatureSee(mob, rogue, out _)); // under 1.5 yards

            mob.SetFlag(UpdateFields.UnitFieldFlags, (uint)UnitFlags.Stunned);
            Assert.False(services.CanCreatureSee(mob, rogue, out _));
            mob.RemoveFlag(UpdateFields.UnitFieldFlags, (uint)UnitFlags.Stunned);
            Assert.True(services.CanCreatureSee(mob, rogue, out _));

            registry.SetVisibility(rogue, StealthVisibility.NoDetect);
            Assert.False(services.CanCreatureSee(mob, rogue, out _));
        }
    }

    [Fact]
    public void TheServicesAreFoundPerMap_AfterInstall()
    {
        (SpellTestKit kit, StealthServices services, _, _, _) = Setup(5f);
        using (kit)
        {
            Map map = kit.World.GetMap(0);
            Assert.Null(StealthServices.Find(map));
            StealthServices.Install(map, services);
            Assert.Same(services, StealthServices.Find(map));
        }
    }
}
