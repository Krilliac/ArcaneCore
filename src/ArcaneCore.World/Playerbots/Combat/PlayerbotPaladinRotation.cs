using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>vmangos PartyBotAI::UpdateOutOfCombatAI_Paladin / UpdateInCombatAI_Paladin (PartyBotAI.cpp:1163-1387).</summary>
internal sealed class PlayerbotPaladinRotation : PlayerbotClassRotation
{
    internal const string SealOfRighteousness = "Seal of Righteousness";
    internal const string SealOfCommand = "Seal of Command";
    internal const string SealOfTheCrusader = "Seal of the Crusader";
    internal const string Judgement = "Judgement";
    internal const string HammerOfJustice = "Hammer of Justice";
    internal const string BlessingOfMight = "Blessing of Might";
    internal const string BlessingOfWisdom = "Blessing of Wisdom";
    internal const string BlessingOfKings = "Blessing of Kings";
    internal const string BlessingOfSanctuary = "Blessing of Sanctuary";
    internal const string BlessingOfProtection = "Blessing of Protection";
    internal const string BlessingOfFreedom = "Blessing of Freedom";
    internal const string BlessingOfSacrifice = "Blessing of Sacrifice";
    internal const string DevotionAura = "Devotion Aura";
    internal const string RetributionAura = "Retribution Aura";
    internal const string ConcentrationAura = "Concentration Aura";
    internal const string SanctityAura = "Sanctity Aura";
    internal const string Exorcism = "Exorcism";
    internal const string Consecration = "Consecration";
    internal const string HammerOfWrath = "Hammer of Wrath";
    internal const string Cleanse = "Cleanse";
    internal const string Purify = "Purify";
    internal const string DivineShield = "Divine Shield";
    internal const string LayOnHands = "Lay on Hands";
    internal const string RighteousFury = "Righteous Fury";
    internal const string HolyShock = "Holy Shock";
    internal const string DivineFavor = "Divine Favor";
    internal const string HolyWrath = "Holy Wrath";
    internal const string TurnUndead = "Turn Undead";
    internal const string HolyShield = "Holy Shield";

    public override Class Class => Class.Paladin;

    public override IReadOnlyList<string> Abilities { get; } =
    [
        SealOfRighteousness, SealOfCommand, SealOfTheCrusader, Judgement, HammerOfJustice, BlessingOfMight, BlessingOfWisdom,
        BlessingOfKings, BlessingOfSanctuary, BlessingOfProtection, BlessingOfFreedom, BlessingOfSacrifice, DevotionAura,
        RetributionAura, ConcentrationAura, SanctityAura, Exorcism, Consecration, HammerOfWrath, Cleanse, Purify, DivineShield,
        LayOnHands, RighteousFury, HolyShock, DivineFavor, HolyWrath, TurnUndead, HolyShield,
    ];

    protected override IEnumerable<string> Dispels => [Cleanse, Purify];

    /// <summary>A paladin fights in melee; only a healer with a group to heal stands back.</summary>
    public override float PreferredRange(RotationState state)
        => state.Role == PlayerbotRole.Healer && state.InGroup ? CasterRange : MeleeRange;

    /// <summary>The role's aura (vmangos PopulateSpellData picks one aura per role).</summary>
    internal static string AuraFor(RotationState s) => s.Role switch
    {
        PlayerbotRole.MeleeDps when s.Spells.Has(SanctityAura) => SanctityAura,
        PlayerbotRole.MeleeDps when s.Spells.Has(RetributionAura) => RetributionAura,
        PlayerbotRole.Healer when s.Spells.Has(ConcentrationAura) && s.InGroup => ConcentrationAura,
        _ => DevotionAura,
    };

    /// <summary>The role's seal: Command for a damage dealer that has it, else Righteousness.</summary>
    internal static string SealFor(RotationState s)
        => s.Role == PlayerbotRole.MeleeDps && s.Spells.Has(SealOfCommand) ? SealOfCommand : SealOfRighteousness;

