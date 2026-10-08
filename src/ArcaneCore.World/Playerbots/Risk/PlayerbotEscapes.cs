using ArcaneCore.Game;

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

    /// <summary>Every escape name a bot may resolve from its spellbook.</summary>
    internal static IReadOnlyList<string> All { get; } =
    [
        FrostNova, Blink, Vanish, Sprint, Gouge, FeignDeath, ConcussiveShot, WingClip, AspectOfTheCheetah, PsychicScream,
        PowerWordShield, Fade, Fear, HowlOfTerror, DeathCoil, IntimidatingShout, Hamstring, EntanglingRoots, TravelForm, CatForm,
        Dash, HammerOfJustice, DivineShield, FrostShock,
    ];

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

    /// <summary>The first escape of the class that is ready (<paramref name="ready"/>: known, castable at its target, not used yet).</summary>
    internal static EscapeAction? Choose(EscapeSituation s, Func<EscapeAction, bool> ready)
    {
        foreach (EscapeAction action in Candidates(s))
            if (ready(action)) return action;
        return null;
    }
}
