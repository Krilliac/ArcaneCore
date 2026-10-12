using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots;
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
}
