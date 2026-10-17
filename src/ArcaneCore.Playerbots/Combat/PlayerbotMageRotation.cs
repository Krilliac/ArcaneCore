using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>vmangos PartyBotAI::UpdateOutOfCombatAI_Mage / UpdateInCombatAI_Mage (PartyBotAI.cpp:1691-1934).</summary>
internal sealed class PlayerbotMageRotation : PlayerbotClassRotation
{
    internal const string ArcaneIntellect = "Arcane Intellect";
    internal const string ArcaneBrilliance = "Arcane Brilliance";
    internal const string IceArmor = "Ice Armor";
    internal const string FrostArmor = "Frost Armor";
    internal const string IceBarrier = "Ice Barrier";
    internal const string Combustion = "Combustion";
    internal const string Pyroblast = "Pyroblast";
    internal const string PresenceOfMind = "Presence of Mind";
    internal const string IceBlock = "Ice Block";
    internal const string ManaShield = "Mana Shield";
    internal const string FrostNova = "Frost Nova";
    internal const string ConeOfCold = "Cone of Cold";
    internal const string BlastWave = "Blast Wave";
    internal const string ArcaneExplosion = "Arcane Explosion";
    internal const string Counterspell = "Counterspell";
    internal const string Polymorph = "Polymorph";
    internal const string ArcanePower = "Arcane Power";
    internal const string Scorch = "Scorch";
    internal const string Frostbolt = "Frostbolt";
    internal const string FireBlast = "Fire Blast";
    internal const string Fireball = "Fireball";
    internal const string Evocation = "Evocation";
    internal const string RemoveLesserCurse = "Remove Lesser Curse";
    internal const string Shoot = "Shoot";

    public override Class Class => Class.Mage;

    public override IReadOnlyList<string> Abilities { get; } =
    [
        ArcaneIntellect, ArcaneBrilliance, IceArmor, FrostArmor, IceBarrier, Combustion, Pyroblast, PresenceOfMind, IceBlock,
        ManaShield, FrostNova, ConeOfCold, BlastWave, ArcaneExplosion, Counterspell, Polymorph, ArcanePower, Scorch, Frostbolt,
        FireBlast, Fireball, Evocation, RemoveLesserCurse, Shoot,
    ];

    protected override IEnumerable<string> Dispels => [RemoveLesserCurse];

    public override float PreferredRange(RotationState state) => CasterRange;

    protected override RotationAction? Upkeep(RotationState s)
        => Buff(s, ArcaneIntellect, ArcaneBrilliance)
            // Frost Armor stands in for Ice Armor until it is learned (vmangos PopulateSpellData).
            ?? (s.Spells.Has(IceArmor) ? Do(s, IceArmor, s.Self) : Do(s, FrostArmor, s.Self))
            ?? Do(s, IceBarrier, s.Self);

    protected override RotationAction? Fight(RotationState s, RotationUnit v)
    {
        int close = AttackersWithin(s, 10f);
        bool mana50 = s.PowerPercent > 50f;
        return Do(s, Combustion, s.Self, s.InCombat)
            ?? Do(s, Pyroblast, v, s.Self.HasAura(PresenceOfMind) || !v.InCombat && v.MaxHealth > s.Self.MaxHealth)
            ?? Do(s, IceBlock, s.Self, s.Self.HealthPercent < 10f)
            ?? Do(s, ManaShield, s.Self, close > 0 && s.PowerPercent > 20f)
            ?? Do(s, FrostNova, s.Self, close > 0 && !v.HasAura(FrostNova) && s.Role != PlayerbotRole.MeleeDps)
            ?? Do(s, ConeOfCold, s.Self, close > 1 && !s.IsMoving)
            ?? Do(s, BlastWave, s.Self, close > 1)
            ?? Do(s, ArcaneExplosion, s.Self, close > 1)
            ?? Do(s, Counterspell, v, v.IsCasting)
            ?? (FirstAttackerOtherThan(s, v) is { } extra
                ? Do(s, Polymorph, extra, extra.HealthPercent > 20f && extra.CreatureType is 1 or 7 && !extra.HasAura(Polymorph)) : null)
            ?? Do(s, ArcanePower, s.Self, mana50 && s.InCombat)
            ?? Do(s, PresenceOfMind, s.Self, mana50 && s.InCombat)
            ?? Do(s, Scorch, v, v.HealthPercent < 20f)
            ?? Do(s, Frostbolt, v)
            ?? Do(s, FireBlast, v)
            ?? Do(s, Fireball, v)
            ?? Do(s, Evocation, s.Self, s.PowerPercent < 30f && close == 0 && s.InCombat)
            ?? Wand(s, v, 5f);
    }
}
