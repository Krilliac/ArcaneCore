using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureMapSystem
{
    internal Creature SpawnSummonedWithCounter(CreatureTemplate template, HighGuid highGuid, uint counter, Func<Creature, CreatureHome> prepare, uint guidEntry)
    {
        var creature = new Creature(counter & 0x00FFFFFF, template, spawn: null, _content, _random, highGuid, guidEntry);
        creature.MapId = Map.MapId;
        CreatureHome home = prepare(creature);
        creature.SetHome(home);
        creature.ResetToHome(_serverTime());
        creature.IsNewObject = true;
        GridCoord grid = ComputeGrid(home.X, home.Y);
        if (!_grids.TryGetValue(grid, out LoadedGrid? loaded)) loaded = LoadGrid(grid);
        AddToWorld(creature, loaded);
        return creature;
    }

    /// <summary>
    /// Effect 113's existing current summoned-pet branch (vmangos SpellEffects.cpp:209-257).
    /// The retained corpse stays in its map with the same GUID, owner links, action bar,
    /// cooldowns, powers and death-persistent aura holders. This does not load a cached pet.
    /// </summary>
    internal bool TryReviveCurrentPet(Creature pet, Unit caster, uint health, SpellSystem spells)
    {
        if (pet.Summon is not { Kind: SummonKind.Pet, Charm: not null }
            || pet.DeathState != CreatureDeathState.Corpse || pet.Combat.DeathState != DeathState.Corpse
            || !pet.IsInWorld || !ReferenceEquals(pet.System, this) || !ReferenceEquals(pet.Map, Map)
            || !ReferenceEquals(FindCreature(pet.Guid), pet) || !ReferenceEquals(Map.FindObject(pet.Guid), pet)
            || Map.Pets?.Summons.Contains(pet) != true
            || !caster.IsInWorld || !ReferenceEquals(caster.Map, Map) || !ReferenceEquals(Map.FindObject(caster.Guid), caster)
            || pet.GetOwner() is not { IsInWorld: true } owner || !ReferenceEquals(owner.Map, Map)
            || !ReferenceEquals(Map.FindObject(owner.Guid), owner) || owner.PetGuid != pet.Guid
            || caster is Player { IsQuestSettlementPending: true } || owner is Player { IsQuestSettlementPending: true }
            || spells.IsInTransit(caster) || spells.IsInTransit(owner) || spells.IsInTransit(pet))
        {
            return false;
        }

        // Clear the model's dynamic combat, AI and motion state without replacing update
        // fields or unapplying retained passives. Modifier flags remain owned by their auras.
        Map.Combat.Untrack(pet);
        StopMoving(pet);
        pet.Motion.Reset();
        ResetAiState(pet);
        // Unit::NearTeleportTo publishes the destination to observers on both sides of
        // relocation. A zero-health ALIVE pet cannot rely on a later AI move to correct it.
        MovementInfo moved = pet.Movement;
        moved.X = caster.X;
        moved.Y = caster.Y;
        moved.Z = caster.Z;
        moved.Orientation = caster.Orientation;
        moved.Time = _serverTime();
        byte[] teleport = TeleportPackets.BuildMoveTeleport(pet.Guid, moved);
        Map.BroadcastToObservers(pet, WorldOpcode.MsgMoveTeleport, teleport);
        pet.Relocate(caster.X, caster.Y, caster.Z, caster.Orientation, _serverTime());
        Map.BroadcastToObservers(pet, WorldOpcode.MsgMoveTeleport, teleport);
        pet.SetUInt32(UpdateFields.UnitDynamicFlags, 0);
        pet.UnitFlags &= ~UnitFlags.Skinnable;
        pet.DeathState = CreatureDeathState.Alive;
        pet.Combat.DeathState = DeathState.Alive;
        pet.CorpseDecayMs = 0;
        pet.RespawnAtMs = 0;
        pet.Health = Math.Min(pet.MaxHealth, health);
        pet.Motion.Initialize(pet.Motion.Default, this, start: true);
        pet.AI = new PetAI(pet, () => spells, _random);
        Map.Combat.Track(pet);
        return true;
    }
}
