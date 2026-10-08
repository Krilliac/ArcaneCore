using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// The type behaviours of vmangos GameObject::Use and GameObject::Update that cast spells or act on players nearby: goober spells and
/// gossip, spell-caster objects, linked traps, environmental traps, area damage and flag stands (GameObject.cpp:313-690, 1258-1347,
/// 1398-2030; behaviour re-implemented, no code copied).
/// </summary>
public sealed partial class GameObjectMapSystem
{
    /// <summary>goober.spellId (data10), goober.linkedTrapId (data12) and goober.gossipID (data19), GameObjectDefines.h:333-355.</summary>
    public const int GooberSpellData = 10;

    public const int GooberGossipData = 19;

    /// <summary>spellcaster.spellId (data0) and spellcaster.partyOnly (data2), GameObjectDefines.h:427-436.</summary>
    public const int SpellCasterSpellData = 0;

    public const int SpellCasterPartyOnlyData = 2;

    /// <summary>areadamage.radius, damageMin, damageMax and damageSchool (data1..data4), GameObjectDefines.h:364-375.</summary>
    public const int AreaDamageRadiusData = 1;

    public const int AreaDamageMinData = 2;

    public const int AreaDamageMaxData = 3;

    public const int AreaDamageSchoolData = 4;

    /// <summary>GameObject::TriggerLinkedGameObject / RespawnLinkedGameObject (GameObject.cpp:1293-1296, 1330): the search range without a trap spell.</summary>
    public const float LinkedTrapSearchRange = 0.5f;

    /// <summary>The spells objects cast (the world's spell system); null skips every object spell.</summary>
    public IGameObjectSpells? Spells { get; set; }

    /// <summary>Gossip menus of goobers; null shows none.</summary>
    public IGameObjectGossip? Gossip { get; set; }

    /// <summary>The battleground side of flag stands; null leaves flag stands unusable (as outside a battleground).</summary>
    public IGameObjectFlagStands? FlagStands { get; set; }

    /// <summary>vmangos Player::IsInSameRaidWith (party-only spell casters, grouped rituals); null: only a player is in a raid with himself.</summary>
    public Func<Player, Player, bool>? SameRaid { get; set; }

    /// <summary>vmangos Group::GetId of the player's group, 0 for none (party-only spell casters without an owner); null: nobody is grouped.</summary>
    public Func<Player, uint>? GroupIdOf { get; set; }

    private bool InSameRaid(Player a, Player b) => ReferenceEquals(a, b) || SameRaid?.Invoke(a, b) == true;

    /// <summary>The unit that owns <paramref name="go"/> (vmangos GameObject::GetOwner), when it is in this map.</summary>
    private Unit? OwnerOf(GameObject go) => go.OwnerGuid.IsEmpty ? null : Map.FindObject(go.OwnerGuid) as Unit;

    // --- goober -----------------------------------------------------------------------------

    /// <summary>
    /// GameObject::Use, goober (GameObject.cpp:1547-1562): the page text when the goober has one, otherwise its gossip menu. Both are shown before
    /// the quest gate, so a goober tied to a quest still reads or talks to a player without it.
    /// </summary>
    private void ShowGooberPageOrGossip(Player player, GameObject go)
    {
        if (go.Template.GetData(7) != 0)
        {
            player.Session.Send(WorldOpcode.SmsgGameobjectPagetext, GameObjectPackets.PageText(go.Guid));
        }
        else if (go.Template.GetData(GooberGossipData) is var menu and not 0)
        {
            Gossip?.OpenGossip(player, go, menu);
        }
    }

    /// <summary>goober.questId is signed (GameObjectDefines.h:337): only a positive value gates the use; -1 marks an object every viewer may use.</summary>
    private static int GooberQuestId(GameObject go) => unchecked((int)go.Template.GetData(1));

    /// <summary>GameObject::Use (GameObject.cpp:1984-1990, 2025-2028): a goober's spell (data10) is cast by the object at its user.</summary>
    private void CastGooberSpell(Player player, GameObject go)
    {
        if (go.Template.GetData(GooberSpellData) is var spellId and not 0)
        {
            Spells?.Cast(go, spellId, player, null);
        }
    }

    // --- spell caster -----------------------------------------------------------------------

