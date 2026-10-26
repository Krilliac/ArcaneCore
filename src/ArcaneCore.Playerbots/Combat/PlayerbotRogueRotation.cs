using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>vmangos PartyBotAI::UpdateOutOfCombatAI_Rogue / UpdateInCombatAI_Rogue and ShouldEnterStealth (PartyBotAI.cpp:2669-2935).</summary>
internal sealed class PlayerbotRogueRotation : PlayerbotClassRotation
{
    internal const string Stealth = "Stealth";
    internal const string Premeditation = "Premeditation";
    internal const string Garrote = "Garrote";
    internal const string Ambush = "Ambush";
    internal const string CheapShot = "Cheap Shot";
    internal const string Vanish = "Vanish";
    internal const string Preparation = "Preparation";
    internal const string SliceAndDice = "Slice and Dice";
    internal const string Eviscerate = "Eviscerate";
    internal const string KidneyShot = "Kidney Shot";
    internal const string ExposeArmor = "Expose Armor";
    internal const string Rupture = "Rupture";
    internal const string Blind = "Blind";
    internal const string AdrenalineRush = "Adrenaline Rush";
    internal const string Gouge = "Gouge";
    internal const string Kick = "Kick";
    internal const string Evasion = "Evasion";
    internal const string ColdBlood = "Cold Blood";
    internal const string BladeFlurry = "Blade Flurry";
    internal const string Backstab = "Backstab";
    internal const string GhostlyStrike = "Ghostly Strike";
    internal const string Hemorrhage = "Hemorrhage";
    internal const string SinisterStrike = "Sinister Strike";
    internal const string Sprint = "Sprint";

    /// <summary>vmangos finishes at more than four combo points (PartyBotAI.cpp:2797).</summary>
    internal const int FinisherPoints = 5;

    public override Class Class => Class.Rogue;

    public override IReadOnlyList<string> Abilities { get; } =
    [
        Stealth, Premeditation, Garrote, Ambush, CheapShot, Vanish, Preparation, SliceAndDice, Eviscerate, KidneyShot, ExposeArmor,
        Rupture, Blind, AdrenalineRush, Gouge, Kick, Evasion, ColdBlood, BladeFlurry, Backstab, GhostlyStrike, Hemorrhage,
        SinisterStrike, Sprint, ShootBow, ShootGun, ShootCrossbow, Throw,
    ];

    /// <summary>
    /// Closer than this, Stealth is not an opener. A creature this near detects it before the next think, the aura never
    /// sticks, and the out-of-combat upkeep recasts it every think: the brain returns before the swing or the chase, so the
    /// bot stands on goal Grind. Issue #433: level 14-15 rogues grinding Young Goretusks (entry 454) in Westfall.
    /// </summary>
    internal const float StealthOpenYards = 10f;

    /// <summary>
    /// vmangos ShouldEnterStealth (:2669-2689): with a victim to open on (here: the creature the bot is closing in on), or
    /// nearly dead. A victim already inside <see cref="StealthOpenYards"/> is fought instead; recasting Stealth on top of it
    /// never lands.
    /// </summary>
    internal static bool ShouldEnterStealth(RotationState s)
    {
        if (s.InCombat) return false;
        if (s.Self.HealthPercent < 10f) return true;
        return s.Victim is { IsAlive: true } victim && victim.Distance > StealthOpenYards;
    }

    protected override RotationAction? Upkeep(RotationState s)
        => ShouldEnterStealth(s) && !s.IsStealthed ? Do(s, Stealth, s.Self) : null;

    protected override RotationAction? Fight(RotationState s, RotationUnit v)
    {
        if (s.IsStealthed && (s.Spells.Has(Garrote) || s.Spells.Has(Ambush) || s.Spells.Has(CheapShot)))
        {
            // Openers (vmangos :2741-2770): Garrote on a caster, else Ambush or Cheap Shot. A cast that cannot be made does
            // not end the decision: vmangos falls through to Sinister Strike while still stealthed (the strike breaks it).
            // Vanish and the defensive cooldowns stay in the not-stealthed branch (:2775, :2873).
            RotationAction? opener = Do(s, Premeditation, v)
                ?? Do(s, Garrote, v, UsesMana(v))
                ?? Do(s, Ambush, v, !UsesMana(v))
                ?? Do(s, CheapShot, v, !UsesMana(v))
                ?? Do(s, Garrote, v);
            if (opener is not null) return opener;
        }

        bool hidden = s.IsStealthed;
        return (hidden ? null : Do(s, Vanish, s.Self, s.InCombat && s.Self.HealthPercent < 10f))
            ?? (hidden ? null : Do(s, Preparation, s.Self, s.InCombat && s.Self.HealthPercent < 10f && s.Spells.Has(Vanish) && !s.CanCast(s.Spells[Vanish]!, s.Self)))
            ?? Finisher(s, v)
            ?? Do(s, Blind, FirstAttackerOtherThan(s, v), s.InGroup)
            ?? Do(s, AdrenalineRush, s.Self, s.InCombat && s.Power == 0)
            ?? Do(s, Gouge, v, v.IsCasting)
            ?? Do(s, Kick, v, v.IsCasting)
            ?? (hidden ? null : Do(s, Evasion, s.Self, s.InCombat && s.Self.HealthPercent < 80f && (AttackersWithin(s, 10f) > 2 || !UsesMana(v))))
            ?? (hidden ? null : Do(s, ColdBlood, s.Self, s.InCombat && s.ComboPoints >= FinisherPoints - 1))
            ?? (hidden ? null : Do(s, BladeFlurry, s.Self, s.InCombat && AttackersWithin(s, 10f) > 1))
            ?? Do(s, Backstab, v)
            ?? Do(s, GhostlyStrike, v)
            ?? Do(s, Hemorrhage, v)
            ?? Do(s, SinisterStrike, v)
            ?? Do(s, Sprint, s.Self, s.InCombat && !v.InMeleeRange && !s.IsRooted)
            ?? (s.InCombat ? RangedShot(s, v) : null);
    }

    /// <summary>
    /// At five points (vmangos :2797-2830): Slice and Dice first when it is missing and the victim is not nearly dead, else a
    /// damage finisher; a victim below a quarter of its health is finished from three points.
    /// </summary>
    internal static RotationAction? Finisher(RotationState s, RotationUnit v)
    {
        bool full = s.ComboPoints >= FinisherPoints;
        bool early = s.ComboPoints >= 3 && v.HealthPercent < 25f;
        if (!full && !early) return null;
        return Do(s, SliceAndDice, s.Self, full && !s.Self.HasAura(SliceAndDice) && v.HealthPercent > 10f)
            ?? Do(s, Eviscerate, v)
            ?? Do(s, Rupture, v, full)
            ?? Do(s, KidneyShot, v, full)
            ?? Do(s, ExposeArmor, v, full);
    }
}
