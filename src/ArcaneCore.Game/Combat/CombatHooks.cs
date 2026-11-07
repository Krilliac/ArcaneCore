using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// Optional per-creature combat traits. The creature class (creatures area) implements this;
/// a non-player unit that does not gets the vmangos defaults (no evade, can parry/block, can
/// score crushing blows, not a world boss, regenerates health).
/// </summary>
public interface ICombatCreature
{
    /// <summary>vmangos Creature::IsInEvadeMode: attacks against it evade and it cannot be attacked.</summary>
    bool IsInEvadeMode { get; }

    /// <summary>False with CREATURE_FLAG_EXTRA_NO_PARRY (vmangos RollMeleeOutcomeAgainst).</summary>
    bool CanParry { get; }

    /// <summary>False with CREATURE_FLAG_EXTRA_NO_BLOCK.</summary>
    bool CanBlock { get; }

    /// <summary>False for pets and CREATURE_STATIC_FLAG_2_NO_CRUSHING_BLOWS.</summary>
    bool CanCrush { get; }

    /// <summary>World bosses count as the target's level + 3 for skill (vmangos SpellCaster::GetLevelForTarget).</summary>
    bool IsWorldBoss { get; }

    /// <summary>vmangos Creature::IsRegeneratingHealth.</summary>
    bool RegeneratesHealth { get; }

    /// <summary>AI reaction to being attacked (vmangos CreatureAI::AttackedBy).</summary>
    void OnAttackedBy(Unit attacker);

    /// <summary>The creature died (vmangos CreatureAI::JustDied); loot, respawn timers etc. belong to the creature side.</summary>
    void OnJustDied(Unit? killer);

    /// <summary>
    /// vmangos Creature::CountDamageTaken (Creature.h:540-546), which Unit::DealDamage calls for every hit before the lethal check
    /// (Unit.cpp:789): <paramref name="fromPlayerOrSelf"/> when the attacker is a player, something a player charms or owns, or the
    /// creature itself. The default keeps no count.
    /// </summary>
    void CountDamageTaken(uint damage, bool fromPlayerOrSelf) { }

    /// <summary>vmangos Creature::ResetDamageTakenOrigin, at a real combat stop (Unit::AttackStop, Unit.cpp:4607-4611).</summary>
    void ResetDamageTakenOrigin() { }

    /// <summary>
    /// The start of vmangos Unit::Kill for a creature victim (Unit.cpp:987-1008), before any kill credit resolves: a corpse whose
    /// damage came mostly from non-players (Creature::IsLootAllowedDueToDamageOrigin, Creature.h:548-554) loses its loot recipient.
    /// </summary>
    void SettleKillCredit() { }
}