    /// <summary>
    /// GAMEOBJECT_TYPE_SPELLCASTER (GameObject.cpp:1798-1834): the object's flags become GO_FLAG_LOCKED; a party-only object (data2) is usable only
    /// by the raid of its owner, or, without an owner, by the group of the player who created it; then the use is counted (charges, data1, run
    /// out in <see cref="UpdateTypeBehaviour"/>) and the object casts its spell (data0) at the user.
    /// </summary>
    private GameObjectUseResult UseSpellCaster(Player player, GameObject go)
    {
        go.Flags = GameObjectFlags.Locked;
        if (go.Template.GetData(SpellCasterPartyOnlyData) != 0)
        {
            if (OwnerOf(go) is not { } owner)
            {
                if (go.OwnerGroupId == 0 || GroupIdOf?.Invoke(player) is not { } groupId || groupId != go.OwnerGroupId)
                {
                    return GameObjectUseResult.NotUsable;
                }
            }
            else if (owner is not Player ownerPlayer || !InSameRaid(player, ownerPlayer))
            {
                return GameObjectUseResult.NotUsable;
            }
        }

        go.UseCount++;
        if (go.Template.GetData(SpellCasterSpellData) is var spellId and not 0)
        {
            Spells?.Cast(go, spellId, player, null);
        }

        return GameObjectUseResult.Ok;
    }

    // --- flag stand -------------------------------------------------------------------------

    /// <summary>
    /// GAMEOBJECT_TYPE_FLAGSTAND (GameObject.cpp:1843-1870): a player who may use battleground objects loses stealth and invisibility and the
    /// click goes to the battleground; anywhere else nothing happens.
    /// </summary>
    private GameObjectUseResult UseFlagStand(Player player, GameObject go)
    {
        if (FlagStands is not { } stands || !stands.CanUseBattlegroundObject(player))
        {
            return GameObjectUseResult.NotUsable;
        }

        stands.BreakStealthAndInvisibility(player);
        return stands.OnFlagClicked(player, go) ? GameObjectUseResult.Ok : GameObjectUseResult.NotUsable;
    }

    // --- area damage ------------------------------------------------------------------------

    /// <summary>
    /// GAMEOBJECT_TYPE_AREADAMAGE through CMSG_GAMEOBJ_USE: its interaction distance is 0 (GameObjectDefines.h:780), so only a user standing
    /// exactly on it gets here; the lock (data0) is checked like a door's, then <see cref="ActivateAreaDamage"/>.
    /// </summary>
    private GameObjectUseResult UseAreaDamage(Player player, GameObject go)
    {
        GameObjectUseResult locked = CheckDirectLock(player, go);
        return locked != GameObjectUseResult.Ok ? locked : ActivateAreaDamage(go);
    }

    /// <summary>
    /// Activate an area damage object (scripts, GM commands, spells; players cannot reach one, see <see cref="UseAreaDamage"/>). The reference
    /// core has no behaviour for this type (GameObject::Use logs it as unhandled, GameObject.cpp:1981-1983) and no 1.12 database row uses it, so
    /// this follows the template columns only (GameObjectDefines.h:364-375): the object activates like a door and closes after its auto-close time
    /// (data5), and every living player within its radius (data1, 3D from the centre) takes one roll of damageMin..damageMax (data2, data3) as
    /// environmental damage, logged as slime for nature (school 3) and as fire otherwise. Absorb and resist are not applied. An object already
    /// active answers <see cref="GameObjectUseResult.InUse"/>.
    /// </summary>
    public GameObjectUseResult ActivateAreaDamage(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (go.Type != GameObjectType.AreaDamage || !go.IsSpawned || !Tracks(go))
        {
            return GameObjectUseResult.NotUsable;
        }

        GameObjectUseResult activated = ActivateDoorOrButton(go, go.Template.AutoCloseSeconds());
        if (activated != GameObjectUseResult.Ok)
        {
            return activated;
        }

        float radius = go.Template.GetData(AreaDamageRadiusData);
        uint min = go.Template.GetData(AreaDamageMinData);
        uint max = Math.Max(min, go.Template.GetData(AreaDamageMaxData));
        EnvironmentalDamageType type = go.Template.GetData(AreaDamageSchoolData) == 3 ? EnvironmentalDamageType.Slime : EnvironmentalDamageType.Fire;
        foreach (Player victim in Map.Players.ToArray())
        {
            if (!victim.IsAlive || victim.IsGameMaster || CentreDistanceSquared(go, victim) > radius * radius)
            {
                continue;
            }

            uint damage = max > min ? (uint)Random.NextInt64(min, (long)max + 1) : min;
            CombatPackets.SendToSet(victim, WorldOpcode.SmsgEnvironmentaldamagelog, EnvironmentalDamage.BuildLog(victim.Guid.Value, type, damage, 0, 0));
            Map.Combat.DealDamage(victim, victim, damage, direct: false, meleeDamage: false, startsCombat: false);
        }

        return GameObjectUseResult.Ok;
    }