    protected override RotationAction? Upkeep(RotationState s)
        => Do(s, AuraFor(s), s.Self)
            ?? Do(s, RighteousFury, s.Self, s.Role == PlayerbotRole.Tank)
            ?? Blessings(s)
            ?? (s.Role == PlayerbotRole.Healer ? HealInjuredAlly(s) : null);

    /// <summary>
    /// One blessing per friend that has none (vmangos casts its role's blessing on every member): Kings when known, else Might for
    /// a fighter without mana and Wisdom for a mana user; a tank's Sanctuary for itself.
    /// </summary>
    internal static RotationAction? Blessings(RotationState s)
    {
        foreach (RotationUnit friend in s.Friends)
        {
            if (!friend.IsAlive || friend.Distance > FriendlyRange
                || friend.Auras.Any(a => a.StartsWith("Blessing of", StringComparison.OrdinalIgnoreCase)))
                continue;
            string blessing = s.Spells.Has(BlessingOfKings) ? BlessingOfKings
                : friend.IsSelf && s.Role == PlayerbotRole.Tank && s.Spells.Has(BlessingOfSanctuary) ? BlessingOfSanctuary
                : UsesMana(friend) && (friend.Class != (byte)Class.Paladin || s.Role == PlayerbotRole.Healer) && s.Spells.Has(BlessingOfWisdom)
                    ? BlessingOfWisdom
                : BlessingOfMight;
            if (Do(s, blessing, friend) is { } action) return action;
        }
        return null;
    }

    protected override RotationAction? Fight(RotationState s, RotationUnit v)
    {
        RotationUnit? lowFriend = s.Party.Where(m => m.IsAlive && m.Distance <= FriendlyRange && m.HealthPercent < 70f)
            .OrderBy(m => m.HealthPercent).FirstOrDefault();
        RotationAction? save = Do(s, DivineShield, s.Self, s.InCombat && s.Self.HealthPercent < 20f && s.Role != PlayerbotRole.Tank)
            ?? (lowFriend is null ? null
                : Do(s, BlessingOfProtection, lowFriend, !IsPhysicalClass(lowFriend.Class))
                    ?? Do(s, BlessingOfSacrifice, lowFriend, s.Self.HealthPercent > 80f)
                    ?? Do(s, LayOnHands, lowFriend, lowFriend.HealthPercent < 15f))
            ?? Do(s, HolyShield, s.Self, s.Attackers.Count > 0)
            ?? (FirstAttackerOtherThan(s, v) is { } undead
                ? Do(s, TurnUndead, undead, undead.CreatureType == 6 && s.Role != PlayerbotRole.Tank) : null);
        if (save is not null) return save;

        if (s.Role == PlayerbotRole.Healer)
        {
            RotationAction? heal = Do(s, HolyShock, s.Self, s.Self.HealthPercent < 50f)
                ?? HealInjuredAlly(s, 80f, 90f);
            if (heal is not null || !HealerMayFight(s)) return heal;
        }

        string seal = SealFor(s);
        bool hasSeal = s.Self.HasAura(seal);
        int close = AttackersWithin(s, 10f);
        return Do(s, LayOnHands, s.Self, s.Self.HealthPercent < 15f)
            ?? Do(s, seal, s.Self, !hasSeal)
            ?? Do(s, Judgement, v, hasSeal && s.PowerPercent > 30f && s.InCombat)
            ?? Do(s, HammerOfJustice, v, v.IsCasting || s.Self.HealthPercent < 20f && s.Attackers.Count > 0)
            ?? Do(s, HammerOfWrath, v, v.HealthPercent < 20f)
            ?? Do(s, Consecration, s.Self, close > 2)
            ?? Do(s, HolyShock, v)
            ?? Do(s, Exorcism, v, v.CreatureType == 6)
            ?? Do(s, HolyWrath, v, v.CreatureType is 3 or 6 && s.Attackers.Count < 3)
            ?? Do(s, BlessingOfFreedom, s.Self, s.IsRooted || s.IsSlowed)
            ?? (s.Role != PlayerbotRole.Healer && s.Self.HealthPercent < 30f ? HealInjured(s, s.Self) : null);
    }
}
