using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;

namespace ArcaneCore.Game.Spells;

/// <summary>Cast states (vmangos SpellState).</summary>
public enum SpellCastState : byte
{
    Preparing = 1,
    Casting = 2,
    Finished = 3,
}

/// <summary>One cast in flight (vmangos Spell): preparing (cast bar) → casting (channel) → finished.</summary>
public sealed class SpellCast
{
    internal SpellCast(SpellInfo spell, Unit caster, SpellCastTargets targets, bool triggered, int castTime, uint powerCost, int duration,
        SpellInfo? triggeringSpell = null, Item? castItem = null, byte itemSpellIndex = 0, byte itemBag = 0, byte itemSlot = 0,
        int? itemCooldownMs = null, int? itemCategoryCooldownMs = null, uint? itemCategory = null,
        IReadOnlyDictionary<int, int>? customAuraAmounts = null)
    {
        Spell = spell;
        Caster = caster;
        // vmangos copies its SpellCastTargets into each Spell. Selection may redirect or set a
        // destination, so keep those changes local when a caller reuses its request block.
        Targets = new SpellCastTargets
        {
            Mask = targets.Mask, Unit = targets.Unit, GameObject = targets.GameObject,
            Item = targets.Item, Corpse = targets.Corpse, Source = targets.Source,
            Dest = targets.Dest, Text = targets.Text,
        };
        IsTriggered = triggered;
        TriggeringSpell = triggeringSpell;
        CustomAuraAmounts = customAuraAmounts;
        CastItem = castItem;
        ItemSpellIndex = itemSpellIndex;
        ItemBag = itemBag;
        ItemSlot = itemSlot;
        ItemCooldownMs = itemCooldownMs;
        ItemCategoryCooldownMs = itemCategoryCooldownMs;
        ItemCategory = itemCategory;
        CastTime = castTime;
        Timer = castTime;
        PowerCost = powerCost;
        Duration = duration;
        CastX = caster.X;
        CastY = caster.Y;
        CastZ = caster.Z;
        CastO = caster.Orientation;
    }

    public SpellInfo Spell { get; }

    public Unit Caster { get; }

    public SpellCastTargets Targets { get; internal set; }

    /// <summary>SEND_EVENT is once per effect even when target selection supplies several units.</summary>
    internal HashSet<int> DispatchedEventEffects { get; } = [];

    // A redirected cast keeps the chosen unit for all its explicit enemy effects, even after
    // selection consumed the last magnet charge and removed the protection aura.
    internal Unit? MagnetTarget { get; set; }

    public bool IsTriggered { get; }

    /// <summary>Triggered by an aura (vmangos Spell::m_triggeredByAuraSpell): a periodic trigger tick. Such a cast takes no power.</summary>
    internal bool IsTriggeredByAura { get; init; }

    /// <summary>
    /// One shot of the auto-repeat spell (ranged (autorepeat lane)): a triggered copy of Auto Shot / Shoot cast by
    /// <see cref="SpellSystem"/> every weapon period. It sends no SMSG_SPELL_COOLDOWN (vmangos Player::AddCooldown only
    /// tells the client for COOLDOWN_ON_EVENT spells, Player.cpp:22139-22250), so the client's own timer keeps running.
    /// </summary>
    internal bool AutoRepeatShot { get; set; }

    /// <summary>How many targets the proc engine has seen for this cast (vmangos Spell::m_targetNum): a secondary target's melee ability counts as a spell for the attacker.</summary>
    internal int ProcTargetCount { get; set; }

    /// <summary>The original spell/aura behind a trigger (vmangos m_triggeredBySpellInfo).</summary>
    public SpellInfo? TriggeringSpell { get; }

    /// <summary>Per-effect aura values supplied by a narrow server-side triggered cast seam.</summary>
    internal IReadOnlyDictionary<int, int>? CustomAuraAmounts { get; }

    /// <summary>
    /// The item the spell is cast from (vmangos Spell::m_CastItem; CMSG_USE_ITEM, recipes, bandages, poisons), or null. Set at prepare and
    /// retained through delayed casts; cleared when the item is consumed as a reagent (Spell.cpp:7268-7290).
    /// </summary>
    public Item? CastItem { get; internal set; }

    internal bool IsItemEquipCast { get; set; }

    /// <summary>
    /// A spell the cast item carries that is not one of its on-use spells (a weapon's chance-on-hit spell, an enchantment's combat or equip
    /// spell): the on-use checks of the item (CanStartItemUse) do not apply.
    /// </summary>
    internal bool IsItemTriggeredCast { get; set; }

    /// <summary>Template spell slot selected by CMSG_USE_ITEM.</summary>
    public byte ItemSpellIndex { get; }

    public byte ItemBag { get; }

    public byte ItemSlot { get; }

    public int? ItemCooldownMs { get; }

    public int? ItemCategoryCooldownMs { get; }

    public uint? ItemCategory { get; }

    public SpellCastState State { get; internal set; } = SpellCastState.Preparing;

