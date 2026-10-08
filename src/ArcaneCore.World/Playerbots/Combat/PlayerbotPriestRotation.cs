using ArcaneCore.Game;
using Form = ArcaneCore.Game.Spells.ShapeshiftForm;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>vmangos PartyBotAI::UpdateOutOfCombatAI_Priest / UpdateInCombatAI_Priest (PartyBotAI.cpp:1936-2170).</summary>
internal sealed class PlayerbotPriestRotation : PlayerbotClassRotation
{
    internal const string PowerWordFortitude = "Power Word: Fortitude";
    internal const string PrayerOfFortitude = "Prayer of Fortitude";
    internal const string DivineSpirit = "Divine Spirit";
    internal const string PrayerOfSpirit = "Prayer of Spirit";
    internal const string ShadowProtection = "Shadow Protection";
    internal const string PrayerOfShadowProtection = "Prayer of Shadow Protection";
    internal const string InnerFire = "Inner Fire";
    internal const string PowerWordShield = "Power Word: Shield";
    internal const string WeakenedSoul = "Weakened Soul";
    internal const string Fade = "Fade";
    internal const string ShackleUndead = "Shackle Undead";
    internal const string InnerFocus = "Inner Focus";
    internal const string Shadowform = "Shadowform";
    internal const string Silence = "Silence";
    internal const string VampiricEmbrace = "Vampiric Embrace";
    internal const string MindBlast = "Mind Blast";
    internal const string ShadowWordPain = "Shadow Word: Pain";
    internal const string DevouringPlague = "Devouring Plague";
    internal const string PsychicScream = "Psychic Scream";
    internal const string ManaBurn = "Mana Burn";
    internal const string MindFlay = "Mind Flay";
    internal const string HolyNova = "Holy Nova";
    internal const string Smite = "Smite";
    internal const string Shoot = "Shoot";
    internal const string DispelMagicSpell = "Dispel Magic";
    internal const string AbolishDisease = "Abolish Disease";
    internal const string CureDisease = "Cure Disease";

    public override Class Class => Class.Priest;

    public override IReadOnlyList<string> Abilities { get; } =
    [
        PowerWordFortitude, PrayerOfFortitude, DivineSpirit, PrayerOfSpirit, ShadowProtection, PrayerOfShadowProtection, InnerFire,
        PowerWordShield, Fade, ShackleUndead, InnerFocus, Shadowform, Silence, VampiricEmbrace, MindBlast, ShadowWordPain,
        DevouringPlague, PsychicScream, ManaBurn, MindFlay, HolyNova, Smite, Shoot, DispelMagicSpell, AbolishDisease, CureDisease,
    ];

    protected override IEnumerable<string> Dispels => [DispelMagicSpell, AbolishDisease, CureDisease];

    public override float PreferredRange(RotationState state) => CasterRange;

    protected override RotationAction? Upkeep(RotationState s)
        => Buff(s, PowerWordFortitude, PrayerOfFortitude)
            ?? Buff(s, DivineSpirit, PrayerOfSpirit)
            ?? Buff(s, ShadowProtection, PrayerOfShadowProtection)
            ?? Do(s, InnerFire, s.Self)
            ?? (s.Role == PlayerbotRole.Healer ? HealInjuredAlly(s) : null);

    protected override RotationAction? Fight(RotationState s, RotationUnit v)
    {
        bool attacked = s.Attackers.Count > 0;
        RotationAction? defend = Do(s, PowerWordShield, s.Self, s.InCombat && !s.Self.HasAura(WeakenedSoul))
            ?? Do(s, Fade, s.Self, attacked && s.InGroup && s.Role != PlayerbotRole.Tank)
            ?? (FirstAttackerOtherThan(s, null) is { } attacker
                ? Do(s, ShackleUndead, attacker, attacker.CreatureType == 6 && attacker.Health > s.Self.Health && s.InGroup) : null)
            ?? Do(s, InnerFocus, s.Self, s.InCombat && s.PowerPercent < 50f);
        if (defend is not null) return defend;

        // vmangos heals as a healer, or when not fighting in Shadowform (:2063-2105): shield an attacked friend, a direct heal
        // for a serious wound, a renew for a light one.
        if (s.Role == PlayerbotRole.Healer || s.Form == Form.None)
        {
            RotationUnit? shieldTarget = s.Party.FirstOrDefault(m => m.IsAlive && m.Distance <= FriendlyRange && m.InCombat
                && m.HealthPercent < 80f && !m.HasAura(WeakenedSoul) && !m.HasAura(PowerWordShield));
            RotationAction? heal = Do(s, PowerWordShield, shieldTarget, s.Role == PlayerbotRole.Healer)
                ?? (SelectHealTarget(s, 60f, 80f) is { } wounded ? HealDirect(s, wounded) : null)
                ?? (SelectPeriodicHealTarget(s, 80f, 90f) is { } scratched ? HealPeriodic(s, scratched) : null);
            if (heal is not null || !HealerMayFight(s)) return heal;
        }

        return Do(s, Shadowform, s.Self, s.Form != Form.Shadow)
            ?? Do(s, Silence, v, v.IsCasting)
            ?? Do(s, VampiricEmbrace, v)
            ?? Do(s, MindBlast, v)
            ?? Do(s, ShadowWordPain, v)
            ?? Do(s, DevouringPlague, v)
            ?? Do(s, PsychicScream, s.Self, AttackersWithin(s, 10f) > 0 && s.InGroup)
            ?? Do(s, ManaBurn, v, UsesMana(v) && v.IsPlayer)
            ?? Do(s, MindFlay, v, AttackersWithin(s, 10f) == 0 || s.Self.HasAura(PowerWordShield))
            ?? (s.Form == Form.None
                ? Do(s, HolyNova, s.Self, AttackersWithin(s, 10f) > 2) ?? Do(s, Smite, v)
                : null)
            ?? Wand(s, v, 10f);
    }
}