/// <summary>
/// The seams combat needs from areas it does not own: hostility (factions), equipment
/// (weapon skills, shields, off-hand), spells/auras (ghost form, resurrection sickness),
/// graveyards and kill rewards. Every member has a documented default so melee, death and
/// regeneration work on their own; the owning areas override them (see
/// docs/integration/combat.md).
/// </summary>
public class CombatHooks
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Maps.WorldRuntime, CombatHooks> s_registered = new();

    public static CombatHooks Default { get; } = new();

    /// <summary>Use <paramref name="hooks"/> for every map of <paramref name="world"/> (call before the world thread starts).</summary>
    public static void Register(Maps.WorldRuntime world, CombatHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(hooks);
        s_registered.AddOrUpdate(world, hooks);
    }

    /// <summary>
    /// Register <paramref name="hooks"/> only when nothing is registered for <paramref name="world"/> yet:
    /// the first registration wins and a later one returns false instead of silently replacing it
    /// (unlike <see cref="Register"/>, which is last-writer-wins). <see cref="Default"/> is never registered.
    /// </summary>
    public static bool TryRegister(Maps.WorldRuntime world, CombatHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(hooks);
        return !ReferenceEquals(hooks, Default) && s_registered.TryAdd(world, hooks);
    }

    /// <summary>The hooks registered for <paramref name="world"/>, or <see cref="Default"/>.</summary>
    public static CombatHooks For(Maps.WorldRuntime world) => s_registered.TryGetValue(world, out CombatHooks? hooks) ? hooks : Default;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Maps.WorldRuntime, Func<Player, Player, bool>> s_sameGroup = new();

    /// <summary>
    /// The group test the free-for-all rule needs (vmangos Unit::IsInRaidWith, Unit.h:1173, called by GetReactionTo at Object.cpp:3655): two players of one group stay friends in an arena.
    /// Per world; without one nobody is grouped. Called by the spell feature when it attaches and the social area is present.
    /// </summary>
    public static void RegisterSameGroup(Maps.WorldRuntime world, Func<Player, Player, bool> sameGroup)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(sameGroup);
        s_sameGroup.AddOrUpdate(world, sameGroup);
    }

    /// <summary>
    /// vmangos Unit::IsFriendlyTo. Default until faction templates are loaded: players of the
    /// same team are friendly; everything else is not.
    /// </summary>
    public virtual bool IsFriendly(Unit a, Unit b)
    {
        // Always friendly to a charmer, an owner or a unit they control, and between units with the same charmer or owner
        // (vmangos GetReactionTo, Object.cpp:3614-3617, before the duel clause).
        if (a.CharmerOrOwnerOrOwnGuid == b.CharmerOrOwnerOrOwnGuid)
        {
            return true;
        }

        // Duel opponents are hostile before any team rule (vmangos Object.cpp:3650-3652, "duel - always hostile to opponent").
        return !DuelRules.IsOpponentHostile(a, b) && a is Player pa && b is Player pb && PlayersFriendly(pa, pb);
    }

    /// <summary>True when either unit is under another unit's charm (UNIT_FIELD_CHARMEDBY set): the native-team shortcut no longer holds for it.</summary>
    internal static bool EitherCharmed(Unit a, Unit b) => !a.CharmerGuid.IsEmpty || !b.CharmerGuid.IsEmpty;

    /// <summary>
    /// True when each side of a charmed player pair resolves to a player (<see cref="DuelRules.ControllingPlayer"/>); a side
    /// charmed by a creature resolves to none, and the pair then goes by faction template or is not friendly.
    /// </summary>
    internal static bool BothResolveToPlayers(Unit a, Unit b) => DuelRules.ControllingPlayer(a) is not null && DuelRules.ControllingPlayer(b) is not null;

    /// <summary>
    /// Player pair friendliness (vmangos WorldObject::GetReactionTo, Object.cpp:3614-3666). Each side is resolved to the player it acts for
    /// (GetAffectingPlayer): a mind-controlled player acts for its charmer, whatever its own race says, and one charmed by a creature
    /// resolves to none and is not friendly. The player himself and a group mate are friends first; two free-for-all flagged players
    /// (an arena or a free-for-all realm) are then hostile whatever their teams; otherwise the same team is friendly.
    /// <see cref="Player.Team"/> never changes under charm, so the teams compared are those of the controlling players.
    /// </summary>
    private static bool PlayersFriendly(Player pa, Player pb)
    {
        if (DuelRules.ControllingPlayer(pa) is not { } ca || DuelRules.ControllingPlayer(pb) is not { } cb)
        {
            return false;
        }

        if (ReferenceEquals(ca, cb))
        {
            return true;
        }

        // The cheap tests come first: the group answer can only change the result when the pair is otherwise not friendly (two
        // free-for-all players, or two teams), so the common same-team pair never reads the group manager. CanAttack runs on map
        // threads; the group manager is written by packet handlers and bots under the world's serial gate (in-world packets run inside
        // Map.Update), so its reads and writes must both be inside that gate or on the world thread. InSameGroup enforces that: a read
        // from a thread outside the gate during the parallel phase is reported and made to take the gate (WorldRuntime.EnterSerialForRead).
        if (!AreBothFreeForAll(ca, cb) && ca.Team == cb.Team)
        {
            return true;
        }

        return InSameGroup(pa, ca, cb);
    }

    /// <summary>
    /// The victim's own state allows an attack whoever the attacker is (the flag mask of vmangos IsValidAttackTarget, a GM in GM mode,
    /// a creature in evade mode): not non-attackable, spawning, on a taxi, not selectable. <see cref="CanAttack"/> and the magnet
    /// redirect share it (mangos-classic Unit::CanAttack, Relations.cpp:351).
    /// </summary>
    internal static bool IsAttackableState(Unit victim)
    {
        const UnitFlags notAttackable = UnitFlags.NonAttackable2 | UnitFlags.Spawning | UnitFlags.TaxiFlight
            | UnitFlags.NotAttackable1 | UnitFlags.NotSelectable;
        return (victim.UnitFlags & notAttackable) == 0
            && victim is not Player { IsGameMaster: true }
            && victim is not ICombatCreature { IsInEvadeMode: true };
    }

    /// <summary>
    /// UNIT_FLAG_IMMUNE_TO_PLAYER / UNIT_FLAG_IMMUNE_TO_NPC in the attack check (mangos-classic Unit::CanAttack, Relations.cpp:376-385): a
    /// victim immune to players refuses an attacker a player controls (acts for), one immune to NPCs refuses every other attacker; the same
    /// test with the roles swapped on the attacker's own flags, judged by who controls the victim. The shape of
    /// <see cref="ArcaneCore.Game.Combat.Threat.ThreatRules.CanPullPetOwner"/>.
    /// </summary>
    private static bool IsBlockedByImmuneToFlags(Unit attacker, Unit victim)
    {
        if (t_ignoreImmuneToFlags > 0)
        {
            return false;
        }

        if (((attacker.UnitFlags | victim.UnitFlags) & (UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc)) == 0)
        {
            return false;
        }

        UnitFlags victimBlocks = attacker.IsCharmerOrOwnerPlayerOrPlayerItself ? UnitFlags.ImmuneToPlayer : UnitFlags.ImmuneToNpc;
        UnitFlags attackerBlocks = victim.IsCharmerOrOwnerPlayerOrPlayerItself ? UnitFlags.ImmuneToPlayer : UnitFlags.ImmuneToNpc;
        return (victim.UnitFlags & victimBlocks) != 0 || (attacker.UnitFlags & attackerBlocks) != 0;
    }

    [ThreadStatic]
    private static int t_ignoreImmuneToFlags;

    /// <summary>
    /// <see cref="CanAttack"/> without the ImmuneToPlayer / ImmuneToNpc clause, for a script-forced attack and for keeping a started fight.
    /// mangos-classic <c>UnitAI::AttackStart</c> (UnitAI.cpp:283-294) goes straight to <c>Unit::Attack</c>, which has no CanAttack or immunity check
    /// (Unit.cpp:6259), and vmangos silithus.cpp:1463-1470 fights with UNIT_FLAG_IMMUNE_TO_NPC set. On-sight, assist, target-selection and spell paths
    /// keep using <see cref="CanAttack"/>. Runs on the calling thread only; the bypass is scoped to this call (virtual overrides are included).
    /// </summary>
    public bool CanAttackIgnoringImmuneToFlags(Unit attacker, Unit victim)
    {
        t_ignoreImmuneToFlags++;
        try
        {
            return CanAttack(attacker, victim);
        }
        finally
        {
            t_ignoreImmuneToFlags--;
        }
    }

    private static bool InSameGroup(Unit mapOf, Player a, Player b)
    {
        if (mapOf.Map?.World is not { } world || !s_sameGroup.TryGetValue(world, out Func<Player, Player, bool>? sameGroup))
        {
            return false;
        }

        using Maps.WorldRuntime.SerialScope gate = world.EnterSerialForRead("GroupManager");
        return sameGroup(a, b);
    }

    /// <summary>
    /// The free-for-all hostile clause of GetReactionTo (vmangos Object.cpp:3661-3662) for any two units: the players they act for
    /// (<see cref="DuelRules.ControllingPlayer"/>) are two different, ungrouped players who both carry the free-for-all flag. Hunter traps,
    /// pets and totems of such a player therefore treat the other one's side as hostile whatever the teams. False when a side acts for no player.
    /// </summary>
    internal static bool AreFreeForAllOpponents(Unit a, Unit b)
        => DuelRules.ControllingPlayer(a) is { } ca && DuelRules.ControllingPlayer(b) is { } cb
            && !ReferenceEquals(ca, cb) && AreBothFreeForAll(ca, cb) && !InSameGroup(a, ca, cb);

    /// <summary>Both players carry the free-for-all PvP flag (vmangos IsFFAPvP on each, GetReactionTo and IsValidAttackTarget).</summary>
    private static bool AreBothFreeForAll(Player a, Player b)
        => (a.Flags & PlayerFlags.FfaPvp) != 0 && (b.Flags & PlayerFlags.FfaPvp) != 0;

    /// <summary>
    /// vmangos Unit::IsHostileTo (GetReactionTo(target) &lt;= REP_HOSTILE): the FACTION reaction, which unlike
    /// <see cref="CanAttack"/> is false for neutral units. Without faction templates the reaction is unknown, so
    /// the default falls back to <see cref="CanAttack"/> (a documented seam limit; <see cref="FactionCombatHooks"/>
    /// supplies the real reaction).
    /// </summary>
    public virtual bool IsHostileTo(Unit a, Unit b) => CanAttack(a, b);

    /// <summary>
    /// Whether <paramref name="attacker"/> may attack <paramref name="victim"/> (the parts of
    /// vmangos Unit::IsValidAttackTarget / IsTargetableBy that do not need factions): both in
    /// the same map, the victim in the world and not flagged non-attackable, not a GM in GM mode, not
    /// friendly, and an enemy player must be PvP-flagged.
    /// </summary>
    public virtual bool CanAttack(Unit attacker, Unit victim)
    {
        if (ReferenceEquals(attacker, victim) || !victim.IsInWorld || !ReferenceEquals(attacker.Map, victim.Map))
        {
            return false;
        }

        if (!IsAttackableState(victim))
        {
            return false;
        }

        if (IsBlockedByImmuneToFlags(attacker, victim))
        {
            return false;
        }

        if (IsFriendly(attacker, victim))
        {
            return false;
        }

        // Player vs player outside duels needs the victim flagged for PvP (vmangos
        // Unit::IsValidAttackTarget → player targets must be IsPvP unless FFA/duel). A started duel is
        // checked first and does not test Finished (Object.cpp:3797-3800), so it holds for the whole finishing tick.
        if (DuelRules.ControllingPlayer(attacker) is { } pa && DuelRules.ControllingPlayer(victim) is { } pv && !ReferenceEquals(pa, pv) && (pv.UnitFlags & UnitFlags.Pvp) == 0 && !AreBothFreeForAll(pa, pv) && !DuelRules.IsInDuelWith(pa, pv))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// The unit's skill with the weapon in this slot (vmangos SpellCaster::GetWeaponSkillValue).
    /// Default: a player with attached skills (<see cref="Player.Skills"/>) uses <see cref="PlayerCombatSkills"/>;
    /// without them players are treated as having maxed unarmed/weapon skill (level × 5); other units use
    /// level × 5 (GetUnitMeleeSkill), raised for world bosses.
    /// </summary>
    public virtual int GetWeaponSkill(Unit unit, WeaponAttackType attackType, Unit? victim)
    {
        if (unit is Player { Skills: { } skills } skilled)
        {
            return PlayerCombatSkills.WeaponSkill(skilled, skills, attackType);
        }

        if (unit is Player && attackType != WeaponAttackType.BaseAttack && !HasOffhandWeapon(unit))
        {
            return 0; // "feral or unarmed skill only for base attack"
        }

        return MeleeHitTable.SkillMaxForLevel(unit, victim);
    }

    /// <summary>
    /// Defense skill (vmangos SpellCaster::GetDefenseSkillValue). Default: the Defense skill for a player with
    /// attached skills; level × 5 for everyone else.
    /// </summary>
    public virtual int GetDefenseSkill(Unit unit, Unit? attacker)
        => unit is Player { Skills: { } skills } ? PlayerCombatSkills.DefenseSkill(skills, attacker) : MeleeHitTable.SkillMaxForLevel(unit, attacker);

    /// <summary>vmangos Unit::HaveOffhandWeapon. Default: a player with attached skills has one when a usable weapon is in the off hand; nobody else dual wields.</summary>
    public virtual bool HasOffhandWeapon(Unit unit) => unit is Player { Skills: not null } player && PlayerCombatSkills.HasOffhandWeapon(player);

    /// <summary>Player has a weapon that can parry (vmangos Player::CanParry &amp;&amp; GetWeaponForParry). Default: the Parry ability and a weapon, with attached skills; otherwise no.</summary>
    public virtual bool PlayerCanParry(Player player) => player.Skills is { } skills && PlayerCombatSkills.CanParry(player, skills);

    /// <summary>Player can block with an intact shield (vmangos Player::CanBlock + off-hand item with Block). Default: the Block ability and a shield, with attached skills; otherwise no.</summary>
    public virtual bool PlayerCanBlock(Player player) => player.Skills is { } skills && PlayerCombatSkills.CanBlock(player, skills);

    /// <summary>
    /// vmangos Unit::GetShieldBlockValue: players (strength / 20 − 1, auras later), creatures
    /// level / 2 + strength / 20.
    /// </summary>
    public virtual uint GetShieldBlockValue(Unit unit)
    {
        float strength = unit.GetUInt32(UpdateFields.UnitFieldStat0);
        if (unit is Player)
        {
            float value = (strength / 20) - 1;
            return value < 0 ? 0 : (uint)value;
        }

        return (uint)(unit.Level / 2) + (uint)(strength / 20);
    }

    /// <summary>A unit died. Kill rewards (XP, loot, reputation, honor) go here (vmangos Unit::Kill → Reward*).</summary>
    public virtual void OnKill(Unit? killer, Unit victim)
    {
    }

    /// <summary>
    /// Apply the ghost form at spirit release (vmangos Player::ApplyGhostForm: cast 8326, or
    /// 20584 too for night elves with 20585). The default asks the world's registered
    /// <see cref="Death.IGhostForm"/> (<see cref="Death.DeathSeams"/>) and does nothing without one; combat itself
    /// sets PLAYER_FLAGS_GHOST and water walking either way.
    /// </summary>
    public virtual void ApplyGhostForm(Player player) => player.Map?.Combat.ApplyGhostFormViaSeam(player);

    /// <summary>Remove the ghost form at resurrection (vmangos Player::RemoveGhostForm); the default asks the registered <see cref="Death.IGhostForm"/>.</summary>
    public virtual void RemoveGhostForm(Player player) => player.Map?.Combat.RemoveGhostFormViaSeam(player);

    /// <summary>
    /// Where a released spirit goes (vmangos Player::RepopAtGraveyard → nearest graveyard
    /// from graveyard_zone/WorldSafeLocs). Return false to leave the ghost at its corpse —
    /// the default when no graveyard feature registered an <see cref="Death.IGraveyardRepop"/> for the world
    /// (<see cref="Death.DeathSeams"/>); with one, the default asks it. An implementation that returns
    /// true has already moved the player.
    /// </summary>
    public virtual bool RepopAtGraveyard(Player player) => player.Map?.Combat.RepopViaSeam(player) ?? false;

    /// <summary>Resurrection sickness after a spirit-healer resurrection etc. (vmangos ResurrectPlayer applySickness). Default: none.</summary>
    public virtual void OnResurrected(Player player, bool applySickness)
    {
    }

    /// <summary>
    /// Whether a map is instanceable (vmangos MapEntry::Instanceable). Default until Map.dbc is
    /// loaded: the two continents (0, 1) are not; every other map is.
    /// </summary>
    public virtual bool IsInstanceable(uint mapId) => mapId is not (0 or 1);

    /// <summary>Find a non-player unit by GUID in a map (creatures area). Default: none.</summary>
    public virtual Unit? FindUnit(Maps.Map map, ObjectGuid guid) => null;
}
