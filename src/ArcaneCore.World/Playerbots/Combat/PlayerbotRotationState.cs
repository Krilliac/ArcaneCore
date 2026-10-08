using ArcaneCore.Game;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>
/// One unit as a rotation sees it: the bot, its victim, a group member, the bot's pet or an attacker. A plain snapshot taken
/// on the world thread (<see cref="PlayerbotCombatView"/>), so a rotation is a pure function of it and unit tests can build one
/// directly.
/// </summary>
internal sealed record RotationUnit
{
    private static readonly IReadOnlySet<string> NoAuras = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public ObjectGuid Guid { get; init; }

    public bool IsSelf { get; init; }

    public bool IsPlayer { get; init; }

    /// <summary>The player class, or 0 for a creature.</summary>
    public byte Class { get; init; }

    public byte Level { get; init; } = 1;

    public uint Health { get; init; } = 100;

    public uint MaxHealth { get; init; } = 100;

    public float HealthPercent => MaxHealth == 0 ? 0f : Health * 100f / MaxHealth;

    public uint MissingHealth => MaxHealth > Health ? MaxHealth - Health : 0;

    public bool IsAlive { get; init; } = true;

    /// <summary>Distance from the bot in yards (0 for the bot itself).</summary>
    public float Distance { get; init; }

    /// <summary>Whether the bot reaches it with a melee swing.</summary>
    public bool InMeleeRange { get; init; }

    /// <summary>Casting or channelling a spell (vmangos IsNonMeleeSpellCasted).</summary>
    public bool IsCasting { get; init; }

    public bool IsMoving { get; init; }

    /// <summary>Its victim is the bot.</summary>
    public bool TargetsBot { get; init; }

    public bool InCombat { get; init; }

    /// <summary>CreatureType of a creature (6 undead, 3 demon, ...); 0 for players.</summary>
    public uint CreatureType { get; init; }

    public PowerType PowerType { get; init; }

    /// <summary>Wears a shield or is in a tanking stance/form (vmangos IsTankingForm / IsWearingShield).</summary>
    public bool IsTank { get; init; }

    public bool HasPeriodicHeal { get; init; }

    /// <summary>Dispel types (bit 1 &lt;&lt; dispel) of the harmful auras a friend could have removed.</summary>
    public uint HarmfulDispelMask { get; init; }

    /// <summary>Dispel types of the beneficial auras an enemy could have purged.</summary>
    public uint HelpfulDispelMask { get; init; }

    /// <summary>Names of the auras on the unit (rank-independent).</summary>
    public IReadOnlySet<string> Auras { get; init; } = NoAuras;

    public bool HasAura(string name) => Auras.Contains(name);
}

/// <summary>A carried consumable the rotation may use: bag position plus whether its cooldown allows it now.</summary>
internal sealed record RotationItem(ObjectGuid Guid, byte Bag, byte Slot, uint Entry);

internal enum RotationPetStatus : byte
{
    None,
    Alive,
    Dead,
}

/// <summary>Everything one rotation decision reads: the bot, its fight, its group and its resources.</summary>
internal sealed class RotationState
{
    public required PlayerbotAbilities Spells { get; init; }

    public required RotationUnit Self { get; init; }

    public PlayerbotRole Role { get; init; }

    /// <summary>The fight's target (in combat) or the creature the bot is about to pull (out of combat); null for none.</summary>
    public RotationUnit? Victim { get; init; }

    /// <summary>The other members of the bot's group on its map (players), nearest first.</summary>
    public IReadOnlyList<RotationUnit> Party { get; init; } = [];

    /// <summary>Units auto-attacking the bot.</summary>
    public IReadOnlyList<RotationUnit> Attackers { get; init; } = [];

    public bool InCombat { get; init; }

    public PowerType PowerType { get; init; }

    /// <summary>Current power in its stored unit (rage is stored times ten).</summary>
    public uint Power { get; init; }

    public uint MaxPower { get; init; }

    public float PowerPercent => MaxPower == 0 ? 0f : Power * 100f / MaxPower;

    public int ComboPoints { get; init; }

    public Game.Spells.ShapeshiftForm Form { get; init; }

    public bool IsMoving { get; init; }

    public bool IsRooted { get; init; }

    public bool IsSlowed { get; init; }

    public bool IsStealthed { get; init; }

    public bool WearsShield { get; init; }

    public RotationPetStatus Pet { get; init; }

    public RotationUnit? PetUnit { get; init; }

    /// <summary>The pet's victim is <see cref="Victim"/>.</summary>
    public bool PetOnVictim { get; init; }

    /// <summary>The pet is attacking something while the bot is out of combat.</summary>
    public bool PetFighting { get; init; }

    /// <summary>A hunter whose current pet is stabled away (Call Pet can bring it).</summary>
    public bool CanCallPet { get; init; }

    /// <summary>An auto-repeat spell (Auto Shot, wand Shoot) is running.</summary>
    public bool AutoRepeatActive { get; init; }

    public bool HasRangedWeapon { get; init; }

    public bool HasWand { get; init; }

    public RotationItem? HealingPotion { get; init; }

    public RotationItem? Bandage { get; init; }

    /// <summary>Names of the bot's live totems.</summary>
    public IReadOnlySet<string> Totems { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the bot is grouped (party members on its map, or not).</summary>
    public bool InGroup { get; init; }

    /// <summary>
    /// Whether the bot can cast <c>spell</c> at <c>unit</c> now (vmangos CombatBotBaseAI::CanTryToCastSpell): cooldowns, the global
    /// cooldown, power, aura states, form, reagents and range. Unit tests leave the permissive default.
    /// </summary>
    public Func<SpellInfo, RotationUnit, bool> CanCast { get; init; } = static (_, _) => true;

    /// <summary>The bot itself and its group members (self first).</summary>
    public IEnumerable<RotationUnit> Friends
    {
        get
        {
            yield return Self;
            foreach (RotationUnit member in Party) yield return member;
        }
    }
}

internal enum RotationActionKind : byte
{
    Cast,
    UseItem,
    PetAttack,
    PetFollow,
}

/// <summary>One decision of a rotation: cast a spell at a unit, use an item, or command the pet.</summary>
internal readonly record struct RotationAction(RotationActionKind Kind, SpellInfo? Spell, ObjectGuid Target, RotationItem? Item = null)
{
    public static RotationAction Cast(SpellInfo spell, RotationUnit target) => new(RotationActionKind.Cast, spell, target.Guid);

    public static RotationAction Use(RotationItem item, RotationUnit target) => new(RotationActionKind.UseItem, null, target.Guid, item);

    public static RotationAction PetAttack(RotationUnit target) => new(RotationActionKind.PetAttack, null, target.Guid);

    public static RotationAction PetFollow() => new(RotationActionKind.PetFollow, null, ObjectGuid.Empty);

    public override string ToString() => Kind switch
    {
        RotationActionKind.Cast => $"cast {Spell?.Name} ({Spell?.Id}) at {Target.Value:X}",
        RotationActionKind.UseItem => $"use item {Item?.Entry} at {Target.Value:X}",
        _ => $"{Kind} {Target.Value:X}",
    };
}
