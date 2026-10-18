using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Auras;

/// <summary>
/// Crowd control heartbeats (vmangos SpellAuraHolder::CalculateHeartBeat, SpellAuras.cpp:8114-8143, and the heartbeat step of
/// SpellAuraHolder::Update, :7252-7295): a heartbeat crowd control of over 10 s on a player breaks on a curve that is 50% at 12 s and
/// 99% at 15 s; on a creature it rolls the caster's spell miss chance every 5 s.
/// </summary>
public sealed class HeartbeatResistTests
{
    private const uint Stun = 963_101;
    private const uint ShortStun = 963_102;
    private const uint Fear = 963_103;

    private sealed class FixedRandom(float single, int next) : Random(1)
    {
        public override float NextSingle() => single;

        public override int Next(int minValue, int maxValue) => Math.Clamp(next, minValue, maxValue - 1);
    }

    private static SpellInfo Cc(uint id, AuraType type, int durationMs) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, type)) with
        {
            Duration = new SpellDuration(durationMs, 0, durationMs),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            Attributes = (SpellAttributes)SpellRuleFlags.HeartbeatResist,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellTestKit Kit() => new(Cc(Stun, AuraType.ModStun, 20_000), Cc(ShortStun, AuraType.ModStun, 10_000), Cc(Fear, AuraType.ModFear, 20_000));

    [Theory]
    [InlineData(1f, 12f, 50f)]
    [InlineData(1f, 15f, 99f)]
    [InlineData(0.5f, 6f, 50f)]
    [InlineData(0.5f, 7.5f, 99f)]
    public void ThePvpCurve_Is50PercentAtTwelveSeconds_And99AtFifteen_ScaledByDiminishing(float rate, float elapsed, float expected)
        => Assert.Equal(expected, SpellSystem.PvpHeartbeatBreakChance(rate, elapsed), 2);

    [Fact]
    public void APlayersLongStun_BreaksWhenTheCurvePassesItsDraw()
    {
        using SpellTestKit kit = Kit();
        kit.System.Random = new FixedRandom(0.5f, 10_000); // a draw of 50: the break comes at 12 s
        (Player caster, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 5, 0);
        Assert.True(kit.System.AddAura(victim, Stun, caster: caster));

        kit.Advance(11_800);
        Assert.True(kit.System.HasAura(victim, Stun));
        kit.Advance(500);
        Assert.False(kit.System.HasAura(victim, Stun));
    }

    [Fact]
    public void ATenSecondStun_HasNoHeartbeat()
    {
        using SpellTestKit kit = Kit();
        kit.System.Random = new FixedRandom(0.001f, 0);
        (Player caster, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 5, 0);
        Assert.True(kit.System.AddAura(victim, ShortStun, caster: caster));

        kit.Advance(9_800);
        Assert.True(kit.System.HasAura(victim, ShortStun));
    }

    [Fact]
    public void TheOption_Off_KeepsTheFullDuration()
    {
        using SpellTestKit kit = Kit();
        kit.System.AuraOptions = new AuraOptions { HeartbeatResist = false };
        kit.System.Random = new FixedRandom(0.001f, 0);
        (Player caster, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 5, 0);
        Assert.True(kit.System.AddAura(victim, Stun, caster: caster));

        kit.Advance(19_800);
        Assert.True(kit.System.HasAura(victim, Stun));
    }

    [Fact]
    public void ACreaturesStun_RollsTheCastersMissChanceEveryFiveSeconds_AndFearHasNoPveHeartbeat()
    {
        using SpellTestKit kit = Kit();
        kit.System.CombatRules = new VanillaSpellCombatRules();
        kit.System.Random = new FixedRandom(0.5f, 0); // urand 0: under any miss chance
        var content = Content([Template()], [Spawn(1, WolfEntry, 5, 0)]);
        (_, _, CreatureMapSystem creatures) = CreateAiSystem(content, new CreatureAiServices { UnitSpells = new SpellSystemUnitSpellQueries(kit.System) }, world: kit.World);
        kit.AddPlayer(1); // spawns load around a player
        Creature wolf = Assert.Single(creatures.Creatures);
        Assert.True(kit.System.AddAura(wolf, Stun, caster: wolf));
        Assert.True(kit.System.AddAura(wolf, Fear, caster: wolf));

        kit.Advance(4_800);
        Assert.True(kit.System.HasAura(wolf, Stun));
        kit.Advance(400);
        Assert.False(kit.System.HasAura(wolf, Stun));
        Assert.True(kit.System.HasAura(wolf, Fear));
    }
}