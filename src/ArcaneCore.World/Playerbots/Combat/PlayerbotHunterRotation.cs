using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>
/// vmangos PartyBotAI::UpdateOutOfCombatAI_Hunter / UpdateInCombatAI_Hunter (PartyBotAI.cpp:1510-1689) with
/// CombatBotBaseAI::SummonPetIfNeeded (CombatBotBaseAI.cpp:2333-2363): Auto Shot from beyond the 8-yard dead zone, shots and
/// stings, melee strikes once the victim is on the hunter, and the pet called, revived and sent at the hunter's target.
/// </summary>
internal sealed class PlayerbotHunterRotation : PlayerbotClassRotation
{
    internal const string AutoShot = "Auto Shot";
    internal const string AspectOfTheHawk = "Aspect of the Hawk";
    internal const string AspectOfTheMonkey = "Aspect of the Monkey";
    internal const string HuntersMark = "Hunter's Mark";
    internal const string ConcussiveShot = "Concussive Shot";
    internal const string AimedShot = "Aimed Shot";
    internal const string ArcaneShot = "Arcane Shot";
    internal const string SerpentSting = "Serpent Sting";
    internal const string MultiShot = "Multi-Shot";
    internal const string ScareBeast = "Scare Beast";
    internal const string Disengage = "Disengage";
    internal const string FeignDeath = "Feign Death";
    internal const string WingClip = "Wing Clip";
    internal const string MongooseBite = "Mongoose Bite";
    internal const string RaptorStrike = "Raptor Strike";
    internal const string CallPet = "Call Pet";
    internal const string RevivePet = "Revive Pet";
    internal const string MendPet = "Mend Pet";
    internal const string DistractingShot = "Distracting Shot";

    public override Class Class => Class.Hunter;

    public override IReadOnlyList<string> Abilities { get; } =
    [
        AutoShot, AspectOfTheHawk, AspectOfTheMonkey, HuntersMark, ConcussiveShot, AimedShot, ArcaneShot, SerpentSting, MultiShot,
        ScareBeast, Disengage, FeignDeath, WingClip, MongooseBite, RaptorStrike, CallPet, RevivePet, MendPet, DistractingShot,
    ];

    /// <summary>A hunter with a ranged weapon and Auto Shot stands off at 30 yards; without them it fights in melee.</summary>
    public override float PreferredRange(RotationState state)
        => state.HasRangedWeapon && state.Spells.Has(AutoShot) ? HunterRange : MeleeRange;

    protected override RotationAction? Upkeep(RotationState s)
        => Do(s, AspectOfTheHawk, s.Self)
            ?? Pet(s)
            ?? CommandPet(s);

    /// <summary>
    /// vmangos SummonPetIfNeeded for a hunter: a dead pet is revived, a stabled current pet is called. (vmangos also tames a
    /// random beast for a pet-less bot; an autonomous bot keeps to its own pet.)
    /// </summary>
    internal static RotationAction? Pet(RotationState s) => s.Pet switch
    {
        RotationPetStatus.Dead when s.PetUnit is { } dead => Do(s, RevivePet, dead),
        RotationPetStatus.None when s.CanCallPet => Do(s, CallPet, s.Self),
        RotationPetStatus.Alive when s.PetUnit is { } pet && pet.HealthPercent < 50f && !pet.HasAura(MendPet) => Do(s, MendPet, pet),
        _ => null,
    };

    protected override RotationAction? Fight(RotationState s, RotationUnit v)
    {
        bool outside = v.Distance >= HunterDeadZone;
        RotationUnit? close = s.Attackers.FirstOrDefault(a => a.IsAlive && a.Distance < HunterDeadZone);
        return CommandPet(s)
            ?? Do(s, HuntersMark, v, !s.InCombat)
            ?? Do(s, DistractingShot, v, NeedsTaunt(s, v))
            ?? Do(s, AutoShot, v, outside && !s.AutoRepeatActive && !s.IsMoving)
            ?? Do(s, ConcussiveShot, v, outside && v.IsMoving && v.TargetsBot)
            ?? Do(s, AimedShot, v, outside)
            ?? Do(s, ArcaneShot, v, outside)
            ?? Do(s, SerpentSting, v, outside && v.CreatureType is not (4 or 11)) // elementals and mechanicals are immune
            ?? Do(s, MultiShot, v, outside)
            ?? (close is null ? null
                : Do(s, ScareBeast, close, close.CreatureType == 1)
                    ?? Do(s, Disengage, close, s.InGroup)
                    ?? Do(s, AspectOfTheMonkey, s.Self, s.InCombat)
                    ?? Do(s, FeignDeath, s.Self, s.Self.HealthPercent < 20f && s.InGroup))
            // In the dead zone the hunter fights back in melee (vmangos :1640-1664).
            ?? (v.InMeleeRange
                ? Do(s, WingClip, v, !v.HasAura(WingClip)) ?? Do(s, MongooseBite, v) ?? Do(s, RaptorStrike, v)
                : Do(s, AspectOfTheHawk, s.Self, s.Self.HasAura(AspectOfTheMonkey) || !s.InCombat))
            ?? (s.Pet == RotationPetStatus.Alive && s.PetUnit is { } pet && pet.HealthPercent < 40f
                ? Do(s, MendPet, pet, !pet.HasAura(MendPet)) : null);
    }
}
