using ArcaneCore.Game;
using Form = ArcaneCore.Game.Spells.ShapeshiftForm;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>vmangos PartyBotAI::EnterCombatDruidForm / UpdateOutOfCombatAI_Druid / UpdateInCombatAI_Druid (PartyBotAI.cpp:2937-3351).</summary>
internal sealed class PlayerbotDruidRotation : PlayerbotClassRotation
{
    internal const string MarkOfTheWild = "Mark of the Wild";
    internal const string GiftOfTheWild = "Gift of the Wild";
    internal const string Thorns = "Thorns";
    internal const string NaturesGrasp = "Nature's Grasp";
    internal const string CatForm = "Cat Form";
    internal const string BearForm = "Bear Form";
    internal const string DireBearForm = "Dire Bear Form";
    internal const string MoonkinForm = "Moonkin Form";
    internal const string Prowl = "Prowl";
    internal const string Barkskin = "Barkskin";
    internal const string Hibernate = "Hibernate";
    internal const string Innervate = "Innervate";
    internal const string Pounce = "Pounce";
    internal const string Ravage = "Ravage";
    internal const string TigersFury = "Tiger's Fury";
    internal const string Cower = "Cower";
    internal const string FerociousBite = "Ferocious Bite";
    internal const string Rip = "Rip";
    internal const string FaerieFireFeral = "Faerie Fire (Feral)";
    internal const string Dash = "Dash";
    internal const string Shred = "Shred";
    internal const string Rake = "Rake";
    internal const string Claw = "Claw";
    internal const string FeralCharge = "Feral Charge";
    internal const string Bash = "Bash";
    internal const string FrenziedRegeneration = "Frenzied Regeneration";
    internal const string DemoralizingRoar = "Demoralizing Roar";
    internal const string Swipe = "Swipe";
    internal const string Maul = "Maul";
    internal const string Growl = "Growl";
    internal const string EntanglingRoots = "Entangling Roots";
    internal const string FaerieFire = "Faerie Fire";
    internal const string InsectSwarm = "Insect Swarm";
    internal const string Moonfire = "Moonfire";
    internal const string Starfire = "Starfire";
    internal const string Wrath = "Wrath";
    internal const string RemoveCurse = "Remove Curse";
    internal const string AbolishPoison = "Abolish Poison";
    internal const string CurePoison = "Cure Poison";

    public override Class Class => Class.Druid;

    public override IReadOnlyList<string> Abilities { get; } =
    [
        MarkOfTheWild, GiftOfTheWild, Thorns, NaturesGrasp, CatForm, BearForm, DireBearForm, MoonkinForm, Prowl, Barkskin, Hibernate,
        Innervate, Pounce, Ravage, TigersFury, Cower, FerociousBite, Rip, FaerieFireFeral, Dash, Shred, Rake, Claw, FeralCharge,
        Bash, FrenziedRegeneration, DemoralizingRoar, Swipe, Maul, Growl, EntanglingRoots, FaerieFire, InsectSwarm, Moonfire,
        Starfire, Wrath, RemoveCurse, AbolishPoison, CurePoison,
    ];

    protected override IEnumerable<string> Dispels => [AbolishPoison, CurePoison, RemoveCurse];

    private static bool Feral(Form form) => form is Form.Cat or Form.Bear or Form.DireBear;

    /// <summary>
    /// The least rage (power units, rage x10: 10 rage) that makes Frenzied Regeneration worth casting: its tick heals <c>rage * amount / 10</c> (Druid scripts,
    /// SpellAuras.cpp:1417-1433), so with no rage it heals nothing and only burns the cooldown.
    /// </summary>
    internal const uint FrenziedRegenerationMinRage = ArcaneCore.Game.Spells.Druid.DruidScriptsModule.MaxRageConverted;

    /// <summary>A druid alone, still classed as a healer because it has no Moonkin or Leader of the Pack, heals only below this.</summary>
    internal const float SoloEmergencyHealth = 30f;

    /// <summary>Cat and bear fight in melee; a caster druid (balance, restoration, or one without forms yet) casts from range.</summary>
    public override float PreferredRange(RotationState state)
        => Feral(state.Form) || state.Role is PlayerbotRole.MeleeDps or PlayerbotRole.Tank ? MeleeRange : CasterRange;

    /// <summary>vmangos EnterCombatDruidForm (:2937-2964): cat for damage, bear for a tank, moonkin for a caster.</summary>
    internal static RotationAction? EnterCombatForm(RotationState s)
    {
        if (s.Form != Form.None) return null;
        return Do(s, CatForm, s.Self, s.Role == PlayerbotRole.MeleeDps)
            ?? Do(s, DireBearForm, s.Self, s.Role is PlayerbotRole.Tank or PlayerbotRole.MeleeDps)
            ?? Do(s, BearForm, s.Self, s.Role is PlayerbotRole.Tank or PlayerbotRole.MeleeDps)
            ?? Do(s, MoonkinForm, s.Self, s.Role == PlayerbotRole.RangeDps);
    }

