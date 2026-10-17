using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>What a bot does in a fight (vmangos CombatBotRoles, CombatBotBaseAI.h).</summary>
internal enum PlayerbotRole : byte
{
    MeleeDps,
    RangeDps,
    Tank,
    Healer,
}

/// <summary>
/// vmangos CombatBotBaseAI::AutoAssignRole (CombatBotBaseAI.cpp:58-118): the role follows the talent signature spell a bot
/// knows. The ids are the vmangos rank-1 talent spells; a known later rank of the same spell counts too (the caller answers
/// by name as well as by id).
/// </summary>
internal static class PlayerbotRoles
{
    internal const uint ShieldSlam = 23922;
    internal const uint HolyShield = 20925;
    internal const uint SanctityAura = 20218;
    internal const uint Shadowform = 15473;
    internal const uint ElementalMastery = 16166;
    internal const uint Stormstrike = 17364;
    internal const uint MoonkinForm = 24858;
    internal const uint LeaderOfThePack = 17007;

    internal static PlayerbotRole Assign(Class playerClass, Func<uint, bool> hasSpell)
    {
        ArgumentNullException.ThrowIfNull(hasSpell);
        return playerClass switch
        {
            Class.Warrior => hasSpell(ShieldSlam) ? PlayerbotRole.Tank : PlayerbotRole.MeleeDps,
            Class.Rogue => PlayerbotRole.MeleeDps,
            Class.Hunter or Class.Mage or Class.Warlock => PlayerbotRole.RangeDps,
            Class.Paladin => hasSpell(HolyShield) ? PlayerbotRole.Tank
                : hasSpell(SanctityAura) ? PlayerbotRole.MeleeDps : PlayerbotRole.Healer,
            Class.Priest => hasSpell(Shadowform) ? PlayerbotRole.RangeDps : PlayerbotRole.Healer,
            Class.Shaman => hasSpell(ElementalMastery) ? PlayerbotRole.RangeDps
                : hasSpell(Stormstrike) ? PlayerbotRole.MeleeDps : PlayerbotRole.Healer,
            Class.Druid => hasSpell(MoonkinForm) ? PlayerbotRole.RangeDps
                : hasSpell(LeaderOfThePack) ? PlayerbotRole.MeleeDps : PlayerbotRole.Healer,
            _ => PlayerbotRole.MeleeDps,
        };
    }
}
