namespace ArcaneCore.Game.Spells.Rules.Diminishing;

/// <summary>
/// The diminishing-return group of a spell (vmangos SpellEntry::GetDiminishingReturnsGroup,
/// SpellEntry.cpp:281-390, the branch for client builds above 1.9.4, which is 1.12): explicit family and
/// spell cases first, then the strongest mechanic present. The family flag values are bit indices of
/// vmangos SpellClassMask.h (kidney shot 21, blind 24, freezing trap effect 3, warlock misc debuffs 31,
/// hamstring 1, frost shock 31).
/// </summary>
public static class DiminishingClassifier
{
    private const uint FamilyMage = 3;
    private const uint FamilyWarrior = 4;
    private const uint FamilyWarlock = 5;
    private const uint FamilyRogue = 8;
    private const uint FamilyHunter = 9;
    private const uint FamilyShaman = 11;

    private const ulong KidneyShot = 1UL << 21;
    private const ulong Blind = 1UL << 24;
    private const ulong FreezingTrapEffect = 1UL << 3;
    private const ulong WarlockMiscDebuffs = 1UL << 31;
    private const ulong Hamstring = 1UL << 1;
    private const ulong FrostShock = 1UL << 31;

    private const uint IceBlockVisual = 4325;
    private const uint Seduction = 6358;
    private const uint Impact = 12355;
    private const uint Pyroclasm = 18093;

    /// <param name="spell">The spell.</param>
    /// <param name="triggeredByAura">Whether an aura (a proc or a periodic trigger) cast the spell: stuns and roots then form their own "trigger" groups.</param>
    public static DiminishingGroup GetGroup(SpellInfo spell, bool triggeredByAura)
    {
        ArgumentNullException.ThrowIfNull(spell);
        switch (spell.SpellFamilyName)
        {
            case FamilyRogue:
                if ((spell.SpellFamilyFlags & KidneyShot) != 0)
                {
                    return DiminishingGroup.KidneyShot;
                }

                if ((spell.SpellFamilyFlags & Blind) != 0)
                {
                    return DiminishingGroup.None;
                }

                break;
            case FamilyHunter:
                if ((spell.SpellFamilyFlags & FreezingTrapEffect) != 0)
                {
                    return DiminishingGroup.Freeze;
                }

                break;
            case FamilyWarlock:
                if ((spell.SpellFamilyFlags & WarlockMiscDebuffs) != 0 && spell.Mechanic == (uint)SpellMechanic.Fear)
                {
                    return DiminishingGroup.WarlockFear;
                }

                if (spell.Id == Seduction)
                {
                    return DiminishingGroup.WarlockFear;
                }

                if ((spell.SpellFamilyFlags & WarlockMiscDebuffs) != 0)
                {
                    return DiminishingGroup.LimitOnly;
                }

                break;
            case FamilyWarrior:
                if ((spell.SpellFamilyFlags & Hamstring) != 0)
                {
                    return DiminishingGroup.LimitOnly;
                }

                break;
            case FamilyShaman:
                if ((spell.SpellFamilyFlags & FrostShock) != 0)
                {
                    return DiminishingGroup.ControlRoot;
                }

                break;
            case FamilyMage:
                if (spell.SpellVisual == IceBlockVisual)
                {
                    return DiminishingGroup.None;
                }

                break;
            case 0:
                if (spell.Id is Impact or Pyroclasm)
                {
                    return DiminishingGroup.TriggerStun;
                }

                break;
        }

        // Triggered by an aura, yet a controlled stun: Charge and the Intercept ranks.
        if (spell.Id is 7922 or 20253 or 20614 or 20615)
        {
            return DiminishingGroup.ControlStun;
        }

        uint mechanics = spell.AllMechanicMask();
        if (mechanics == 0)
        {
            return DiminishingGroup.None;
        }

        if ((mechanics & SpellMechanics.Mask(SpellMechanic.Stun)) != 0)
        {
            return triggeredByAura ? DiminishingGroup.TriggerStun : DiminishingGroup.ControlStun;
        }

        if ((mechanics & SpellMechanics.Mask(SpellMechanic.Sleep)) != 0)
        {
            return DiminishingGroup.Sleep;
        }

        if ((mechanics & SpellMechanics.Mask(SpellMechanic.Polymorph)) != 0)
        {
            return DiminishingGroup.Polymorph;
        }

        if ((mechanics & SpellMechanics.Mask(SpellMechanic.Root)) != 0)
        {
            return triggeredByAura ? DiminishingGroup.TriggerRoot : DiminishingGroup.ControlRoot;
        }

        if ((mechanics & SpellMechanics.Mask(SpellMechanic.Fear)) != 0)
        {
            return DiminishingGroup.Fear;
        }

        if ((mechanics & SpellMechanics.Mask(SpellMechanic.Charm)) != 0)
        {
            return DiminishingGroup.Charm;
        }

        if ((mechanics & SpellMechanics.Mask(SpellMechanic.Silence)) != 0)
        {
            return DiminishingGroup.Silence;
        }

        if ((mechanics & SpellMechanics.Mask(SpellMechanic.Disarm)) != 0)
        {
            return DiminishingGroup.Disarm;
        }

        if ((mechanics & SpellMechanics.Mask(SpellMechanic.Freeze)) != 0)
        {
            return DiminishingGroup.Freeze;
        }

        if ((mechanics & (SpellMechanics.Mask(SpellMechanic.Knockout) | SpellMechanics.Mask(SpellMechanic.Sapped))) != 0)
        {
            return DiminishingGroup.Knockout;
        }

        if ((mechanics & SpellMechanics.Mask(SpellMechanic.Banish)) != 0)
        {
            return DiminishingGroup.Banish;
        }

        return (mechanics & SpellMechanics.Mask(SpellMechanic.Horror)) != 0 ? DiminishingGroup.DeathCoil : DiminishingGroup.None;
    }
}
