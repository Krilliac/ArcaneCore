using ArcaneCore.Game.Chat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Chat;

/// <summary>
/// The SPELL_AURA_MOD_LANGUAGE read the chat handler uses (vmangos ChatHandler.cpp
/// HandleChatMessageOpcode: <c>ModLangAuras.front()->GetModifier()->m_miscvalue</c>).
/// </summary>
public sealed class ChatLanguageQueryTests
{
    private const uint Gnomish = 930401;
    private const uint Demonic = 930402;

    private static SpellTestKit Kit()
    {
        SpellInfo LanguageAura(uint id, Language language) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModLanguage, misc: (int)language)) with
        {
            Duration = new SpellDuration(30_000, 0, 30_000),
            SpellVisual = 1,
        };

        return new SpellTestKit(LanguageAura(Gnomish, Language.Gnomish), LanguageAura(Demonic, Language.Demonic));
    }

    [Fact]
    public void WithoutAnAura_NoLanguageIsForced()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        Assert.Null(kit.System.ModLanguageOverride(player));
    }

    [Fact]
    public void TheAurasMiscValue_IsTheForcedLanguage_AndTheFirstAppliedAuraWins()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, Gnomish, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, Demonic, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(Language.Gnomish, kit.System.ModLanguageOverride(player)); // front() of the aura list

        kit.System.RemoveAuras(player, Gnomish);
        Assert.Equal(Language.Demonic, kit.System.ModLanguageOverride(player));

        kit.System.RemoveAuras(player, Demonic);
        Assert.Null(kit.System.ModLanguageOverride(player));
    }
}
