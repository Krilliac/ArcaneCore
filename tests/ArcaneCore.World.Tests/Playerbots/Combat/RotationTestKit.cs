using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots.Combat;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// Synthetic spellbooks and fight states for the pure rotation tests: a <see cref="RotationState"/> is a plain snapshot, so each
/// test states the bot, its victim and its friends directly and asks the class rotation for its next action.
/// </summary>
internal static class RotationTestKit
{
    internal static readonly ObjectGuid BotGuid = new(0x1);
    internal static readonly ObjectGuid VictimGuid = ObjectGuid.WithEntry(HighGuid.Unit, 990001, 1);

    private static uint s_nextId = 900_000;

    /// <summary>A hostile single-target damage spell.</summary>
    internal static SpellInfo Hostile(string name, int rank = 1, uint? id = null) => new()
    {
        Id = id ?? NextId(), Name = name, Rank = rank > 0 ? "Rank " + rank : string.Empty,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = 9 }],
    };

    /// <summary>A hostile aura (DoT, debuff, sting).</summary>
    internal static SpellInfo Debuff(string name, int rank = 1) => new()
    {
        Id = NextId(), Name = name, Rank = "Rank " + rank, Duration = new SpellDuration(15000, 0, 15000),
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.PeriodicDamage, TargetA = SpellImplicitTarget.UnitEnemy, Amplitude = 3000 }],
    };

    /// <summary>A beneficial aura on a friend or the caster (buff, stance, form, armor).</summary>
    internal static SpellInfo Aura(string name, int rank = 1, SpellImplicitTarget target = SpellImplicitTarget.UnitFriend) => new()
    {
        Id = NextId(), Name = name, Rank = rank > 0 ? "Rank " + rank : string.Empty, Duration = new SpellDuration(1_800_000, 0, 1_800_000),
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModResistance, TargetA = target, BasePoints = 10 }],
    };

    /// <summary>A self-only instant (cooldown, summon, totem).</summary>
    internal static SpellInfo SelfSpell(string name, SpellEffectName effect = SpellEffectName.Dummy) => new()
    {
        Id = NextId(), Name = name,
        Effects = [new SpellEffectInfo { Effect = effect, TargetA = SpellImplicitTarget.UnitCaster }],
    };

    /// <summary>A direct single-target heal of <paramref name="amount"/>.</summary>
    internal static SpellInfo Heal(string name, int amount, int rank = 1) => new()
    {
        Id = NextId(), Name = name, Rank = "Rank " + rank,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.Heal, TargetA = SpellImplicitTarget.UnitFriend, BasePoints = amount - 1, BaseDice = 1 }],
    };

    /// <summary>A heal-over-time of <paramref name="tick"/> every 3 s for 15 s.</summary>
    internal static SpellInfo Renew(string name, int tick) => new()
    {
        Id = NextId(), Name = name, Rank = "Rank 1", Duration = new SpellDuration(15000, 0, 15000),
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.PeriodicHeal, TargetA = SpellImplicitTarget.UnitFriend, BasePoints = tick - 1, BaseDice = 1, Amplitude = 3000 }],
    };

    /// <summary>A friendly dispel of <paramref name="dispelType"/> (1 magic, 2 curse, 3 disease, 4 poison).</summary>
    internal static SpellInfo Dispel(string name, int dispelType) => new()
    {
        Id = NextId(), Name = name,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.Dispel, TargetA = SpellImplicitTarget.UnitFriend, MiscValue = dispelType }],
    };

    internal static uint NextId() => Interlocked.Increment(ref s_nextId);

    internal static RotationUnit Self(byte playerClass, float healthPercent = 100f, params string[] auras) => new()
    {
        Guid = BotGuid, IsSelf = true, IsPlayer = true, Class = playerClass, Health = (uint)healthPercent, MaxHealth = 100,
        PowerType = playerClass is 1 ? PowerType.Rage : playerClass is 4 ? PowerType.Energy : PowerType.Mana,
        Auras = new HashSet<string>(auras, StringComparer.OrdinalIgnoreCase),
    };

    internal static RotationUnit Victim(float distance = 20f, float healthPercent = 100f, bool casting = false, bool moving = false,
        bool targetsBot = true, uint creatureType = 1, params string[] auras) => new()
    {
        Guid = VictimGuid, Distance = distance, InMeleeRange = distance <= 4f, Health = (uint)healthPercent, MaxHealth = 100,
        IsCasting = casting, IsMoving = moving, TargetsBot = targetsBot, InCombat = true, CreatureType = creatureType,
        PowerType = PowerType.Mana, Auras = new HashSet<string>(auras, StringComparer.OrdinalIgnoreCase),
    };

    internal static RotationUnit Member(ulong low, byte playerClass, float healthPercent = 100f, float distance = 10f, uint harmfulDispel = 0,
        params string[] auras) => new()
    {
        Guid = new ObjectGuid(low), IsPlayer = true, Class = playerClass, Health = (uint)healthPercent, MaxHealth = 100, Distance = distance,
        PowerType = playerClass is 1 or 4 ? PowerType.Rage : PowerType.Mana, HarmfulDispelMask = harmfulDispel,
        Auras = new HashSet<string>(auras, StringComparer.OrdinalIgnoreCase), InCombat = true,
    };

    /// <summary>A fight state over <paramref name="spells"/> resolved for <paramref name="rotation"/>; everything castable unless blocked.</summary>
    internal static RotationState State(PlayerbotClassRotation rotation, IEnumerable<SpellInfo> spells, RotationUnit self, RotationUnit? victim,
        PlayerbotRole role, bool inCombat = true, uint power = 1000, uint maxPower = 1000, IEnumerable<string>? blocked = null,
        Action<RotationStateBuilder>? more = null)
    {
        var builder = new RotationStateBuilder
        {
            Spells = PlayerbotAbilities.Resolve(spells, rotation.Abilities), Self = self, Victim = victim, Role = role, InCombat = inCombat,
            Power = power, MaxPower = maxPower, PowerType = self.PowerType,
        };
        var refused = new HashSet<string>(blocked ?? [], StringComparer.OrdinalIgnoreCase);
        builder.CanCast = (spell, _) => !refused.Contains(spell.Name);
        more?.Invoke(builder);
        return builder.Build();
    }

    /// <summary>The next action's spell name ("item", "pet attack", "pet follow" for the others; null for none).</summary>
    internal static string? Next(RotationAction? action) => action switch
    {
        null => null,
        { Kind: RotationActionKind.Cast } cast => cast.Spell!.Name,
        { Kind: RotationActionKind.UseItem } => "item",
        { Kind: RotationActionKind.PetAttack } => "pet attack",
        _ => "pet follow",
    };
}

