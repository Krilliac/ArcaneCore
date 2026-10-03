using ArcaneCore.Game.Entities;

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

    /// <summary>
    /// vmangos Unit::IsFriendlyTo. Default until faction templates are loaded: players of the
    /// same team are friendly; everything else is not.
    /// </summary>
    public virtual bool IsFriendly(Unit a, Unit b)
        // Duel opponents are hostile before any team rule (vmangos Object.cpp:3650-3652, "duel - always hostile to opponent").
        => !DuelRules.IsOpponentHostile(a, b) && a is Player pa && b is Player pb && pa.Team == pb.Team;

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
    /// the same map, the victim alive and not flagged non-attackable, not a GM in GM mode, not
    /// friendly, and an enemy player must be PvP-flagged.
    /// </summary>
    public virtual bool CanAttack(Unit attacker, Unit victim)
    {
        if (ReferenceEquals(attacker, victim) || !victim.IsInWorld || !ReferenceEquals(attacker.Map, victim.Map))
        {
            return false;
        }

        const UnitFlags notAttackable = UnitFlags.NonAttackable2 | UnitFlags.Spawning | UnitFlags.TaxiFlight
            | UnitFlags.NotAttackable1 | UnitFlags.NotSelectable;
        if ((victim.UnitFlags & notAttackable) != 0)
        {
            return false;
        }

        if (victim is Player { IsGameMaster: true })
        {
            return false;
        }

        if (victim is ICombatCreature { IsInEvadeMode: true })
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
        if (attacker is Player pa && victim is Player pv && (victim.UnitFlags & UnitFlags.Pvp) == 0 && !DuelRules.IsInDuelWith(pa, pv))
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
    /// 20584 too for night elves with 20585). The default does nothing and no production code
    /// overrides it yet (nothing registers a <see cref="CombatHooks"/> subclass), so the ghost
    /// aura is never cast; combat itself sets PLAYER_FLAGS_GHOST and water walking.
    /// </summary>
    public virtual void ApplyGhostForm(Player player)
    {
    }

    /// <summary>Remove the ghost form at resurrection (vmangos Player::RemoveGhostForm).</summary>
    public virtual void RemoveGhostForm(Player player)
    {
    }

    /// <summary>
    /// Where a released spirit goes (vmangos Player::RepopAtGraveyard → nearest graveyard
    /// from graveyard_zone/WorldSafeLocs). Return false to leave the ghost at its corpse —
    /// the default until graveyard content and teleports exist. An implementation that returns
    /// true has already moved the player.
    /// </summary>
    public virtual bool RepopAtGraveyard(Player player) => false;

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
