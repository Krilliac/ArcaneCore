using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Chat;

/// <summary>Reads of spell state that the chat handlers need (they live in another assembly than the internal aura queries).</summary>
public static class ChatLanguageQueries
{
    /// <summary>
    /// The language forced on the unit's speech by SPELL_AURA_MOD_LANGUAGE (vmangos
    /// ChatHandler.cpp HandleChatMessageOpcode: <c>ModLangAuras.front()->GetModifier()->m_miscvalue</c>,
    /// "only single case used"), or null when no such aura is on the unit. The first aura applied wins.
    /// </summary>
    public static Language? ModLanguageOverride(this SpellSystem system, Unit unit)
    {
        foreach (SpellAura aura in system.AurasOfType(unit, AuraType.ModLanguage))
        {
            return (Language)(uint)aura.MiscValue;
        }

        return null;
    }
}
