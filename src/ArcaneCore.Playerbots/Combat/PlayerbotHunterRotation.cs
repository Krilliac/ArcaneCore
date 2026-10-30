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
    internal const string TameBeast = "Tame Beast";
    internal const string RevivePet = "Revive Pet";
    internal const string MendPet = "Mend Pet";
    internal const string DistractingShot = "Distracting Shot";

    public override Class Class => Class.Hunter;

    public override IReadOnlyList<string> Abilities { get; } =
    [
        AutoShot, AspectOfTheHawk, AspectOfTheMonkey, HuntersMark, ConcussiveShot, AimedShot, ArcaneShot, SerpentSting, MultiShot,
        ScareBeast, Disengage, FeignDeath, WingClip, MongooseBite, RaptorStrike, CallPet, TameBeast, RevivePet, MendPet, DistractingShot,
    ];

    /// <summary>A hunter with a ranged weapon and Auto Shot stands off at 30 yards; without them it fights in melee.</summary>
    public override float PreferredRange(RotationState state)
        => state.HasRangedWeapon && state.Spells.Has(AutoShot) ? HunterRange : MeleeRange;

    protected override RotationAction? Upkeep(RotationState s)
        => Do(s, AspectOfTheHawk, s.Self)
            ?? Pet(s)
            // A pet attack that leads the out-of-combat think is the whole think: the brain returns before Auto Shot or a
            // swing. Standing on a pull whose shot is ready, that command (when it does not stick, so the pet never gains a
            // victim) holds goal Combat with nothing hitting the creature. The idle pet is sent beside the shot
            // (PetCompanion). While still closing in, the pet goes ahead.
            ?? (YieldsToShot(s) ? null : CommandPet(s));

    /// <summary>
    /// Standing on a live pull with a shot or strike ready: the think is that action, not the pet command.
    /// </summary>
    private bool YieldsToShot(RotationState s)
        => s.Victim is { IsAlive: true, IsCrowdControlled: false } pull && !s.IsMoving && Fight(s, pull) is not null;


    /// <summary>
    /// vmangos SummonPetIfNeeded for a hunter: a dead pet is revived, a stabled current pet is called. A pet-less hunter tames a beast in reach.
    /// </summary>
    internal static RotationAction? Pet(RotationState s) => s.Pet switch
    {
        RotationPetStatus.Dead => Do(s, RevivePet, s.PetUnit ?? s.Self),
        RotationPetStatus.None when s.CanCallPet => Do(s, CallPet, s.Self),
        // A hunter that never tamed (or abandoned) a pet tames a beast standing in reach (vmangos PartyBotAI tames for a pet-less bot).
        RotationPetStatus.None when s.TameCandidate is { } beast && !s.IsMoving => Do(s, TameBeast, beast),
        RotationPetStatus.Alive when s.PetUnit is { } pet && pet.HealthPercent < 50f && !pet.HasAura(MendPet) => Do(s, MendPet, pet),
        _ => null,
    };

    /// <summary>
    /// The shots come first and an idle pet is sent beside them (<see cref="PlayerbotClassRotation.SendIdlePet"/>): a pet command
    /// that leads the think and does not stick would take every think and no shot would fire.
    /// </summary>
    public override RotationAction? PetCompanion(RotationState state, RotationAction? chosen) => SendIdlePet(state, chosen);

    protected override RotationAction? Fight(RotationState s, RotationUnit v)
    {
        bool outside = v.Distance >= HunterDeadZone;
        RotationUnit? close = s.Attackers.FirstOrDefault(a => a.IsAlive && a.Distance < HunterDeadZone);
        return Do(s, DistractingShot, v, NeedsTaunt(s, v))
            ?? Do(s, AutoShot, v, outside && !s.AutoRepeatActive && !s.IsMoving)
            ?? Do(s, ConcussiveShot, v, outside && v.IsMoving && v.TargetsBot)
            ?? Do(s, AimedShot, v, outside)
            ?? Do(s, ArcaneShot, v, outside)
            ?? Do(s, SerpentSting, v, outside && v.CreatureType is not (4 or 9)) // elementals (4) and mechanicals (9) are immune to the poison (no server data carries it); totems are refused by CanCast
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
            // vmangos marks and then still runs UpdateInCombatAI_Hunter in the same call (PartyBotAI.cpp:1521-1537). One action per
            // think meant a hunter who was not in combat — every level 6-8 bot, Mark is known and a pet is not — returned only
            // Hunter's Mark. Update then returned true, so the brain never stopped for Auto Shot and never swung. Bot QA #398:
            // those hunters stood on Rockjaw Bonesnapper (1115) for 120s, attackerFacts=none, and never got a kill. Mark is the
            // pull only when no damaging shot or strike is ready, and not while still walking (a moving cast keeps the think and
            // Auto Shot, which requires standing, never becomes the action).
            ?? Do(s, HuntersMark, v, !s.InCombat && !s.IsMoving)
            ?? (s.Pet == RotationPetStatus.Alive && s.PetUnit is { } pet && pet.HealthPercent < 40f
                ? Do(s, MendPet, pet, !pet.HasAura(MendPet)) : null);
    }
}
