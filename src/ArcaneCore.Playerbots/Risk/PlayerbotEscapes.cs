using ArcaneCore.Game;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Playerbots;

/// <summary>Who an escape is cast at.</summary>
internal enum EscapeTarget : byte
{
    Self,
    Victim,
}

/// <summary>One escape: the spell, its target, and whether the bot turns away from its enemies first (Blink goes forward).</summary>
internal readonly record struct EscapeAction(string Spell, EscapeTarget Target, bool FaceAway = false);

/// <summary>What the escape choice reads about the retreat (a plain snapshot).</summary>
internal readonly record struct EscapeSituation(Class Class, float HealthPct, bool InCombat, int AttackersWithin8, int AttackersWithin10,
    bool VictimInMelee, bool HasVictim, bool InCatForm);

/// <summary>
/// The 1.12 class escapes a retreating bot uses, in order, each at most once per retreat (pure). They follow the uses vmangos
/// PartyBotAI makes of the same spells (PartyBotAI.cpp: Feign Death below 20% health :1638-1645, Frost Nova and Blink when a
/// melee attacker reaches the mage :1786-1806, Vanish below 10% then RunAwayFromTarget :2777-2795, Sprint :2926-2933, Fade
/// :2016-2020, Psychic Scream :2116-2121, Fear :2323-2328, Hamstring and Intimidating Shout :2507-2530) and the cmangos/mangoszero
/// playerbot FleeStrategy ("flee" on critical health): crowd control or a threat drop first, then speed.
/// </summary>
internal static class PlayerbotEscapes
{
    internal const string FrostNova = "Frost Nova";
    internal const string Blink = "Blink";
    internal const string Vanish = "Vanish";
    internal const string Sprint = "Sprint";
    internal const string Gouge = "Gouge";
    internal const string FeignDeath = "Feign Death";
    internal const string ConcussiveShot = "Concussive Shot";
    internal const string WingClip = "Wing Clip";
    internal const string AspectOfTheCheetah = "Aspect of the Cheetah";
    internal const string PsychicScream = "Psychic Scream";
    internal const string PowerWordShield = "Power Word: Shield";
    internal const string Fade = "Fade";
    internal const string Fear = "Fear";
    internal const string HowlOfTerror = "Howl of Terror";
    internal const string DeathCoil = "Death Coil";
    internal const string IntimidatingShout = "Intimidating Shout";
    internal const string Hamstring = "Hamstring";
    internal const string EntanglingRoots = "Entangling Roots";
    internal const string TravelForm = "Travel Form";
    internal const string CatForm = "Cat Form";
    internal const string Dash = "Dash";
    internal const string HammerOfJustice = "Hammer of Justice";
    internal const string DivineShield = "Divine Shield";
    internal const string FrostShock = "Frost Shock";
    internal const string Frostbolt = "Frostbolt";
    internal const string ConeOfCold = "Cone of Cold";
    internal const string CurseOfExhaustion = "Curse of Exhaustion";

    /// <summary>Every escape name a bot may resolve from its spellbook.</summary>
    internal static IReadOnlyList<string> All { get; } =
    [
        FrostNova, Blink, Vanish, Sprint, Gouge, FeignDeath, ConcussiveShot, WingClip, AspectOfTheCheetah, PsychicScream,
        PowerWordShield, Fade, Fear, HowlOfTerror, DeathCoil, IntimidatingShout, Hamstring, EntanglingRoots, TravelForm, CatForm,
        Dash, HammerOfJustice, DivineShield, FrostShock,
    ];

    /// <summary>
    /// The victim snares only the kite uses (<see cref="PlayerbotKiteModel"/>). Kept out of <see cref="All"/> so the risk model's
    /// ready-escape count (<c>PlayerbotRisk.ReadyEscapes</c>) and the retreat's candidates are unchanged by the kite.
    /// </summary>
    internal static IReadOnlyList<string> KiteSlows { get; } = [Frostbolt, ConeOfCold, CurseOfExhaustion];

    /// <summary>Every name a bot resolves from its spellbook into its escape book: <see cref="All"/> and <see cref="KiteSlows"/>.</summary>
    internal static IReadOnlyList<string> Resolvable { get; } = [.. All, .. KiteSlows];