    private static float CentreDistanceSquared(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    /// <summary>vmangos WorldObject::IsWithinDistInMap with bounding radii: strictly below the range plus both radii, 3D.</summary>
    private static bool WithinDistance(WorldObject a, WorldObject b, float range)
    {
        float reach = range + a.BoundingRadius + b.BoundingRadius;
        return CentreDistanceSquared(a, b) < reach * reach;
    }

    // --- linked traps -----------------------------------------------------------------------

    /// <summary>
    /// GameObject::TriggerLinkedGameObject (GameObject.cpp:1284-1319): the nearest trap of the object's linked trap entry (button data3, chest
    /// data7, spell focus data2, goober data12) within the trap spell's maximum range (0.5 yards without a spell) is used on <paramref name="user"/>
    /// when it is spawned. The nearest one decides: the grid search does not skip despawned traps, so a spawned trap further away never stands in
    /// for a despawned one nearby.
    /// </summary>
    private void TriggerLinkedTrap(GameObject go, Unit user)
    {
        if (go.Template.LinkedTrapEntry() is var entry and not 0 && LinkedTrapTemplate(entry) is { } trapTemplate
            && FindLinkedTrap(go, entry, Spells?.MaxRange(trapTemplate.GetData(TrapRules.SpellData)) ?? LinkedTrapSearchRange) is { IsSpawned: true } trap)
        {
            UseTrap(trap, user);
        }
    }

    /// <summary>
    /// GameObject::RespawnLinkedGameObject (GameObject.cpp:1321-1347): when a door, button, chest, spell focus or goober respawns, the nearest
    /// trap of its linked entry within 0.5 yards respawns with it when it is despawned.
    /// </summary>
    private void RespawnLinkedTrap(GameObject go)
    {
        if (go.Type is GameObjectType.Door or GameObjectType.Button or GameObjectType.Chest or GameObjectType.SpellFocus or GameObjectType.Goober
            && go.Template.LinkedTrapEntry() is var entry and not 0 && LinkedTrapTemplate(entry) is not null
            && FindLinkedTrap(go, entry, LinkedTrapSearchRange) is { IsSpawned: false } trap)
        {
            Respawn(trap);
        }
    }

    /// <summary>The template of a linked trap entry when it is a trap (TriggerLinkedGameObject and RespawnLinkedGameObject ignore anything else).</summary>
    private GameObjectTemplate? LinkedTrapTemplate(uint trapEntry)
        => _content.FindTemplate(trapEntry) is { } template && (GameObjectType)template.Type == GameObjectType.Trap ? template : null;

    /// <summary>
    /// MaNGOS::NearestGameObjectEntryInObjectRangeCheck (GridNotifiers.h:639-661): the nearest object of <paramref name="trapEntry"/> within
    /// <paramref name="range"/> (both bounding radii), spawned or not.
    /// </summary>
    private GameObject? FindLinkedTrap(GameObject go, uint trapEntry, float range)
    {
        GameObject? best = null;
        float bestDistance = float.MaxValue;
        foreach (GameObject candidate in _objects.Values)
        {
            if (candidate.Entry != trapEntry || ReferenceEquals(candidate, go))
            {
                continue;
            }

            float distance = CentreDistanceSquared(go, candidate);
            if (WithinDistance(go, candidate, range) && distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// GameObject::SummonLinkedTrapIfAny (GameObject.cpp:1258-1282), for an object a spell created: its linked trap is created at the same place,
    /// lives as long as it, remembers the creating spell and shares its owner and level. Database objects have their traps as spawns of their own.
    /// </summary>
    public GameObject? SummonLinkedTrapIfAny(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        uint trapEntry = go.Template.LinkedTrapEntry();
        if (trapEntry == 0 || !Tracks(go))
        {
            return null;
        }

        uint lifetime = _despawnAt.TryGetValue(go.Guid, out long at) ? (uint)Math.Max(1L, (at - _clockMs + 999) / 1000) : 0;
        if (Summon(trapEntry, go.X, go.Y, go.Z, go.Orientation, lifetime) is not { } trap)
        {
            return null;
        }

        trap.SpellId = go.SpellId;
        if (!go.OwnerGuid.IsEmpty)
        {
            trap.SetOwner(go.OwnerGuid);
            trap.SetUInt32(UpdateFields.GameobjectLevel, go.GetUInt32(UpdateFields.GameobjectLevel));
        }

        return trap;
    }

    // --- traps ------------------------------------------------------------------------------

    /// <summary>
    /// GameObject::Use of a trap (GameObject.cpp:1421-1428, 1487-1513), reached through a linked object: the trap's cooldown (data5) gates it, then its spell
    /// (data3) is cast at the user by its owner or by the trap itself, a custom animation plays, and a trap with charges (data4) counts the use and is
    /// used up at the last charge.
    /// </summary>
    private void UseTrap(GameObject trap, Unit user)
    {
        // The gate is GameObject::Use's own, ahead of its type switch (GameObject.cpp:1421-1428): GetCooldown is trap.cooldown for a trap
        // (GameObjectDefines.h:632-640), and m_cooldownTime is the same timer the environmental scan reads (GameObject.cpp:467, 540).
        if (trap.Template.CooldownSeconds() is var cooldown and not 0)
        {
            if (trap.CooldownUntilMs > _clockMs)
            {
                return;
            }

            trap.CooldownUntilMs = _clockMs + (cooldown * 1000L);
        }

        if (trap.Template.GetData(TrapRules.SpellData) is var spellId and not 0)
        {
            Spells?.Cast(trap, spellId, user, OwnerOf(trap));
        }

        PlayCustomAnim(trap);
        if (trap.Template.Charges() is var charges and not 0)
        {
            trap.UseCount++;
            if (trap.UseCount >= charges)
            {
                trap.UseCount = 0;
                trap.LootState = GameObjectLootState.JustDeactivated;
            }
        }
    }

    private void PlayCustomAnim(GameObject go)
    {
        if (GameObjectInfoView.HasCustomAnim(go.GetUInt32(UpdateFields.GameobjectDisplayid)))
        {
            Map.BroadcastToObservers(go, WorldOpcode.SmsgGameobjectCustomAnim, GameObjectPackets.CustomAnim(go.Guid, 0));
        }
    }

    /// <summary>
    /// The per-update part of the type behaviours (GameObject::Update, GO_NOT_READY and GO_READY, GameObject.cpp:340-570): environmental traps
    /// arm and scan, and a spell caster or environmental trap whose charges ran out is used up. Hunter traps (an owner and charges) belong to
    /// the trap system of the ranged area.
    /// </summary>
    private void UpdateTypeBehaviour(GameObject go)
    {
        if (!go.IsSpawned || !Tracks(go))
        {
            return;
        }

        bool environmentalTrap = go.Type == GameObjectType.Trap && (go.OwnerGuid.IsEmpty || go.Template.Charges() == 0);
        if (environmentalTrap)
        {
            UpdateEnvironmentalTrap(go);
        }

        if ((environmentalTrap || go.Type == GameObjectType.SpellCaster) && go.LootState == GameObjectLootState.Ready
            && go.Template.Charges() is var charges and not 0 && go.UseCount >= charges)
        {
            go.UseCount = 0;
            go.LootState = GameObjectLootState.JustDeactivated;
        }
    }

    /// <summary>
    /// The environmental branch of the trap update (GameObject.cpp:346-357, 455-560): the first update arms the trap (its start delay, data7, becomes
    /// its cooldown); then, once the cooldown ran out, the nearest living player within the radius (data2, both bounding radii, 3D) sets it off: the
    /// trap's spell (data3) is cast at that player by the trap's owner or the trap itself, the cooldown (data5, 4 seconds when 0) starts, a trap with
    /// charges counts the use and a custom animation plays. A trap without a radius only acts through a linked object; the battleground buff objects
    /// (no radius, cooldown 3) belong to the battleground area and are not scanned. Limit: vmangos takes the first player its grid search finds, this
    /// takes the nearest one (lowest guid on a tie).
    /// </summary>
    private void UpdateEnvironmentalTrap(GameObject trap)
    {
        if (!trap.TrapArmed)
        {
            trap.TrapArmed = true;
            if (trap.Template.GetData(TrapRules.StartDelayData) is var delay and not 0)
            {
                trap.CooldownUntilMs = _clockMs + (delay * 1000L);
            }
        }

        if (trap.LootState != GameObjectLootState.Ready || trap.CooldownUntilMs >= _clockMs)
        {
            return;
        }

        float radius = TrapRules.TriggerRadius(trap.Template, TrapRadiusSource.Vmangos);
        if (radius <= 0)
        {
            return;
        }

        Player? target = null;
        float best = float.MaxValue;
        foreach (Player player in Map.Players)
        {
            if (!player.IsAlive || !player.IsInWorld || !WithinDistance(trap, player, radius))
            {
                continue;
            }

            float distance = CentreDistanceSquared(trap, player);
            if (distance < best || (distance == best && target is not null && player.Guid.Value < target.Guid.Value))
            {
                target = player;
                best = distance;
            }
        }

        if (target is null)
        {
            return;
        }

        if (trap.Template.GetData(TrapRules.SpellData) is var spellId and not 0)
        {
            Spells?.Cast(trap, spellId, target, OwnerOf(trap));
        }

        uint cooldown = trap.Template.GetData(TrapRules.CooldownData);
        trap.CooldownUntilMs = _clockMs + ((cooldown != 0 ? cooldown : TrapRules.DefaultCooldownSeconds) * 1000L);
        if (trap.Template.Charges() != 0)
        {
            trap.UseCount++;
        }

        PlayCustomAnim(trap);
    }
}
