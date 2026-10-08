using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// What the EventAI actions and events of cmangos-classic need from the map beyond the core lifecycle (CreatureEventAI.cpp ProcessAction,
/// :665-1380, and the AI hooks JustSummoned, SummonedCreatureJustDied/Despawn, ReceiveAIEvent, SpellHitTarget): who summoned a creature,
/// AI events thrown around a creature, forced and delayed despawns, guardians, sounds and zone-wide combat.
/// </summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>cmangos <c>AI_EVENT_CUSTOM_EVENTAI_A..F</c> (AIDefines.h:30-37): 5, 6, 8, 9, 10, 11; they reach every living creature in range.</summary>
    private static bool IsCustomAiEvent(uint eventType) => eventType is 5 or 6 or (>= 8 and <= 11) or > 100;

    /// <summary>The creature that summoned a creature of this map (cmangos GetSpawnerGuid), by the summoned creature's GUID.</summary>
    private readonly Dictionary<ObjectGuid, ObjectGuid> _summoners = [];

    /// <summary>Forced despawns waiting for their delay (cmangos ForcedDespawn(msDelay)).</summary>
    private readonly List<(Creature Creature, long AtMs)> _forcedDespawns = [];

    /// <summary>The creature that summoned <paramref name="summoned"/> when it is still on this map, else null.</summary>
    public Creature? SummonerOf(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        return _summoners.TryGetValue(summoned.Guid, out ObjectGuid summoner) ? FindCreature(summoner) : null;
    }

    /// <summary>
    /// Record the creature that summons <paramref name="summoned"/> before it enters the world, so its AI already knows its spawner when it
    /// starts (cmangos TemporarySpawn takes the summoner's GUID at construction). A player summoner is not linked.
    /// </summary>
    private void RecordSummoner(Creature summoner, Creature summoned)
    {
        if (!ReferenceEquals(summoner, summoned) && _creatures.ContainsKey(summoner.Guid))
        {
            _summoners[summoned.Guid] = summoner.Guid;
        }
    }

    /// <summary>
    /// The summon is in the world: the summoner's AI hears of it (cmangos JustSummoned, after TemporarySpawn::Summon initialised the summon's
    /// AI; for an EventAI summon and for a spell summon of a creature caster).
    /// </summary>
    private void NotifyJustSummoned(Creature summoned)
    {
        if (SummonerOf(summoned) is { AI: { } ai })
        {
            ai.OnJustSummoned(summoned);
        }
    }

    /// <summary>A summoned creature died: its summoner's AI hears of it (cmangos SummonedCreatureJustDied).</summary>
    private void NotifySummonerOfDeath(Creature summoned)
    {
        if (SummonerOf(summoned) is { IsAlive: true, AI: { } ai })
        {
            ai.OnSummonedCreatureJustDied(summoned);
        }
    }

    /// <summary>A summoned creature left the world: its summoner's AI hears of it (cmangos SummonedCreatureDespawn) and the link goes.</summary>
    private void NotifySummonerOfRemoval(Creature summoned)
    {
        if (!_summoners.Remove(summoned.Guid, out ObjectGuid summonerGuid))
        {
            return;
        }

        if (FindCreature(summonerGuid) is { IsAlive: true, AI: { } ai })
        {
            ai.OnSummonedCreatureDespawn(summoned);
        }
    }

    /// <summary>
    /// cmangos UnitAI::SendAIEventAround (BaseAI/UnitAI.cpp:615-654) with no delay: a custom EventAI event (A-F) reaches every living
    /// creature within <paramref name="radius"/> of <paramref name="sender"/>, the sender included (AnyUnitInObjectRangeCheck); the other
    /// types reach the creatures that could assist the sender against <paramref name="invoker"/> (AnyAssistCreatureInRangeCheck: not the
    /// sender, in range and line of sight, able to assist; without an invoker nobody). Returns how many creatures received it.
    /// </summary>
    public int SendAiEventAround(Creature sender, uint eventType, Unit? invoker, float radius, uint miscValue = 0)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (radius <= 0)
        {
            return 0;
        }

        bool custom = IsCustomAiEvent(eventType);
        List<Creature> receivers = [];
        foreach (Creature candidate in _creatures.Values)
        {
            if (!candidate.IsAlive || candidate.AI is null)
            {
                continue;
            }

            if (custom)
            {
                if (DistanceSquared(sender, candidate) <= radius * radius)
                {
                    receivers.Add(candidate);
                }
            }
            else if (invoker is not null && CanAssist(candidate, sender, invoker, radius))
            {
                receivers.Add(candidate);
            }
        }

        foreach (Creature receiver in receivers)
        {
            receiver.AI?.OnReceiveAiEvent(eventType, sender, invoker, miscValue);
        }

        return receivers.Count;
    }

    /// <summary>
    /// cmangos Creature::ForcedDespawn(msDelay) (EventAI FORCE_DESPAWN): after the delay a living creature dies without loot or a kill and its
    /// corpse goes at once; a database spawn then respawns after its respawn delay, a temporary creature is gone.
    /// </summary>
    public void ForcedDespawn(Creature creature, uint delayMs)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (delayMs == 0)
        {
            ForcedDespawnNow(creature);
            return;
        }

        _forcedDespawns.RemoveAll(d => ReferenceEquals(d.Creature, creature));
        _forcedDespawns.Add((creature, _clockMs + delayMs));
    }

    private void UpdateForcedDespawns()
    {
        for (int i = _forcedDespawns.Count - 1; i >= 0; i--)
        {
            if (_forcedDespawns[i].AtMs <= _clockMs)
            {
                Creature creature = _forcedDespawns[i].Creature;
                _forcedDespawns.RemoveAt(i);
                ForcedDespawnNow(creature);
            }
        }
    }

    private void ForcedDespawnNow(Creature creature)
    {
        if (!_creatures.ContainsKey(creature.Guid))
        {
            return;
        }

        if (creature.Spawn is null)
        {
            Map.Combat.CombatStop(creature);
            Despawn(creature);
            return;
        }

        if (creature.DeathState == CreatureDeathState.Alive)
        {
            Map.Combat.CombatStop(creature);
            StopMoving(creature);
            creature.Health = 0;
            creature.Target = default;
            creature.DeathState = CreatureDeathState.Corpse;
            creature.RespawnAtMs = _clockMs + (creature.NextRespawnDelaySeconds() * 1000L);
            SaveRespawnOnDeath(creature);
        }

        if (creature.DeathState == CreatureDeathState.Corpse)
        {
            RemoveCorpse(creature);
        }
    }

    /// <summary>
    /// cmangos EventAI DESPAWN_GUARDIANS (CreatureEventAI.cpp:1285-1299): the creature's guardians of <paramref name="entry"/> (0: all of
    /// them) are unsummoned (Pet::Unsummon then RemoveGuardian). Returns how many went.
    /// </summary>
    public int DespawnGuardians(Creature owner, uint entry)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (Map.Pets is not { } pets)
        {
            return 0;
        }

        Creature[] guardians = [.. pets.GuardiansOf(owner, entry)];
        foreach (Creature guardian in guardians)
        {
            Map.Combat.CombatStop(guardian);
            pets.Forget(guardian);
            Despawn(guardian);
        }

        return guardians.Length;
    }

    /// <summary>
    /// cmangos WorldObject::PlayDirectSound (EventAI SOUND): SMSG_PLAY_SOUND with the sound id to every player that sees the creature
    /// (wow_messages smsg_play_sound.wowm: u32 sound id).
    /// </summary>
    public void PlayDirectSound(Creature creature, uint soundId)
    {
        ArgumentNullException.ThrowIfNull(creature);
        var w = new PacketWriter(4);
        w.WriteUInt32(soundId);
        Map.BroadcastToObservers(creature, WorldOpcode.SmsgPlaySound, w.ToArray());
    }

    /// <summary>
    /// cmangos Creature::SetInCombatWithZone (Entities/Creature.cpp:2360-2403, EventAI ZONE_COMBAT_PULSE): only on a dungeon or raid map
    /// (Map::IsDungeon, the map template's type: a battleground is not one); every living player on the map who is not a game master and
    /// whom the creature can attack is put on the creature's threat list and in combat with it. Returns how many.
    /// </summary>
    public int SetInCombatWithZone(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (Map.Template is not { IsDungeon: true } || !creature.IsAlive)
        {
            return 0;
        }

        Player[] players = [.. Map.Players.Where(p => p.IsAlive && !p.IsGameMaster && Map.Combat.Hooks.CanAttack(creature, p))];
        if (creature.Combat.Victim is null && players.Length > 0)
        {
            // Engaged first: the unit AttackClosestEnemy would pick from the threat list once every player is on it.
            EnterCombatWithTarget(creature, ClosestEnemy(creature, players.Concat(creature.Combat.Threat.Entries.Select(e => e.Target))));
        }

        foreach (Player player in players)
        {
            EnterCombatWithTarget(creature, player);
        }

        return players.Length;
    }

    /// <summary>
    /// cmangos UnitAI::AttackClosestEnemy (BaseAI/UnitAI.cpp:863-888): the creature attacks the unit of its threat list that is closest to it.
    /// False when it has a victim already or nothing on its threat list.
    /// </summary>
    public bool AttackClosestEnemy(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.Combat.Victim is not null || creature.Combat.Threat.Entries.Count == 0)
        {
            return false;
        }

        Unit closest = ClosestEnemy(creature, creature.Combat.Threat.Entries.Select(e => e.Target));
        if (creature.AI is { } ai)
        {
            ai.AttackStart(closest);
        }
        else
        {
            AttackStart(creature, closest);
        }

        return creature.Combat.Victim is not null;
    }

    private static Unit ClosestEnemy(Creature creature, IEnumerable<Unit> candidates)
        => candidates.MinBy(u => ((u.X - creature.X) * (u.X - creature.X)) + ((u.Y - creature.Y) * (u.Y - creature.Y)) + ((u.Z - creature.Z) * (u.Z - creature.Z)))!;

    /// <summary>
    /// cmangos UnitAI::SetAIImmobilizedState (BaseAI/UnitAI.cpp:890-901; EventAI SET_IMMOBILIZED_STATE): the creature is rooted (the
    /// server-owned root flag its movement code reads, kept while a root aura comes and goes) and stops; <paramref name="combatOnly"/> lets the
    /// next reset (reaching home, respawn) lift it.
    /// </summary>
    public void SetAiImmobilized(Creature creature, bool apply, bool combatOnly)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (combatOnly || !apply)
        {
            creature.AiImmobilizedCombatOnly = apply && combatOnly;
        }

        creature.AiImmobilized = apply;
        if (apply)
        {
            creature.AddMovementFlags(MovementFlags.Root);
            StopMoving(creature);
        }
        else if (_ai.UnitSpells is not { } spells || !spells.IsRooted(creature))
        {
            creature.RemoveMovementFlags(MovementFlags.Root);
        }
    }

    /// <summary>cmangos UnitAI::ClearCombatOnlyRoot (Reset): a combat-only EventAI root ends.</summary>
    internal void ClearCombatOnlyRoot(Creature creature)
    {
        if (creature.AiImmobilizedCombatOnly)
        {
            SetAiImmobilized(creature, false, combatOnly: false);
        }
    }

    /// <summary>
    /// cmangos Creature::UpdateEntry(entry) with its health kept (EventAI UPDATE_TEMPLATE, CreatureEventAI.cpp:1078-1086): the creature takes
    /// the other template (entry, model, faction, flags, level and stats) and keeps its health percent, its combat and its AI; its next respawn
    /// returns to the template it had. False for the creature's own entry or a missing template.
    /// </summary>
    public bool UpdateEntry(Creature creature, uint entry)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.Template.Entry == entry || _content.FindTemplate(entry) is not { } template || !creature.IsAlive)
        {
            return false;
        }

        creature.ScriptOriginalTemplate ??= creature.Template;
        uint healthPercent = creature.MaxHealth == 0 ? 100 : (uint)((ulong)creature.Health * 100 / creature.MaxHealth);
        bool inCombat = (creature.UnitFlags & UnitFlags.InCombat) != 0;
        creature.ChangeTemplate(template);
        creature.InitializeFields();
        creature.Health = Math.Max(1u, (uint)((ulong)creature.MaxHealth * healthPercent / 100));
        if (inCombat)
        {
            creature.UnitFlags |= UnitFlags.InCombat;
        }

        return true;
    }

    /// <summary>
    /// cmangos Unit::SetWalk(!run, asDefault) for a scripted creature (EventAI SET_WALK, the relay SET_RUN): its scripted moves run or walk, and
    /// the change is announced at once.
    /// </summary>
    public void SetScriptRun(Creature creature, bool run)
    {
        ArgumentNullException.ThrowIfNull(creature);
        creature.ScriptRun = run;
        SyncWalkMode(creature, run);
    }

    /// <summary>vmangos IsWithinLOSInMap between two objects of this map (open without vmaps).</summary>
    internal bool IsInLineOfSight(WorldObject from, WorldObject to) => InLineOfSight(from, to);

    /// <summary>A spell a creature of this map cast landed: its AI hears of it (cmangos SpellHitTarget).</summary>
    private void NotifySpellHitTarget(Unit caster, Unit target, SpellInfo spell)
    {
        if (caster is Creature creature && ReferenceEquals(creature.System, this) && creature.IsAlive)
        {
            creature.AI?.OnSpellHitTarget(target, spell);
        }
    }
}