/// <summary>A mutable stand-in for the init-only <see cref="RotationState"/>.</summary>
internal sealed class RotationStateBuilder
{
    public PlayerbotAbilities Spells { get; set; } = PlayerbotAbilities.Empty;
    public RotationUnit Self { get; set; } = new();
    public RotationUnit? Victim { get; set; }
    public PlayerbotRole Role { get; set; }
    public bool InCombat { get; set; }
    public PowerType PowerType { get; set; }
    public uint Power { get; set; }
    public uint MaxPower { get; set; }
    public List<RotationUnit> Party { get; } = [];
    public List<RotationUnit> Attackers { get; } = [];
    public int ComboPoints { get; set; }
    public Game.Spells.ShapeshiftForm Form { get; set; }
    public bool IsStealthed { get; set; }
    public bool WearsShield { get; set; }
    public bool IsMoving { get; set; }
    public RotationPetStatus Pet { get; set; }
    public RotationUnit? PetUnit { get; set; }
    public bool PetOnVictim { get; set; }
    public bool PetFighting { get; set; }
    public bool CanCallPet { get; set; }
    public bool AutoRepeatActive { get; set; }
    public bool HasRangedWeapon { get; set; }
    public bool HasWand { get; set; }
    public RotationItem? HealingPotion { get; set; }
    public RotationItem? Bandage { get; set; }
    public HashSet<string> Totems { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<SpellInfo, RotationUnit, bool> CanCast { get; set; } = static (_, _) => true;

    public RotationState Build() => new()
    {
        Spells = Spells, Self = Self, Victim = Victim, Role = Role, InCombat = InCombat, PowerType = PowerType, Power = Power, MaxPower = MaxPower,
        Party = Party, Attackers = Attackers, ComboPoints = ComboPoints, Form = Form, IsStealthed = IsStealthed, WearsShield = WearsShield,
        IsMoving = IsMoving, Pet = Pet, PetUnit = PetUnit, PetOnVictim = PetOnVictim, PetFighting = PetFighting, CanCallPet = CanCallPet, AutoRepeatActive = AutoRepeatActive,
        HasRangedWeapon = HasRangedWeapon, HasWand = HasWand, HealingPotion = HealingPotion, Bandage = Bandage, Totems = Totems,
        InGroup = Party.Count > 0, CanCast = CanCast,
    };
}