    /// <summary>The escapes of a class in the order they are tried.</summary>
    internal static IEnumerable<EscapeAction> Candidates(EscapeSituation s)
    {
        switch (s.Class)
        {
            case Class.Mage:
                if (s.AttackersWithin10 > 0) yield return new(FrostNova, EscapeTarget.Self);
                yield return new(Blink, EscapeTarget.Self, FaceAway: true);
                break;
            case Class.Rogue:
                yield return new(Vanish, EscapeTarget.Self);
                if (s.HasVictim && s.VictimInMelee) yield return new(Gouge, EscapeTarget.Victim);
                yield return new(Sprint, EscapeTarget.Self);
                break;
            case Class.Hunter:
                if (!s.InCombat) { yield return new(AspectOfTheCheetah, EscapeTarget.Self); break; }
                yield return new(FeignDeath, EscapeTarget.Self);
                if (s.HasVictim && s.VictimInMelee) yield return new(WingClip, EscapeTarget.Victim);
                if (s.HasVictim) yield return new(ConcussiveShot, EscapeTarget.Victim);
                break;
            case Class.Priest:
                if (s.AttackersWithin8 > 0) yield return new(PsychicScream, EscapeTarget.Self);
                yield return new(PowerWordShield, EscapeTarget.Self);
                yield return new(Fade, EscapeTarget.Self);
                break;
            case Class.Warlock:
                if (s.AttackersWithin10 > 1) yield return new(HowlOfTerror, EscapeTarget.Self);
                if (s.HasVictim) yield return new(Fear, EscapeTarget.Victim);
                if (s.HasVictim) yield return new(DeathCoil, EscapeTarget.Victim);
                break;
            case Class.Warrior:
                if (s.AttackersWithin8 > 0) yield return new(IntimidatingShout, EscapeTarget.Victim);
                if (s.HasVictim && s.VictimInMelee) yield return new(Hamstring, EscapeTarget.Victim);
                break;
            case Class.Druid:
                if (s.HasVictim) yield return new(EntanglingRoots, EscapeTarget.Victim);
                yield return new(TravelForm, EscapeTarget.Self);
                if (!s.InCatForm) yield return new(CatForm, EscapeTarget.Self);
                else yield return new(Dash, EscapeTarget.Self);
                break;
            case Class.Paladin:
                if (s.HealthPct < 20f) yield return new(DivineShield, EscapeTarget.Self);
                if (s.HasVictim && s.VictimInMelee) yield return new(HammerOfJustice, EscapeTarget.Victim);
                break;
            case Class.Shaman:
                if (s.HasVictim) yield return new(FrostShock, EscapeTarget.Victim);
                break;
        }
    }

    /// <summary>What kind of help an escape is. Only the kind is fixed here; every number comes from the spell the bot knows.</summary>
    internal enum EscapeKind : byte
    {
        /// <summary>Buys nothing for a retreat (a shield, a threat drop, a shapeshift that is not itself a speed-up).</summary>
        None,

        /// <summary>Keeps every pursuer it reaches off the bot for the spell's duration (a root, stun, fear or immunity around the caster).</summary>
        HoldsAll,

        /// <summary>Keeps its one target off the bot for the spell's duration.</summary>
        HoldsVictim,

        /// <summary>Contact is lost for good (stealth, a feigned death).</summary>
        BreaksContact,

        /// <summary>Speeds the bot up by the spell's speed aura.</summary>
        SelfSpeed,

        /// <summary>Slows its target by the spell's speed-reducing aura.</summary>
        VictimSnare,
    }

    /// <summary>The kind of each escape by spell name (a name that is not an escape is <see cref="EscapeKind.None"/>).</summary>
    internal static EscapeKind KindOf(string spell) => spell switch
    {
        FrostNova or PsychicScream or HowlOfTerror or IntimidatingShout or DivineShield => EscapeKind.HoldsAll,
        EntanglingRoots or Fear or Gouge or HammerOfJustice or DeathCoil => EscapeKind.HoldsVictim,
        Vanish or FeignDeath => EscapeKind.BreaksContact,
        // Travel Form (783) carries no speed aura of its own: the 40% is on a separate passive spell (5419), so it buys nothing here.
        Sprint or AspectOfTheCheetah or Dash => EscapeKind.SelfSpeed,
        Hamstring or WingClip or ConcussiveShot or FrostShock or Frostbolt or ConeOfCold or CurseOfExhaustion => EscapeKind.VictimSnare,
        _ => EscapeKind.None,
    };

