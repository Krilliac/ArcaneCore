using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// <see cref="PeriodicTriggerAuraModule"/>: the Aura::TriggerSpell cases of vmangos (SpellAuras.cpp:1291-1301 Nitrous Boost, :1246-1248 and
/// :4243-4255 Cannon Prep) and the expiry case of Tranquilizing Poison (:7215-7222). Each test fails without its registration: the aura ticks or
/// ends and nothing of what retail does happens. The Cannon Prep spell is shaped like its 5875 Spell.dbc row (24743: two other aura effects and a
/// 5000 ms PERIODIC_TRIGGER effect with no trigger spell). The rocket boots are shaped like their vmangos spell_effect_mod rows (effect 2 a 1000 ms
/// periodic trigger of the explosion).
/// </summary>
public sealed class PeriodicTriggerAuraCasesTests
{
    private const uint Marker = 941201;

    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo TriggerAura(uint id, uint trigger, int durationMs = 60_000) =>
        Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.PeriodicTriggerSpell, amplitude: 1000, trigger: trigger)) with
        {
            Duration = new SpellDuration(durationMs, 0, durationMs),
        });

    private static SpellInfo MarkerAura(uint id) =>
        Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
        });

    private static SpellTestKit MakeKit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    [Fact]
    public void NitrousBoost_BurnsTenManaPerTick_AndEndsWhenTheTargetHasLess()
    {
        using SpellTestKit kit = MakeKit(TriggerAura(PeriodicTriggerAuraModule.NitrousBoost, 0));
        (Player player, FakeSession session) = kit.AddPlayer(1);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana, 500);
        SpellSystem.SetPower(player, PowerType.Mana, 25);

        kit.System.CastSpell(player, PeriodicTriggerAuraModule.NitrousBoost, SpellCastTargets.ForSelf(), triggered: true);
        session.Clear();
        kit.Advance(1_200);
        Assert.Equal(15u, SpellSystem.GetPower(player, PowerType.Mana));
        Assert.NotEmpty(Packets(session, WorldOpcode.SmsgSpellenergizelog));
        Assert.True(kit.System.HasAura(player, PeriodicTriggerAuraModule.NitrousBoost));

        kit.Advance(1_000);
        Assert.Equal(5u, SpellSystem.GetPower(player, PowerType.Mana));
        Assert.True(kit.System.HasAura(player, PeriodicTriggerAuraModule.NitrousBoost));

        kit.Advance(1_000); // 5 mana is below the 10 a tick costs: the aura goes and the mana stays
        Assert.Equal(5u, SpellSystem.GetPower(player, PowerType.Mana));
        Assert.False(kit.System.HasAura(player, PeriodicTriggerAuraModule.NitrousBoost));
    }

    [Theory]
    [InlineData(PeriodicTriggerAuraModule.TranquilizingPoison)]
    [InlineData(PeriodicTriggerAuraModule.TranquilizingPoisonB)]
    public void TranquilizingPoison_CastsItsEndSpellWhenItExpires_NotWhenItIsCancelled(uint poison)
    {
        using SpellTestKit kit = MakeKit(
            Instant(Spell(poison, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
            {
                Duration = new SpellDuration(2_000, 0, 2_000),
            }),
            MarkerAura(PeriodicTriggerAuraModule.TranquilizingPoisonEnd));
        (Player expired, _) = kit.AddPlayer(1);
        (Player cancelled, _) = kit.AddPlayer(2);

        kit.System.CastSpell(expired, poison, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(cancelled, poison, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.RemoveAuras(cancelled, poison, AuraRemoveMode.Cancel);
        Assert.False(kit.System.HasAura(cancelled, PeriodicTriggerAuraModule.TranquilizingPoisonEnd));

        kit.Advance(2_500);

        Assert.False(kit.System.HasAura(expired, poison));
        Assert.True(kit.System.HasAura(expired, PeriodicTriggerAuraModule.TranquilizingPoisonEnd));
        Assert.False(kit.System.HasAura(cancelled, PeriodicTriggerAuraModule.TranquilizingPoisonEnd));
    }

    [Theory]
    [InlineData(758u)]
    [InlineData(17945u)]
    [InlineData(17947u)]
    [InlineData(17949u)]
    public void Firestone_HasNoTickScript_TheDbcTriggerSpellCastsTheEnchant(uint spell)
    {
        using SpellTestKit kit = MakeKit();
        Assert.False(kit.System.HasPeriodicTriggerScript(spell));
    }

    /// <summary><see cref="Random.Next(int)"/> that answers one value, so the 1 in 100 roll of rocket boots is decided by the test.</summary>
    private sealed class FixedRandom(int value) : Random(1)
    {
        public override int Next(int maxValue) => Math.Min(value, maxValue - 1);
    }

    private static SpellInfo RocketBoots(uint id, uint explosion) =>
        Instant(Spell(id,
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModIncreaseSpeed),
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.PeriodicTriggerSpell, amplitude: 1000, trigger: explosion)) with
        {
            Duration = new SpellDuration(20_000, 0, 20_000),
        });

    [Theory]
    [InlineData(PeriodicTriggerAuraModule.GoblinRocketBoots, 8897u, 0, true)]
    [InlineData(PeriodicTriggerAuraModule.GoblinRocketBoots, 8897u, 1, false)]
    [InlineData(PeriodicTriggerAuraModule.GoblinRocketBoots, 8897u, 99, false)]
    [InlineData(PeriodicTriggerAuraModule.GnomishRocketBoots, 13158u, 0, true)]
    [InlineData(PeriodicTriggerAuraModule.GnomishRocketBoots, 13158u, 50, false)]
    public void RocketBoots_ExplodeOnlyOnTheOneInHundredRoll_OtherwiseTheTickDoesNothing(uint boots, uint explosion, int roll, bool exploded)
    {
        using SpellTestKit kit = MakeKit(RocketBoots(boots, explosion), MarkerAura(explosion));
        kit.System.Random = new FixedRandom(roll);
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, boots, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(500);
        Assert.False(kit.System.HasAura(player, explosion)); // the first tick is due after 1000 ms

        kit.Advance(600);

        Assert.True(kit.System.HasAura(player, boots));
        Assert.Equal(exploded, kit.System.HasAura(player, explosion));
    }

    private const uint CannonEntry = 951301, CannonEntryB = 951302;

    private static SpellInfo CannonPrep(uint id) =>
        Instant(Spell(id,
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy),
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy),
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.PeriodicTriggerSpell, amplitude: 5000, trigger: 0)) with
        {
            Duration = new SpellDuration(5_000, 0, 5_000),
        });

    [Theory]
    [InlineData(PeriodicTriggerAuraModule.CannonPrep, CannonEntry)]
    [InlineData(PeriodicTriggerAuraModule.CannonPrepB, CannonEntryB)]
    public void CannonPrep_FiresTheCannonOnTheTickThatEndsTheFiveSecondAura_AndTheCannonAnimatesThreePointSixSecondsAfterTheAuraLands(uint spell, uint entry)
    {
        using SpellTestKit kit = MakeKit(CannonPrep(spell), MarkerAura(PeriodicTriggerAuraModule.FireCannon));
        var objects = new GameObjectMapSystem(kit.World.GetMap(0), new GameObjectContent(
            [GameObjectTestKit.GoTemplate(entry, GameObjectType.Goober, (10, spell))], [], [], [], []));
        kit.World.GetMap(0).AddUpdater(objects);
        (Player player, FakeSession session) = kit.AddPlayer(1);
        GameObject cannon = objects.Summon(entry, 2, 1, player.Z, 0)!;
        kit.World.RunTick(0);
        session.Clear();
        byte[] expectedAnim = GameObjectPackets.CustomAnim(cannon.Guid, 0);

        kit.System.CastSpell(player, spell, SpellCastTargets.ForSelf(), triggered: true);
        for (int i = 0; i < 34; i++) // 3.4 s: the animation is not due yet
        {
            kit.Advance(100);
            kit.World.RunTick(100);
        }

        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgGameobjectCustomAnim);
        Assert.False(kit.System.HasAura(player, PeriodicTriggerAuraModule.FireCannon));

        for (int i = 0; i < 4; i++) // 3.8 s
        {
            kit.Advance(100);
            kit.World.RunTick(100);
        }

        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.SmsgGameobjectCustomAnim && p.Payload.SequenceEqual(expectedAnim));
        Assert.False(kit.System.HasAura(player, PeriodicTriggerAuraModule.FireCannon));

        for (int i = 0; i < 14; i++) // 5.2 s: the aura ended at 5 s and its only tick fired on that update
        {
            kit.Advance(100);
            kit.World.RunTick(100);
        }

        Assert.True(kit.System.HasAura(player, PeriodicTriggerAuraModule.FireCannon));
    }

    [Fact]
    public void FindCannon_PicksTheNearestGooberThatCastsTheSpell_AndIgnoresOthers()
    {
        using SpellTestKit kit = MakeKit();
        var objects = new GameObjectMapSystem(kit.World.GetMap(0), new GameObjectContent(
            [
                GameObjectTestKit.GoTemplate(CannonEntry, GameObjectType.Goober, (10, PeriodicTriggerAuraModule.CannonPrep)),
                GameObjectTestKit.GoTemplate(CannonEntryB, GameObjectType.Goober, (10, PeriodicTriggerAuraModule.CannonPrepB)),
            ], [], [], [], []));
        kit.World.GetMap(0).AddUpdater(objects);
        (Player player, _) = kit.AddPlayer(1);
        GameObject far = objects.Summon(CannonEntry, 20, 0, player.Z, 0)!;
        GameObject near = objects.Summon(CannonEntry, 3, 0, player.Z, 0)!;
        objects.Summon(CannonEntryB, 1, 0, player.Z, 0);

        Assert.Same(near, PeriodicTriggerAuraModule.FindCannon(player, PeriodicTriggerAuraModule.CannonPrep));
        Assert.NotSame(far, PeriodicTriggerAuraModule.FindCannon(player, PeriodicTriggerAuraModule.CannonPrep));
        Assert.Null(PeriodicTriggerAuraModule.FindCannon(player, 12345));
    }
}
