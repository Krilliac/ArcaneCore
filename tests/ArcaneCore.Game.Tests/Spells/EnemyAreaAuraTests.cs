using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Enemy area aura (effect 129, vmangos AreaAura::Update AREA_AURA_ENEMY, SpellAuras.cpp:689-695) target filters of
/// AnyAoETargetUnitInObjectRangeCheck (GridNotifiers.h:952-968), the persisted enemy source across relog, and the creature
/// state of Permanent Feign Death 29266 (SpellAuras.cpp:2103-2112, Unit::SetFeignDeath).
/// </summary>
public sealed class EnemyAreaAuraTests
{
    private const uint Sam = 946101, InvisibilitySpell = 946102, PermanentFeignDeath = 29266;
    private const long SavedAt = 1_800_000_000_000;

    private static SpellInfo EnemyArea() => Spell(Sam,
        Effect(SpellEffectName.ApplyAreaAuraEnemy, 10, SpellImplicitTarget.UnitCaster, AuraType.PeriodicDamage, amplitude: 1000) with { Radius = 20f }) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo Permanent(uint id, SpellEffectInfo effect) => Spell(id, effect) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellTestKit Kit(params SpellInfo[] extra)
    {
        var kit = new SpellTestKit([EnemyArea(), .. extra]);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    private sealed class MapObjectResolver : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => guid.IsEmpty ? null : reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }

    [Fact]
    public void CasterWithoutPvpFlag_DoesNotHitAnotherPlayer_UntilItIsFlagged()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddHostilePlayer(2, 5, 0);

        kit.System.CastSpell(caster, Sam, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(1500);

        Assert.False(kit.System.HasAura(enemy, Sam)); // Unit::CanAttackWithoutEnablingPvP

        FlagForPvp(caster);
        kit.Advance(1500);

        Assert.True(kit.System.HasAura(enemy, Sam)); // control: the skip above was the PvP rule and not the setup
    }

    [Fact]
    public void GmInvisiblePlayer_IsSkipped_AndPlayerWithAnInvisibilityAuraTheCasterCannotDetect_IsSkipped()
    {
        using SpellTestKit kit = Kit(Permanent(InvisibilitySpell, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModInvisibility, misc: 1)));
        (Player caster, _) = kit.AddPlayer(1);
        FlagForPvp(caster);
        (Player hidden, _) = kit.AddHostilePlayer(2, 5, 0, ArcaneCore.Kernel.Accounts.AccountSecurity.GameMaster); // a higher security than the caster's, so the caster cannot see it
        (Player shown, _) = kit.AddHostilePlayer(3, 6, 0);
        hidden.GmInvisible = true;
        kit.System.CastSpell(caster, Sam, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(1500);

        Assert.False(kit.System.HasAura(hidden, Sam));
        Assert.True(kit.System.HasAura(shown, Sam));

        hidden.GmInvisible = false;
        kit.Advance(1500);
        Assert.True(kit.System.HasAura(hidden, Sam)); // control: visible again, hit

        kit.System.CastSpell(hidden, InvisibilitySpell, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(1500);
        Assert.False(kit.System.HasAura(hidden, Sam)); // a held child goes when the unit stops being a valid target
        Assert.True(kit.System.HasAura(shown, Sam));
    }

    [Fact]
    public void EnemySource_SurvivesRelog_AndSpreadsTheRealAuraAgain()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        FlagForPvp(caster);
        (Player enemy, _) = kit.AddHostilePlayer(2, 5, 0);
        kit.System.CastSpell(caster, Sam, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(1500);
        Assert.True(kit.System.HasAura(enemy, Sam));

        SpellStateSnapshot snapshot = kit.System.CaptureState(caster, SavedAt);
        PersistedAura saved = Assert.Single(snapshot.Auras, a => a.SpellId == Sam);
        Assert.Equal(1, saved.EffectMask & 1);

        kit.System.RemoveAuras(caster, Sam); // logout
        kit.Advance(200);
        Assert.False(kit.System.HasAura(enemy, Sam));

        SpellAuraHolder source = Assert.Single(kit.System.RestoreAuras(caster, snapshot.Auras, SavedAt + 1000));
        Assert.True(source.IsAreaSource);
        Assert.Equal(AuraType.None, source.Auras[0]?.Type);
        Assert.Equal(0u, source.Auras[0]?.Amplitude);

        kit.Advance(1500);
        Assert.Contains(kit.System.GetAuras(enemy), h => h.Spell.Id == Sam && h.Auras[0]?.Type == AuraType.PeriodicDamage);
    }

    [Fact]
    public void PermanentFeignDeath_SetsTheCreatureFeignState_AndRemovalClearsItAndTheFlag()
    {
        using SpellTestKit kit = Kit(Permanent(PermanentFeignDeath, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)));
        var creatures = new CreatureMapSystem(kit.World.GetMap(0), Content([Template(WolfEntry)], []), random: new Random(1));
        kit.World.GetMap(0).AddUpdater(creatures);
        kit.System.Units = new MapObjectResolver();
        Creature wolf = creatures.SpawnTemporary(Template(WolfEntry), 6, 0, 83.5f, 0);
        Assert.False(kit.System.IsFeigningDeath(wolf));

        kit.System.CastSpell(wolf, PermanentFeignDeath, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(200);

        Assert.True(kit.System.HasAura(wolf, PermanentFeignDeath));
        Assert.True(kit.System.IsFeigningDeath(wolf));
        Assert.True(wolf.HasFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.Dead));

        kit.System.RemoveAuras(wolf, PermanentFeignDeath);
        kit.Advance(200);

        Assert.False(kit.System.IsFeigningDeath(wolf));
        Assert.False(wolf.HasFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.Dead));
    }
}