    protected override RotationAction? Upkeep(RotationState s)
    {
        if (s.Form == Form.None)
        {
            return Buff(s, MarkOfTheWild, GiftOfTheWild)
                ?? Buff(s, Thorns)
                ?? Do(s, NaturesGrasp, s.Self, s.Victim is not null)
                ?? (s.PowerPercent > 80f || s.Role == PlayerbotRole.Healer ? HealInjuredAlly(s) : null)
                ?? EnterCombatForm(s);
        }

        // A cat closing in on its prey prowls (vmangos EnterStealthIfNeeded with Prowl).
        return s.Form == Form.Cat && s.Victim is not null && !s.IsStealthed && !s.NoStealth ? Do(s, Prowl, s.Self) : null;
    }

    protected override RotationAction? Fight(RotationState s, RotationUnit v)
    {
        RotationAction? caster = Do(s, Barkskin, s.Self, s.Form is Form.None or Form.Moonkin && s.Self.HealthPercent < 50f && s.InCombat);
        if (caster is not null) return caster;

        if (s.Form == Form.None)
        {
            // Every druid without Moonkin Form or Leader of the Pack is a healer, including level 1-10 druids whose only
            // damage spell is Wrath. Healing whenever they were under 60% meant the whole fight was Healing Touch and the
            // quarry never died (bot QA combat stalls: Palemane #372, #443, Prairie Wolf #363, Gnarlpine #323).
            // Alone, only an emergency is a heal. A group still heals before it damages.
            bool heal = s.InGroup || s.Self.HealthPercent < SoloEmergencyHealth;
            RotationAction? support = (FirstAttackerOtherThan(s, null) is { } attacker && s.Role != PlayerbotRole.Tank && s.InGroup
                    ? Do(s, Hibernate, attacker, attacker.CreatureType is 1 or 2 && !attacker.HasAura(Hibernate)) : null)
                ?? (heal && SelectPeriodicHealTarget(s, 80f, 90f) is { } scratched && (s.Role == PlayerbotRole.Healer || scratched.IsSelf && s.Self.HealthPercent < 50f)
                    ? HealPeriodic(s, scratched) : null)
                ?? (heal && SelectHealTarget(s, 60f, 70f) is { } wounded && (s.Role == PlayerbotRole.Healer || wounded.IsSelf && s.Self.HealthPercent < 40f)
                    ? HealDirect(s, wounded) : null)
                ?? Do(s, Innervate, s.Self, s.InCombat && s.Self.HealthPercent > 40f && s.PowerPercent < 10f)
                ?? EnterCombatForm(s);
            if (support is not null) return support;
            if (s.InGroup && s.Role == PlayerbotRole.Healer && !HealerMayFight(s)) return null;
        }

        return s.Form switch
        {
            Form.Cat => CatFight(s, v),
            Form.Bear or Form.DireBear => BearFight(s, v),
            _ => CasterFight(s, v),
        };
    }

    private static RotationAction? CatFight(RotationState s, RotationUnit v)
    {
        if (s.IsStealthed)
            return Do(s, Pounce, v) ?? Do(s, Ravage, v) ?? Do(s, TigersFury, s.Self);
        bool finish = s.ComboPoints >= PlayerbotRogueRotation.FinisherPoints || s.ComboPoints >= 3 && v.HealthPercent < 25f;
        return Do(s, Cower, s.Self, AttackersWithin(s, 8f) > 0 && s.InGroup)
            ?? (finish ? Do(s, FerociousBite, v) ?? Do(s, Rip, v, s.ComboPoints >= PlayerbotRogueRotation.FinisherPoints) : null)
            ?? (!v.InMeleeRange ? Do(s, FaerieFireFeral, v) ?? Do(s, Dash, s.Self, v.IsMoving) : null)
            ?? Do(s, Shred, v)
            ?? Do(s, Rake, v)
            ?? Do(s, Claw, v);
    }

    private static RotationAction? BearFight(RotationState s, RotationUnit v)
        => Do(s, Growl, v, NeedsTaunt(s, v))
            ?? Do(s, FeralCharge, v, !v.InMeleeRange)
            ?? Do(s, Bash, v, v.IsCasting || s.InGroup)
            ?? Do(s, FrenziedRegeneration, s.Self, s.Self.HealthPercent < 30f && s.Power >= FrenziedRegenerationMinRage)
            ?? Do(s, FaerieFireFeral, v)
            ?? (s.Power > 800 || AttackersWithin(s, 10f) > 1
                ? Do(s, DemoralizingRoar, s.Self, !v.HasAura(DemoralizingRoar)) ?? Do(s, Swipe, v)
                : null)
            ?? Do(s, Maul, v);

    private static RotationAction? CasterFight(RotationState s, RotationUnit v)
        => Do(s, EntanglingRoots, v, v.InMeleeRange && v.TargetsBot && !s.IsRooted && s.InGroup)
            ?? Do(s, FaerieFire, v, v.Class == (byte)Class.Rogue)
            ?? Do(s, InsectSwarm, v)
            ?? Do(s, Moonfire, v)
            ?? Do(s, Starfire, v, v.HealthPercent > 50f)
            ?? Do(s, Wrath, v);
}
