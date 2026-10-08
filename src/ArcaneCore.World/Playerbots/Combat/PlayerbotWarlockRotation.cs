using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>
/// vmangos PartyBotAI::UpdateOutOfCombatAI_Warlock / UpdateInCombatAI_Warlock (PartyBotAI.cpp:2172-2374) with
/// CombatBotBaseAI::SummonPetIfNeeded (CombatBotBaseAI.cpp:2364-2383).
/// </summary>
internal sealed class PlayerbotWarlockRotation : PlayerbotClassRotation
{
    internal const string DemonArmor = "Demon Armor";
    internal const string DemonSkin = "Demon Skin";
    internal const string SummonImp = "Summon Imp";
    internal const string SummonVoidwalker = "Summon Voidwalker";
    internal const string SummonSuccubus = "Summon Succubus";
    internal const string SummonFelhunter = "Summon Felhunter";
    internal const string DeathCoil = "Death Coil";
    internal const string Shadowburn = "Shadowburn";
    internal const string SearingPain = "Searing Pain";
    internal const string Banish = "Banish";
    internal const string Immolate = "Immolate";
    internal const string Conflagrate = "Conflagrate";
    internal const string Corruption = "Corruption";
    internal const string SiphonLife = "Siphon Life";
    internal const string DrainLife = "Drain Life";
    internal const string Fear = "Fear";
    internal const string CurseOfAgony = "Curse of Agony";
    internal const string HowlOfTerror = "Howl of Terror";
    internal const string ShadowBolt = "Shadow Bolt";
    internal const string LifeTap = "Life Tap";
    internal const string HealthFunnel = "Health Funnel";
    internal const string Shoot = "Shoot";

    public override Class Class => Class.Warlock;

    public override IReadOnlyList<string> Abilities { get; } =
    [
        DemonArmor, DemonSkin, SummonImp, SummonVoidwalker, SummonSuccubus, SummonFelhunter, DeathCoil, Shadowburn, SearingPain,
        Banish, Immolate, Conflagrate, Corruption, SiphonLife, DrainLife, Fear, CurseOfAgony, HowlOfTerror, ShadowBolt, LifeTap,
        HealthFunnel, Shoot,
    ];

    public override float PreferredRange(RotationState state) => CasterRange;

    protected override RotationAction? Upkeep(RotationState s)
        => (s.Spells.Has(DemonArmor) ? Do(s, DemonArmor, s.Self) : Do(s, DemonSkin, s.Self))
            ?? SummonDemon(s)
            ?? Do(s, HealthFunnel, s.PetUnit, s.Pet == RotationPetStatus.Alive && s.PetUnit?.HealthPercent < 40f && s.Self.HealthPercent > 60f)
            ?? Do(s, LifeTap, s.Self, s.PowerPercent < 40f && s.Self.HealthPercent > 90f)
            ?? CommandPet(s);

    /// <summary>
    /// A demon by role (vmangos picks one of the known summons at random): alone, the Voidwalker tanks for the warlock; in a group
    /// the Imp's Blood Pact and fire bolts serve best, then the Succubus and the Felhunter. The Imp needs no soul shard, so it is
    /// the fallback whenever a shard-costing summon cannot be cast.
    /// </summary>
    internal static RotationAction? SummonDemon(RotationState s)
    {
        if (s.Pet != RotationPetStatus.None) return null;
        string[] order = s.InGroup
            ? [SummonImp, SummonSuccubus, SummonFelhunter, SummonVoidwalker]
            : [SummonVoidwalker, SummonImp, SummonSuccubus, SummonFelhunter];
        foreach (string summon in order)
        {
            if (Do(s, summon, s.Self) is { } action) return action;
        }
        return null;
    }

    protected override RotationAction? Fight(RotationState s, RotationUnit v)
    {
        return CommandPet(s)
            ?? Do(s, DeathCoil, v, v.InMeleeRange && v.TargetsBot || v.IsCasting)
            ?? Do(s, Shadowburn, v, v.HealthPercent < 10f)
            ?? Do(s, SearingPain, v, v.HealthPercent < 20f)
            ?? (s.Attackers.Count > 1 && FirstAttackerOtherThan(s, v) is { } extra
                ? Do(s, Banish, extra, extra.Health > s.Self.Health && extra.CreatureType is 3 or 4) : null)
            ?? Do(s, Immolate, v)
            ?? Do(s, Conflagrate, v, v.HasAura(Immolate))
            ?? Do(s, Corruption, v)
            ?? Do(s, SiphonLife, v, s.Self.HealthPercent < 80f)
            ?? Do(s, DrainLife, v, s.Self.HealthPercent < 30f)
            // Fear sends the victim running into more enemies; only a group has someone to catch it (vmangos fears in a party).
            ?? Do(s, Fear, v, v.TargetsBot && s.InGroup)
            ?? Do(s, CurseOfAgony, v)
            ?? Do(s, HowlOfTerror, s.Self, AttackersWithin(s, 10f) > 1 && s.InGroup)
            ?? Do(s, ShadowBolt, v)
            ?? Do(s, LifeTap, s.Self, s.PowerPercent < 10f && s.Self.HealthPercent > 70f)
            ?? Wand(s, v, 5f);
    }
}