    /// <summary>
    /// The ShouldRemoveStealthAuras roll taken at cast start (vmangos Spell.cpp:3455), reused at completion unless
    /// <see cref="SpellSystem.ImprovedSapRollPerPhase"/>; null until the start phase ran.
    /// </summary>
    internal bool? RemoveStealthRoll { get; set; }

    /// <summary>Full cast time in ms.</summary>
    public int CastTime { get; }

    /// <summary>Time left in the current state (cast bar, then channel).</summary>
    public int Timer { get; internal set; }

    /// <summary>
    /// The power cost: computed at prepare without spending mod charges, and again at the end of the cast bar with them (vmangos
    /// Spell.cpp:3395 and :3646-3658, "in case of mana reduction buff proc while casting").
    /// </summary>
    public uint PowerCost { get; internal set; }

    /// <summary>The charged spell mods this cast spent (vmangos Spell::m_appliedMods); null when the caster holds no modifiers.</summary>
    internal Mods.SpellModScope? ModScope { get; set; }

    /// <summary>The aura/channel duration in ms, computed once at prepare (vmangos Spell::m_duration; -1 = permanent).</summary>
    public int Duration { get; }

    /// <summary>Whether the cast got through its checks and reached its effects (false when cancelled or failed).</summary>
    public bool Completed { get; internal set; }

    /// <summary>Damage pushbacks taken (cast bar or channel; vmangos m_delayAtDamageCount).</summary>
    public int PushbackCount { get; internal set; }

    internal float CastX { get; set; }

    internal float CastY { get; set; }

    internal float CastZ { get; set; }

    /// <summary>The caster's orientation when the cast (or the channel) started (vmangos m_castPosition.o, compared for the turning interrupt).</summary>
    internal float CastO { get; set; }

    /// <summary>Whether the caster moved more than 0.5 yd on any axis since the cast started (vmangos Spell::update).</summary>
    internal bool HasMoved
        => Math.Abs(Caster.X - CastX) > SpellConstants.MovementCancelThreshold
            || Math.Abs(Caster.Y - CastY) > SpellConstants.MovementCancelThreshold
            || Math.Abs(Caster.Z - CastZ) > SpellConstants.MovementCancelThreshold;
}

/// <summary>Per-unit spell state kept by <see cref="SpellSystem"/> (world thread).</summary>
public sealed class UnitSpellState
{
    internal UnitSpellState(Unit unit) => Unit = unit;

    public Unit Unit { get; }

    /// <summary>The cast in progress (one generic/channeled slot; the auto-repeat spell has its own, <see cref="AutoRepeatCast"/>).</summary>
    public SpellCast? CurrentCast { get; internal set; }

    /// <summary>
    /// The auto-repeat spell toggled on (vmangos CURRENT_AUTOREPEAT_SPELL; Auto Shot, wand Shoot). It never casts itself: it stays in
    /// the Preparing state until cancelled and <see cref="SpellSystem"/> fires a triggered copy of it each time the ranged swing
    /// timer is ready (vmangos Unit::_UpdateAutoRepeatSpell). Ranged (autorepeat lane).
    /// </summary>
    public SpellCast? AutoRepeatCast { get; internal set; }

    /// <summary>vmangos Unit::m_autoRepeatFirstCast: the next shot waits at least 500 ms (the wind-up) after a toggle, a cast or movement.</summary>
    internal bool AutoRepeatFirstCast { get; set; }

    /// <summary>The queued next-swing spell (vmangos CURRENT_MELEE_SPELL); it casts when the melee swing fires, see <see cref="SpellSystem.CastQueuedMeleeSpell"/>.</summary>
    public SpellCast? MeleeCast { get; internal set; }

    /// <summary>spell id → absolute expiry (WorldRuntime ms clock).</summary>
    internal Dictionary<uint, uint> SpellCooldowns { get; } = [];

    /// <summary>Spell.dbc Category → absolute expiry.</summary>
    internal Dictionary<uint, uint> CategoryCooldowns { get; } = [];

    /// <summary>StartRecoveryCategory → absolute expiry of the global cooldown.</summary>
    internal Dictionary<uint, uint> GlobalCooldowns { get; } = [];

    internal Dictionary<uint, ItemCooldownOwner> CooldownOwners { get; } = [];

    /// <summary>School → absolute end of an interrupt lockout (vmangos Unit::ProhibitSpellSchool).</summary>
    internal Dictionary<SpellSchool, uint> SchoolLockouts { get; } = [];

    internal List<SpellAuraHolder> Auras { get; } = [];

    public IReadOnlyList<SpellAuraHolder> AuraHolders => Auras;

    /// <summary>
    /// Milliseconds until the channel fields of a channel that ended normally are cleared (vmangos ChannelResetEvent and
    /// UNIT_STATE_PENDING_CHANNEL_RESET, Spell.cpp:4814-4820); 0 when no reset is pending.
    /// </summary>
    internal uint PendingChannelResetMs { get; set; }

    internal bool IsIdle => CurrentCast is null && MeleeCast is null && AutoRepeatCast is null && Auras.Count == 0 && SpellCooldowns.Count == 0
        && CategoryCooldowns.Count == 0 && GlobalCooldowns.Count == 0 && SchoolLockouts.Count == 0 && PendingChannelResetMs == 0;
}
