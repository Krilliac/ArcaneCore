using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.ClientData;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// What an escape buys a retreat is derived from the client's own spell data (Spell.dbc and SpellDuration.dbc of the build-5875
/// directory, <c>ARCANECORE_TEST_DBC_DIR</c>), not from a table of numbers. The expected values below are read straight from the raw
/// DBC rows (the duration row of the spell's duration index, the effect base points and dice of its speed auras), independently of
/// the <see cref="SpellInfo"/> the bot resolves, for every spell of the escape's name and so every rank.
/// </summary>
public sealed class PlayerbotEscapeSpellDataTests
{
    private const uint ApplyAura = 6;
    private const uint ModIncreaseSpeed = 31, ModDecreaseSpeed = 33, ModSpeedAlways = 129, ModSpeedNotStack = 171;

    private static (uint Effect, uint Aura, int Value)[] Effects(SpellTemplateRow r) =>
    [
        (r.Effect1, r.EffectApplyAuraName1, r.EffectBasePoints1 + (int)r.EffectBaseDice1),
        (r.Effect2, r.EffectApplyAuraName2, r.EffectBasePoints2 + (int)r.EffectBaseDice2),
        (r.Effect3, r.EffectApplyAuraName3, r.EffectBasePoints3 + (int)r.EffectBaseDice3),
    ];

    /// <summary>The speed aura of the given raw aura types with the largest magnitude, signed percent (0 when none).</summary>
    private static int RawSpeedPercent(SpellTemplateRow row, params uint[] auras)
    {
        int best = 0;
        foreach ((uint effect, uint aura, int value) in Effects(row))
            if (effect == ApplyAura && auras.Contains(aura) && Math.Abs(value) > Math.Abs(best)) best = value;
        return best;
    }

    [RealClientDbcFact]
    public void TheDerivedEffectOfEveryRankOfEveryEscape_EqualsTheRawSpellData()
    {
        SpellDbcContent content = SpellDbcImporter.ReadDirectory(RealClientDbcFactAttribute.DbcDirectory);
        var castTimes = content.CastTimes.ToDictionary(r => r.Id);
        var durations = content.Durations.ToDictionary(r => r.Id);
        var ranges = content.Ranges.ToDictionary(r => r.Id);
        var radii = content.Radii.ToDictionary(r => r.Id);
        int checkedWithEffect = 0;
        foreach (string name in PlayerbotEscapes.All)
        {
            SpellTemplateRow[] rows = [.. content.Spells.Where(r => string.Equals(r.SpellName, name, StringComparison.Ordinal))];
            Assert.True(rows.Length > 0, $"{name} is not in Spell.dbc");
            foreach (SpellTemplateRow row in rows)
            {
                SpellInfo info = SpellStoreFactory.ToSpellInfo(row, castTimes, durations, ranges, radii);
                PlayerbotEscapes.EscapeEffect actual = PlayerbotEscapes.EffectOf(name, info);
                int rawDuration = durations.TryGetValue(row.DurationIndex, out SpellDurationRow? d) ? d.Duration : 0;
                string what = $"{name} spell {row.Id}";
                switch (PlayerbotEscapes.KindOf(name))
                {
                    case PlayerbotEscapes.EscapeKind.HoldsAll or PlayerbotEscapes.EscapeKind.HoldsVictim:
                        Assert.Equal(rawDuration > 0 ? rawDuration / 1000f : 0f, actual.FreeSeconds);
                        Assert.Equal(rawDuration > 0 && PlayerbotEscapes.KindOf(name) == PlayerbotEscapes.EscapeKind.HoldsAll, actual.AllPursuers);
                        if (rawDuration > 0) checkedWithEffect++;
                        break;
                    case PlayerbotEscapes.EscapeKind.BreaksContact:
                        Assert.True(float.IsPositiveInfinity(actual.FreeSeconds), what);
                        checkedWithEffect++;
                        break;
                    case PlayerbotEscapes.EscapeKind.SelfSpeed:
                    {
                        int percent = RawSpeedPercent(row, ModIncreaseSpeed, ModSpeedAlways, ModSpeedNotStack);
                        Assert.Equal(percent > 0 ? 1f + (percent / 100f) : 1f, actual.SelfSpeed);
                        if (percent > 0) checkedWithEffect++;
                        break;
                    }

                    case PlayerbotEscapes.EscapeKind.VictimSnare:
                    {
                        int percent = -RawSpeedPercent(row, ModDecreaseSpeed);
                        Assert.Equal(percent is > 0 and < 100 ? 1f - (percent / 100f) : 1f, actual.VictimSpeed);
                        if (percent is > 0 and < 100) checkedWithEffect++;
                        break;
                    }

                    default:
                        Assert.Equal(PlayerbotEscapes.EscapeEffect.None, actual);
                        break;
                }
            }
        }

        // Not vacuous: the data gave real effects for the kinds (a derivation that always returned nothing would pass every case above).
        Assert.True(checkedWithEffect >= 20, $"only {checkedWithEffect} escape spells had data to derive from");
    }