    /// <summary>
    /// What an escape buys a retreat, in the terms of <see cref="PlayerbotRiskModel.RetreatSucceeds"/>, read from the spell data of the
    /// rank the bot knows: seconds the pursuers it reaches cannot hit the bot (the spell's duration, <see cref="SpellDuration.Base"/>;
    /// infinity for <see cref="EscapeKind.BreaksContact"/>), the bot's speed multiplier (the largest positive speed aura of the
    /// spell, <see cref="SpellInfo.SimpleValue"/> percent), the nearest pursuer's (the speed-reducing aura). A spell that did not
    /// resolve, or lacks the data its kind needs (no duration, no speed aura), buys nothing: no number is ever assumed.
    /// </summary>
    internal readonly record struct EscapeEffect(float FreeSeconds, bool AllPursuers, float SelfSpeed = 1f, float VictimSpeed = 1f,
        float CastSeconds = 0f)
    {
        /// <summary>Buys nothing.</summary>
        internal static EscapeEffect None { get; } = new(0f, false);
    }

    /// <summary>The effect of <paramref name="spell"/> as the escape named <paramref name="name"/> (<see cref="EscapeEffect.None"/> when it cannot be derived).</summary>
    internal static EscapeEffect EffectOf(string name, SpellInfo? spell)
    {
        if (spell is null) return EscapeEffect.None;
        switch (KindOf(name))
        {
            case EscapeKind.BreaksContact:
                return new(float.PositiveInfinity, true);
            case EscapeKind.HoldsAll or EscapeKind.HoldsVictim:
                return spell.Duration.Base > 0
                    ? new(spell.Duration.Base / 1000f, KindOf(name) == EscapeKind.HoldsAll,
                        CastSeconds: Math.Max(0, spell.CastTime.Base) / 1000f)
                    : EscapeEffect.None;
            case EscapeKind.SelfSpeed:
            {
                int percent = SpeedPercent(spell, AuraType.ModIncreaseSpeed, AuraType.ModSpeedAlways, AuraType.ModSpeedNotStack);
                return percent > 0 ? new(0f, true, SelfSpeed: 1f + (percent / 100f)) : EscapeEffect.None;
            }

            case EscapeKind.VictimSnare:
            {
                int percent = -SpeedPercent(spell, AuraType.ModDecreaseSpeed);
                return percent is > 0 and < 100 ? new(0f, false, VictimSpeed: 1f - (percent / 100f)) : EscapeEffect.None;
            }

            default:
                return EscapeEffect.None;
        }
    }

    /// <summary>The speed aura of the given types with the largest magnitude, as a signed percent (0: none).</summary>
    private static int SpeedPercent(SpellInfo spell, params AuraType[] types)
    {
        int best = 0;
        for (int index = 0; index < spell.Effects.Count; index++)
        {
            SpellEffectInfo effect = spell.Effects[index];
            if (effect.Effect != SpellEffectName.ApplyAura || Array.IndexOf(types, effect.AuraType) < 0) continue;
            int value = effect.BasePoints + effect.BaseDice;
            if (Math.Abs(value) > Math.Abs(best)) best = value;
        }

        return best;
    }

    /// <summary>The escapes of a retreat together: free seconds add up per reach, the fastest self speed and the strongest snare stand.</summary>
    internal static PlayerbotEscapeKit Kit(IEnumerable<(EscapeAction Action, SpellInfo? Spell)> ready)
    {
        float all = 0f, victim = 0f, self = 1f, snare = 1f, cast = 0f;
        foreach ((EscapeAction action, SpellInfo? spell) in ready)
        {
            EscapeEffect effect = EffectOf(action.Spell, spell);
            if (effect.AllPursuers) all += effect.FreeSeconds;
            else victim += effect.FreeSeconds;
            self = MathF.Max(self, effect.SelfSpeed);
            snare = MathF.Min(snare, effect.VictimSpeed);
            cast += effect.CastSeconds;
        }

        return new PlayerbotEscapeKit(all, victim, self, snare, cast);
    }

    /// <summary>The first escape of the class that is ready (<paramref name="ready"/>: known, castable at its target, not used yet).</summary>
    internal static EscapeAction? Choose(EscapeSituation s, Func<EscapeAction, bool> ready)
    {
        foreach (EscapeAction action in Candidates(s))
            if (ready(action)) return action;
        return null;
    }
}
