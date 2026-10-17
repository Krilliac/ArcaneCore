using ArcaneCore.Game;
using Form = ArcaneCore.Game.Spells.ShapeshiftForm;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>vmangos PartyBotAI::UpdateOutOfCombatAI_Warrior / UpdateInCombatAI_Warrior (PartyBotAI.cpp:2376-2667).</summary>
internal sealed class PlayerbotWarriorRotation : PlayerbotClassRotation
{
    internal const string BattleStance = "Battle Stance";
    internal const string DefensiveStance = "Defensive Stance";
    internal const string BerserkerStance = "Berserker Stance";
    internal const string BattleShout = "Battle Shout";
    internal const string Bloodrage = "Bloodrage";
    internal const string Charge = "Charge";
    internal const string Intercept = "Intercept";
    internal const string Pummel = "Pummel";
    internal const string ShieldBash = "Shield Bash";
    internal const string Execute = "Execute";
    internal const string Overpower = "Overpower";
    internal const string LastStand = "Last Stand";
    internal const string ConcussionBlow = "Concussion Blow";
    internal const string ShieldBlock = "Shield Block";
    internal const string ShieldWall = "Shield Wall";
    internal const string ShieldSlam = "Shield Slam";
    internal const string ThunderClap = "Thunder Clap";
    internal const string SunderArmor = "Sunder Armor";
    internal const string Hamstring = "Hamstring";
    internal const string Rend = "Rend";
    internal const string IntimidatingShout = "Intimidating Shout";
    internal const string Retaliation = "Retaliation";
    internal const string SweepingStrikes = "Sweeping Strikes";
    internal const string Recklessness = "Recklessness";
    internal const string DeathWish = "Death Wish";
    internal const string MortalStrike = "Mortal Strike";
    internal const string Bloodthirst = "Bloodthirst";
    internal const string Whirlwind = "Whirlwind";
    internal const string DemoralizingShout = "Demoralizing Shout";
    internal const string Cleave = "Cleave";
    internal const string HeroicStrike = "Heroic Strike";
    internal const string Taunt = "Taunt";
    internal const string MockingBlow = "Mocking Blow";
    internal const string Revenge = "Revenge";

    public override Class Class => Class.Warrior;

    public override IReadOnlyList<string> Abilities { get; } =
    [
        BattleStance, DefensiveStance, BerserkerStance, BattleShout, Bloodrage, Charge, Intercept, Pummel, ShieldBash, Execute,
        Overpower, LastStand, ConcussionBlow, ShieldBlock, ShieldWall, ShieldSlam, ThunderClap, SunderArmor, Hamstring, Rend,
        IntimidatingShout, Retaliation, SweepingStrikes, Recklessness, DeathWish, MortalStrike, Bloodthirst, Whirlwind,
        DemoralizingShout, Cleave, HeroicStrike, Taunt, MockingBlow, Revenge,
    ];

    protected override RotationAction? Upkeep(RotationState s)
    {
        // A tank keeps its defensive stance between fights; everyone else returns to Battle Stance (vmangos :2378-2383).
        RotationAction? stance = s.Role == PlayerbotRole.Tank && s.Spells.Has(DefensiveStance)
            ? Do(s, DefensiveStance, s.Self, s.Form != Form.DefensiveStance)
            : Do(s, BattleStance, s.Self, s.Form != Form.BattleStance);
        return stance
            ?? Do(s, BattleShout, s.Self)
            ?? Do(s, Bloodrage, s.Self, s.Spells.Has(BattleShout) && !s.Self.HasAura(BattleShout) && s.Power < 100);
    }

    protected override RotationAction? Fight(RotationState s, RotationUnit v)
    {
        if (!s.InCombat)
        {
            // The pull: Charge from Battle Stance (vmangos UpdateOutOfCombatAI_Warrior with a victim).
            return Do(s, Charge, v);
        }

        bool tank = s.Role == PlayerbotRole.Tank;
        bool shieldTank = s.Form == Form.DefensiveStance && s.WearsShield;
        int close = AttackersWithin(s, 10f);
        return Do(s, Taunt, v, NeedsTaunt(s, v))
            ?? Do(s, MockingBlow, v, NeedsTaunt(s, v))
            ?? Do(s, Pummel, v, v.IsCasting)
            ?? Do(s, ShieldBash, v, v.IsCasting && s.WearsShield)
            ?? Do(s, Execute, v, v.HealthPercent < 20f)
            ?? Do(s, Overpower, v)
            ?? Do(s, Revenge, v, tank)
            ?? Do(s, LastStand, s.Self, s.Self.HealthPercent < 20f)
            ?? Do(s, ConcussionBlow, v, v.IsCasting || v.IsMoving || s.Self.HealthPercent < 50f)
            ?? Do(s, ShieldBlock, s.Self, shieldTank && s.Attackers.Count > 0)
            ?? Do(s, ShieldWall, s.Self, shieldTank && s.Attackers.Count > 0 && s.Self.HealthPercent < 40f)
            ?? Do(s, ShieldSlam, v, shieldTank)
            ?? Do(s, ThunderClap, v, tank)
            ?? Do(s, SunderArmor, v, tank)
            ?? Do(s, Hamstring, v, v.IsMoving && !v.HasAura(Hamstring))
            ?? Do(s, Rend, v, v.CreatureType is not (4 or 6 or 11)) // elementals, undead and mechanicals do not bleed
            ?? Do(s, IntimidatingShout, v, s.Self.HealthPercent < 30f && close > 2)
            ?? Do(s, Retaliation, s.Self, close > 2)
            ?? Do(s, SweepingStrikes, s.Self, close > 2)
            ?? Do(s, Recklessness, s.Self, !tank && s.Self.HealthPercent > 60f && v.HealthPercent > 40f && !s.IsRooted)
            ?? Do(s, DeathWish, s.Self, !tank && s.Self.HealthPercent > 60f && v.HealthPercent > 40f && !s.IsRooted)
            ?? Do(s, MortalStrike, v)
            ?? Do(s, Bloodthirst, v)
            ?? Stance(s, v)
            ?? Do(s, Intercept, v)
            ?? Do(s, Whirlwind, s.Self, v.InMeleeRange)
            ?? Do(s, DemoralizingShout, s.Self, tank && !v.HasAura(DemoralizingShout))
            // Rage dump on the next swing above 30 rage (vmangos :2645-2663).
            ?? (s.Power > 300 ? Do(s, Cleave, v, close > 1) ?? Do(s, HeroicStrike, v) : null);
    }

    /// <summary>Defensive Stance when low or tanking an equal-level enemy, Berserker Stance otherwise (vmangos :2583-2600).</summary>
    private static RotationAction? Stance(RotationState s, RotationUnit v)
        => s.Self.HealthPercent < 20f || s.Role == PlayerbotRole.Tank && v.Level >= s.Self.Level
            ? Do(s, DefensiveStance, s.Self, s.Form != Form.DefensiveStance)
            : Do(s, BerserkerStance, s.Self, s.Form != Form.BerserkerStance);
}