    /// <summary>
    /// Literal values, so a mistake shared by the derivation and the raw-row oracle above cannot pass. Each was read from the 5875
    /// Spell.dbc / SpellDuration.dbc by a separate reader during review (2026-10-12): the speed auras' base points plus dice, the
    /// duration row's base milliseconds.
    /// </summary>
    [RealClientDbcTheory]
    [InlineData("Sprint", 2983u, 1.5f, 0f, 1f)]
    [InlineData("Sprint", 11305u, 1.7f, 0f, 1f)]
    [InlineData("Dash", 1850u, 1.5f, 0f, 1f)]
    [InlineData("Aspect of the Cheetah", 5118u, 1.3f, 0f, 1f)]
    [InlineData("Frost Nova", 122u, 1f, 8f, 1f)]
    [InlineData("Divine Shield", 642u, 1f, 10f, 1f)]
    [InlineData("Divine Shield", 1020u, 1f, 12f, 1f)]
    [InlineData("Howl of Terror", 5484u, 1f, 10f, 1f)]
    [InlineData("Howl of Terror", 17928u, 1f, 15f, 1f)]
    [InlineData("Hammer of Justice", 853u, 1f, 3f, 1f)]
    [InlineData("Hamstring", 1715u, 1f, 0f, 0.6f)]
    [InlineData("Hamstring", 7373u, 1f, 0f, 0.5f)]
    [InlineData("Concussive Shot", 5116u, 1f, 0f, 0.5f)]
    public void KnownEscapeSpells_DeriveTheirPublishedValues(string name, uint spellId, float selfSpeed, float freeSeconds, float victimSpeed)
    {
        PlayerbotEscapes.EscapeEffect effect = PlayerbotEscapes.EffectOf(name, Resolve(spellId, name));
        Assert.Equal(selfSpeed, effect.SelfSpeed, 3);
        Assert.Equal(freeSeconds, effect.FreeSeconds, 3);
        Assert.Equal(victimSpeed, effect.VictimSpeed, 3);
    }

    /// <summary>Feign Death's row carries a 360000 ms duration, but it breaks contact for good: the kind wins over the number.</summary>
    [RealClientDbcFact]
    public void FeignDeath_BreaksContactForGood() =>
        Assert.True(float.IsPositiveInfinity(PlayerbotEscapes.EffectOf("Feign Death", Resolve(5384u, "Feign Death")).FreeSeconds));

    /// <summary>
    /// Travel Form (783) has no speed aura of its own (its 40% is on the passive 5419), so it must not be classed as a speed escape:
    /// a speed escape that can never derive a speed would turn every druid retreat that leans on it into a silent refusal.
    /// </summary>
    [RealClientDbcFact]
    public void EverySpeedEscape_DerivesASpeedForSomeRank_AndTravelFormIsNotOne()
    {
        Assert.Equal(PlayerbotEscapes.EscapeKind.None, PlayerbotEscapes.KindOf("Travel Form"));
        Assert.Equal(PlayerbotEscapes.EscapeEffect.None, PlayerbotEscapes.EffectOf("Travel Form", Resolve(783u, "Travel Form")));
        SpellDbcContent content = Content.Value;
        foreach (string name in PlayerbotEscapes.All.Where(n => PlayerbotEscapes.KindOf(n) is PlayerbotEscapes.EscapeKind.SelfSpeed or PlayerbotEscapes.EscapeKind.VictimSnare))
        {
            bool any = content.Spells.Where(r => string.Equals(r.SpellName, name, StringComparison.Ordinal))
                .Select(r => PlayerbotEscapes.EffectOf(name, ToInfo(r)))
                .Any(e => e.SelfSpeed > 1f || e.VictimSpeed < 1f);
            Assert.True(any, $"{name} is a speed escape but no rank derives a speed");
        }
    }

    /// <summary>
    /// Every slow the kite model lists for a class (<see cref="PlayerbotKiteModel.SlowsOf"/>) is in Spell.dbc and, for some rank, holds
    /// or slows by its own data (a root, stun or fear with a duration, or a negative speed aura), so no class lists a name that never
    /// derives an effect. Which classes the model covers is the model's table; what each spell does is only ever read from the rows.
    /// </summary>
    [RealClientDbcFact]
    public void EverySlowTheKiteListsForAClass_DerivesAnEffectForSomeRank()
    {
        SpellDbcContent content = Content.Value;
        int checkedNames = 0;
        foreach (Class playerClass in Enum.GetValues<Class>())
        {
            foreach (string name in PlayerbotKiteModel.SlowsOf(playerClass))
            {
                SpellTemplateRow[] rows = [.. content.Spells.Where(r => string.Equals(r.SpellName, name, StringComparison.Ordinal))];
                Assert.True(rows.Length > 0, $"{name} ({playerClass}) is not in Spell.dbc");
                Assert.True(rows.Any(r => PlayerbotKiteModel.IsSlow(name, ToInfo(r))), $"{name} ({playerClass}) derives no slow for any rank");
                checkedNames++;
            }
        }

        Assert.True(checkedNames >= 10, $"only {checkedNames} slow names were checked");
    }

    private static readonly Lazy<SpellDbcContent> Content = new(() => SpellDbcImporter.ReadDirectory(RealClientDbcFactAttribute.DbcDirectory));

    private static SpellInfo ToInfo(SpellTemplateRow row)
    {
        SpellDbcContent c = Content.Value;
        return SpellStoreFactory.ToSpellInfo(row, c.CastTimes.ToDictionary(r => r.Id), c.Durations.ToDictionary(r => r.Id),
            c.Ranges.ToDictionary(r => r.Id), c.Radii.ToDictionary(r => r.Id));
    }

    private static SpellInfo Resolve(uint spellId, string name)
    {
        SpellTemplateRow row = Assert.Single(Content.Value.Spells, r => r.Id == spellId);
        Assert.Equal(name, row.SpellName);
        return ToInfo(row);
    }
}
