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
        SinisterStrike, Sprint,
    ];

    /// <summary>
    /// vmangos ShouldEnterStealth (:2669-2689): with a victim to open on (here: the creature the bot is closing in on), or
    /// nearly dead.
    /// </summary>
    internal static bool ShouldEnterStealth(RotationState s)
        => !s.InCombat && (s.Victim is { IsAlive: true } || s.Self.HealthPercent < 10f);

    protected override RotationAction? Upkeep(RotationState s)
        => ShouldEnterStealth(s) && !s.IsStealthed ? Do(s, Stealth, s.Self) : null;

    protected override RotationAction? Fight(RotationState s, RotationUnit v)
    {
        if (s.IsStealthed && (s.Spells.Has(Garrote) || s.Spells.Has(Ambush) || s.Spells.Has(CheapShot)))
        {
            // Openers (vmangos :2741-2770): Garrote on a caster, else Ambush or Cheap Shot. A rogue that knows none yet
            // opens with its ordinary strikes below (which break the stealth).
            return Do(s, Premeditation, v)
                ?? Do(s, Garrote, v, UsesMana(v))
                ?? Do(s, Ambush, v, !UsesMana(v))
                ?? Do(s, CheapShot, v, !UsesMana(v))
                ?? Do(s, Garrote, v);
        }

        return Do(s, Vanish, s.Self, s.InCombat && s.Self.HealthPercent < 10f)
            ?? Do(s, Preparation, s.Self, s.InCombat && s.Self.HealthPercent < 10f && s.Spells.Has(Vanish) && !s.CanCast(s.Spells[Vanish]!, s.Self))
            ?? Finisher(s, v)
            ?? Do(s, Blind, FirstAttackerOtherThan(s, v), s.InGroup)
            ?? Do(s, AdrenalineRush, s.Self, s.InCombat && s.Power == 0)
            ?? Do(s, Gouge, v, v.IsCasting)
            ?? Do(s, Kick, v, v.IsCasting)
            ?? Do(s, Evasion, s.Self, s.InCombat && s.Self.HealthPercent < 80f && (AttackersWithin(s, 10f) > 2 || !UsesMana(v)))
            ?? Do(s, ColdBlood, s.Self, s.InCombat && s.ComboPoints >= FinisherPoints - 1)
            ?? Do(s, BladeFlurry, s.Self, s.InCombat && AttackersWithin(s, 10f) > 1)
            ?? Do(s, Backstab, v)
            ?? Do(s, GhostlyStrike, v)
            ?? Do(s, Hemorrhage, v)
            ?? Do(s, SinisterStrike, v)
            ?? Do(s, Sprint, s.Self, s.InCombat && !v.InMeleeRange && !s.IsRooted);
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
