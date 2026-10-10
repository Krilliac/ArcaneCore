using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Pets.Control;

/// <summary>
/// Charm and possession (docs/areas/unit-control.md): the aura handlers of SPELL_AURA_MOD_POSSESS (2), SPELL_AURA_MOD_CHARM (6),
/// SPELL_AURA_MOD_POSSESS_PET (128) and SPELL_AURA_AOE_CHARM (177), the charm cast checks, and the control primitives vmangos spreads
/// over Unit and Player: <c>SetCharm</c>/<c>SetCharmerGuid</c>, <c>Player::SetMover</c>, <c>Camera::SetView</c> (PLAYER_FARSIGHT),
/// <c>Unit::UpdateControl</c> and <c>Player::SetClientControl</c> (SMSG_CLIENT_CONTROL_UPDATE), <c>Possess/CharmSpellInitialize</c>
/// (SMSG_PET_SPELLS), <c>Unit::Uncharm</c> and <c>RemoveCharmAuras</c>, <c>RestoreFaction</c>. One instance serves every map; it is
/// created and installed by <see cref="SummonService"/>.
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed partial class CharmService
{
    /// <summary>The dummy auras of the spells with SPELL_EFFECT_SUMMON_POSSESSED (vmangos HandleModPossess / HandleModCharm remove them first).</summary>
    private static readonly uint[] SummonPossessedDummies = [126, 6272, 11403];

    /// <summary>vmangos HandleAuraAoeCharm (SpellAuras.cpp:5740-5748): only Chains of Kel'Thuzad charms.</summary>
    public const uint ChainsOfKelThuzad = 28410;

    /// <summary>Death Knight Understudy (Razuvious) casts Mind Exhaustion on itself when a possession ends (SpellAuras.cpp:3129-3133).</summary>
    public const uint DeathKnightUnderstudyEntry = 16803;

    public const uint MindExhaustionSpell = 29051;

    /// <summary>vmangos CREATURE_TYPE_DEMON.</summary>
    public const uint CreatureTypeDemon = 3;

    private readonly Func<Map, CreatureMapSystem?> _systems;
    private readonly Func<uint> _nextPetNumber;
    private readonly Random _random;
    private SpellSystem? _spells;
    private bool _installed;

    /// <param name="systems">The creature system of a map (re-creates a creature's AI when the charm ends).</param>
    /// <param name="nextPetNumber">vmangos ObjectMgr::GeneratePetNumber (a warlock's charmed demon gets one).</param>
    public CharmService(Func<Map, CreatureMapSystem?>? systems = null, Func<uint>? nextPetNumber = null, Random? random = null)
    {
        _systems = systems ?? (static map => map.FindUpdater<CreatureMapSystem>());
        _nextPetNumber = nextPetNumber ?? (static () => 0);
        _random = random ?? new Random();
    }

    /// <summary>
    /// vmangos <c>Creature::m_spells</c>: the creature's own spell slots, which a charm or possession puts on the controller's bar
    /// (<c>CharmInfo::InitCharmCreateSpells</c>, <c>InitPossessCreateSpells</c>). The repository has no general creature spell lists
    /// (vmangos <c>creature_spells</c> / <c>creature_template.spell_list_id</c>); the default only supplies Naxxramas
    /// understudies' two verified charm spells. Other bars hold the commands only.
    /// </summary>
    // vmangos sql/migrations/20260607172947_world.sql creature_charm_spells:
    // Razuvious' possessed understudy has Taunt (29060) and Shield Wall (29061).
    public Func<Creature, IReadOnlyList<uint>> CreatureSpells { get; set; } = static creature =>
        creature.Map?.MapId == 533 && creature.Entry == DeathKnightUnderstudyEntry
            ? [29060u, 29061u] : [];

    /// <summary>The summon service that owns this one (dismisses a pet before a charm with SPELL_ATTR_EX_DISMISS_PET_FIRST).</summary>
    public SummonService? Summons { get; internal set; }

    /// <summary>The installed spell system, or null before <see cref="Install"/>.</summary>
    public SpellSystem? Spells => _spells;

    /// <summary>Register the four control aura handlers and the charm cast check (once).</summary>
    public void Install(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        if (_installed)
        {
            return;
        }

        _spells = spells;
        spells.RegisterAura(AuraType.ModPossess, new AuraHandler(OnPossessAura, null));
        spells.RegisterAura(AuraType.ModCharm, new AuraHandler(OnCharmAura, null));
        spells.RegisterAura(AuraType.ModPossessPet, new AuraHandler(OnPossessPetAura, null));
        spells.RegisterAura(AuraType.AoeCharm, new AuraHandler(OnAoeCharmAura, null));
        spells.RegisterCastCheck(new CharmCastCheck(this));
        spells.RegisterEffect(SpellEffectName.SummonPossessed, EffectSummonPossessed);
        spells.UnitDied += OnUnitDied;
        spells.HolderAdded += OnHolderChanged;
        spells.HolderRemoved += OnHolderChanged;
        _installed = true;
    }

    // --- Uncharm / RemoveCharmAuras -------------------------------------------------------------------------

    /// <summary>
    /// vmangos Unit::Uncharm (Unit.cpp:4868-4880): the charm the unit holds loses its charm, possess and AoE charm auras, and its possess
    /// pet aura ("Pet possess is not a typical charm").
    /// </summary>
    public void Uncharm(Unit charmer)
    {
        ArgumentNullException.ThrowIfNull(charmer);
        if (_spells is not { } spells || charmer.GetCharm() is not { } charm)
        {
            return;
        }

        RemoveCharmAuras(charm);
        spells.RemoveAurasByType(charm, AuraType.ModPossessPet);
    }

    /// <summary>vmangos Unit::RemoveCharmAuras (Unit.cpp:4882-4887): the possess, charm and AoE charm auras on the unit.</summary>
    public void RemoveCharmAuras(Unit unit, AuraRemoveMode mode = AuraRemoveMode.Default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (_spells is not { } spells)
        {
            return;
        }

        foreach (SpellAuraHolder holder in spells.GetAuras(unit)
                     .Where(h => !h.IsRemoved && (h.HasAura(AuraType.ModPossess) || h.HasAura(AuraType.ModCharm) || h.HasAura(AuraType.AoeCharm)))
                     .ToArray())
        {
            spells.RemoveAuras(unit, holder.Spell.Id, mode);
        }
    }

    // --- control primitives ---------------------------------------------------------------------------------

    /// <summary>vmangos Unit::SetCharm (UNIT_FIELD_CHARM).</summary>
    internal static void SetCharm(Unit unit, Unit? charm) => unit.SetUInt64(UpdateFields.UnitFieldCharm, charm?.Guid.Value ?? 0);

    /// <summary>vmangos Unit::SetCharmerGuid (UNIT_FIELD_CHARMEDBY).</summary>
    internal static void SetCharmerGuid(Unit unit, ObjectGuid charmer) => unit.SetUInt64(UpdateFields.UnitFieldCharmedby, charmer.Value);

    /// <summary>vmangos Player::SetMover: null means the player itself.</summary>
    internal static void SetMover(Player player, Unit? mover)
        => UnitControl.State(player).Mover = mover is null || ReferenceEquals(mover, player) ? default : mover.Guid;

    /// <summary>vmangos Player::SetClientControl: SMSG_CLIENT_CONTROL_UPDATE.</summary>
    internal static void SetClientControl(Player player, Unit target, bool allowMove)
        => player.Session.Send(WorldOpcode.SmsgClientControlUpdate, CharmPackets.BuildClientControlUpdate(target.Guid, allowMove));

    /// <summary>
    /// vmangos Camera::SetView / ResetView (Camera.cpp:39-95): PLAYER_FARSIGHT names the view point and the map evaluates the player's
    /// visibility from there (<see cref="UnitControl.ViewerOf"/>).
    /// </summary>
    internal static void SetView(Player player, WorldObject? viewPoint)
    {
        player.SetUInt64(UpdateFields.PlayerFarsight, viewPoint is null || ReferenceEquals(viewPoint, player) ? 0 : viewPoint.Guid.Value);
        player.NeedsVisibilityUpdate = true;
    }

    /// <summary>vmangos UNIT_STATE_FLEEING | UNIT_STATE_CONFUSED, read from the flags the crowd-control auras own.</summary>
    private static bool LostControl(Unit unit) => (unit.UnitFlags & (UnitFlags.Fleeing | UnitFlags.Confused)) != 0;

    /// <summary>
    /// vmangos Unit::UpdateControl (Unit.cpp:10898-10917): tell the charming player whether it controls the unit, and a player whether
    /// it controls the unit it possesses or itself.
    /// </summary>
    public static void UpdateControl(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.GetCharmer() is Player charmer && charmer.CharmGuid == unit.Guid)
        {
            SetClientControl(charmer, unit, !LostControl(unit));
        }

        if (unit is Player me)
        {
            if (me.GetCharm() is { } possessed && possessed.CharmerGuid == me.Guid)
            {
                SetClientControl(me, possessed, !LostControl(possessed));
                return;
            }

            SetClientControl(me, me, !me.IsPossessedState && !LostControl(me) && me.CharmerGuid.IsEmpty);
        }
    }

    /// <summary>vmangos Unit::RestoreFaction (Unit.cpp:4949-4958): a player's race faction, a creature's template faction.</summary>
    internal static void RestoreFaction(Unit unit)
    {
        switch (unit)
        {
            case Player player:
                player.FactionTemplate = player.RaceFactionTemplate;
                break;
            case Creature creature:
                creature.FactionTemplate = creature.Template.Faction;
                break;
        }
    }

    /// <summary>vmangos Unit::InitCharmInfo: a pet keeps its own charm info; anything else gets a new one (passive until set).</summary>
    internal static CharmInfo InitCharmInfo(Unit unit)
    {
        if (unit is Creature { Summon.Charm: { } petCharm })
        {
            return petCharm;
        }

        UnitControlState state = UnitControl.State(unit);
        return state.Charm ??= new CharmInfo(ReactState.Passive) { Name = (unit as Creature)?.Template.Name ?? unit.Guid.ToString() };
    }

    /// <summary>vmangos Unit::ClearCharmInfo (never for a pet).</summary>
    internal static void ClearCharmInfo(Unit unit)
    {
        if (UnitControl.Find(unit) is { } state)
        {
            state.Charm = null;
        }
    }

    /// <summary>vmangos CharmInfo::InitEmptyActionBar (Unit.cpp:8362-8367): attack in slot 0, nine empty passive slots.</summary>
    internal static void InitEmptyActionBar(CharmInfo info)
    {
        info.SetActionBar(0, (uint)CommandState.Attack, (byte)ActionType.Command);
        for (int slot = 1; slot < CharmInfo.ActionBarSize; slot++)
        {
            info.SetActionBar(slot, 0, (byte)ActionType.Passive);
        }
    }

    /// <summary>vmangos SpellInternal::IsCharmSpell (SpellMgr.cpp:3291-3294).</summary>
    internal static bool IsCharmSpell(SpellInfo spell) => spell.HasAura(AuraType.ModCharm) || spell.HasAura(AuraType.ModPossess);

    /// <summary>
    /// vmangos CharmInfo::InitPossessCreateSpells (Unit.cpp:8369-8396): an empty bar; a creature's passive spells are cast on itself,
    /// the others go on the bar as passive buttons, except charm spells ("Charm spells on charmed creatures are no longer available
    /// to the players that charm them", 1.10.0).
    /// </summary>
    private void InitPossessCreateSpells(Unit unit, CharmInfo info)
    {
        InitEmptyActionBar(info);
        if (unit is not Creature creature || _spells is not { } spells)
        {
            return;
        }

        foreach (uint spellId in CreatureSpells(creature))
        {
            if (spellId == 0 || spells.Store.Get(spellId) is not { } spell)
            {
                continue;
            }

            if (spell.IsPassive)
            {
                spells.CastSpell(creature, spellId, SpellCastTargets.ForSelf(), triggered: true);
            }
            else if (!IsCharmSpell(spell))
            {
                info.AddSpellToActionBar(spellId, ActionType.Passive);
            }
        }
    }

    /// <summary>
    /// vmangos CharmInfo::InitCharmCreateSpells (Unit.cpp:8398-8455): the pet bar; a creature's passive spells are cast on itself, the
    /// others go on the bar, disabled when they target only the caster or are harmful, else passive; charm spells are left out. Returns
    /// the charm spell words (vmangos m_charmSpells) a warlock's charmed demon lists.
    /// </summary>
    private List<uint> InitCharmCreateSpells(Unit unit, CharmInfo info)
    {
        info.InitPetActionBar();
        var words = new List<uint>();
        if (unit is not Creature creature || _spells is not { } spells)
        {
            return words; // charmed players don't have spells
        }

        foreach (uint spellId in CreatureSpells(creature))
        {
            if (spellId == 0)
            {
                continue;
            }

            SpellInfo? spell = spells.Store.Get(spellId);
            if (spell is { IsPassive: true })
            {
                spells.CastSpell(creature, spellId, SpellCastTargets.ForSelf(), triggered: true);
                words.Add(ActionButton.Make(spellId, ActionType.Passive).Packed);
                continue;
            }

            if (spell is not null && IsCharmSpell(spell))
            {
                continue;
            }

            words.Add(ActionButton.Make(spellId, ActionType.Disabled).Packed);
            bool onlySelfCast = spell is not null
                && spell.Effects.All(e => e.TargetA is SpellImplicitTarget.UnitCaster or 0);
            ActionType state = onlySelfCast || spell is null || !spell.IsPositive ? ActionType.Disabled : ActionType.Passive;
            info.AddSpellToActionBar(spellId, state);
        }

        return words;
    }

    /// <summary>The remaining duration of the controller's aura of <paramref name="type"/> on the unit, 0 when none has one (SMSG_PET_SPELLS).</summary>
    private int ControlDuration(Unit unit, Unit controller, AuraType type)
        => _spells?.GetAuras(unit).FirstOrDefault(h => !h.IsRemoved && h.CasterGuid == controller.Guid && h.HasAura(type) && h.Duration > 0)?.Duration ?? 0;

    /// <summary>vmangos Player::PossessSpellInitialize (Player.cpp:17400-17437).</summary>
    internal void PossessSpellInitialize(Player player)
    {
        if (player.GetCharm() is not { } charm || charm.GetCharmInfo() is not { } info)
        {
            return;
        }

        player.Session.Send(WorldOpcode.SmsgPetSpells, CharmPackets.BuildPossessSpells(
            charm, info, ControlDuration(charm, player, AuraType.ModPossess), _spells?.GetActiveCooldowns(charm) ?? []));
    }

    /// <summary>vmangos Player::CharmSpellInitialize (Player.cpp:17439-17505): a warlock's charmed demon also lists its charm spells.</summary>
    internal void CharmSpellInitialize(Player player, IReadOnlyList<uint> charmSpellWords)
    {
        if (player.GetCharm() is not { } charm || charm.GetCharmInfo() is not { } info)
        {
            return;
        }

        bool listSpells = charm is Creature creature && creature.Template.CreatureType == CreatureTypeDemon && player.Class == Class.Warlock;
        player.Session.Send(WorldOpcode.SmsgPetSpells, CharmPackets.BuildCharmSpells(
            charm, info, ControlDuration(charm, player, AuraType.ModCharm), listSpells ? charmSpellWords : [], _spells?.GetActiveCooldowns(charm) ?? []));
    }

    /// <summary>vmangos Player::RemovePetActionBar.</summary>
    internal static void RemovePetActionBar(Player player)
        => player.Session.Send(WorldOpcode.SmsgPetSpells, PetPackets.BuildRemoveActionBar());

    /// <summary>
    /// vmangos BuildValuesUpdateBlockForPlayerWithFlags(UF_FLAG_OWNER_ONLY) after a charm or possession: the controller now receives the
    /// unit's owner-only fields (its stats), so they are queued for the next values update.
    /// </summary>
    private static void ForceOwnerOnlyFields(Unit unit)
    {
        ReadOnlySpan<ushort> flags = unit.FieldFlags;
        for (int index = 0; index < flags.Length; index++)
        {
            if ((flags[index] & (ushort)UpdateFieldFlags.OwnerOnly) != 0)
            {
                unit.ForceFieldUpdate(index);
            }
        }
    }

    /// <summary>
    /// vmangos SwitchAiAtControl + Creature::AIM_Initialize at the start of a charm: a creature that is not a pet takes PetAI (vmangos
    /// CreatureAISelector picks PetAI for a charmed creature, AI/CreatureAISelector.cpp:57-58); its own AI is kept for the end.
    /// </summary>
    private void SwitchToCharmedAi(Creature creature)
    {
        if (creature.AI is PetAI)
        {
            return;
        }

        UnitControl.State(creature).PreviousAi = creature.AI;
        creature.AI = new PetAI(creature, () => _spells, _random);
    }

    /// <summary>vmangos Creature::AIM_Initialize at the end of a charm: the creature system builds the template's AI again.</summary>
    private void RestoreAi(Creature creature)
    {
        if (creature.Summon?.Charm is not null)
        {
            return; // a pet keeps its PetAI
        }

        UnitControlState state = UnitControl.State(creature);
        CreatureAI? previous = state.PreviousAi;
        state.PreviousAi = null;
        if (creature.Map is { } map && _systems(map) is { } system && system.ReinitializeAi(creature))
        {
            return;
        }

        creature.AI = previous;
    }

    /// <summary>vmangos Unit::StopMoving and MotionMaster::Clear(false) + MoveIdle for a creature.</summary>
    private static void StopAndIdle(Unit unit)
    {
        if (unit is Creature creature)
        {
            creature.System?.StopMoving(creature);
            creature.Motion.Clear();
        }
    }

    /// <summary>vmangos Unit::CombatStop(true) + DeleteThreatList + HostileRefManager::deleteReferences.</summary>
    private static void StopAllFighting(Unit unit)
    {
        if (unit.Map is not { } map)
        {
            return;
        }

        map.Combat.CombatStop(unit);
        if (unit.Combat.HasThreatList)
        {
            unit.Combat.Threat.Clear();
        }

        HostileRefs.DeleteReferences(unit);
    }

    /// <summary>
    /// vmangos Unit::RemoveAttackersThreat (Unit.cpp:10802-10810): everyone fighting the unit forgets its threat and gets one point
    /// on <paramref name="owner"/> instead.
    /// </summary>
    private static void RemoveAttackersThreat(Unit unit, Unit? owner)
    {
        foreach (Unit attacker in unit.Combat.Attackers.ToArray())
        {
            attacker.Combat.Threat.ModifyThreatPercent(unit, -100);
            if (owner is not null && ThreatRules.CanHaveThreatList(attacker))
            {
                attacker.Combat.Threat.AddThreat(owner, 1.0f);
            }
        }
    }

    /// <summary>
    /// The end of a charm or possession for a creature: AttackedBy and threat equal to its maximum health on the former controller
    /// (vmangos HandleModPossess :3105-3114, HandleModCharm :3423-3428), unless it ended by the creature's death or the controller is
    /// no longer a valid target (gone, dead, friendly).
    /// </summary>
    private static void TurnOnFormerController(Creature creature, Unit? controller, AuraRemoveMode mode)
    {
        if (mode == AuraRemoveMode.Death || controller is null || !controller.IsInWorld || !controller.IsAlive || !creature.IsAlive
            || creature.Map is not { } map || !ReferenceEquals(controller.Map, map) || (controller is Player p && map.FindPlayer(p.Guid) is null)
            || !map.Combat.Hooks.CanAttack(creature, controller))
        {
            return;
        }

        creature.OnAttackedBy(controller);
        if (ThreatRules.CanHaveThreatList(creature))
        {
            creature.Combat.Threat.AddThreat(controller, creature.MaxHealth);
        }
    }

    /// <summary>
    /// The map control registry of a unit's map (null outside a map). It also relays the map's damage to the charmed creatures
    /// (<see cref="MapUnitControl"/>): the subscription lives with the map, so this world-wide service never holds one.
    /// </summary>
    private static MapUnitControl? Registry(Unit unit) => unit.Map?.FindUpdater<MapUnitControl>();

    // --- events ---------------------------------------------------------------------------------------------

    /// <summary>
    /// vmangos Player::SetDeathState(JUST_DIED) calls Uncharm (Player.cpp:1528); the dying charmed unit itself loses its charm auras with the
    /// rest of its auras (Unit::SetDeathState, RemoveCharmAuras(AURA_REMOVE_BY_DEATH), Unit.cpp:7318). A creature charmer is handled the same
    /// way here (vmangos lets the charmed player's PlayerControlledAI notice the dead controller one update later).
    /// </summary>
    private void OnUnitDied(Unit unit)
    {
        if (!unit.CharmGuid.IsEmpty)
        {
            Uncharm(unit);
        }
    }

    /// <summary>
    /// vmangos Unit::SetFeared / SetConfused call UpdateControl: a fear or confuse that lands on (or leaves) a controlled unit, or a unit that
    /// controls another, re-tells the controller whether it may move it.
    /// </summary>
    private static void OnHolderChanged(SpellAuraHolder holder)
    {
        if (!holder.HasAura(AuraType.ModFear) && !holder.HasAura(AuraType.ModConfuse))
        {
            return;
        }

        Unit unit = holder.Target;
        if (unit.IsInWorld && (unit.IsCharmed || (unit is Player && !unit.CharmGuid.IsEmpty)))
        {
            UpdateControl(unit);
        }
    }
}
