using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

public sealed record PlayerbotUnitFacts(ulong Guid, string Kind, uint Entry, uint NpcFlags, uint Faction, uint CreatureType);
public sealed record PlayerbotTeacherFacts(uint TeachingSpell, uint LearnedSpell, bool Known, bool FitsClassRace, uint RequiredLevel, uint Cost);
public sealed record PlayerbotNpcFacts(uint Entry, ulong Guid, uint Flags, byte TrainerClass, byte TrainerType,
    bool Hostile, bool InCombat, float Distance, IReadOnlyList<PlayerbotTeacherFacts> Teaching);
public sealed record PlayerbotCorpseFacts(ulong Guid, uint MapId, float X, float Y, float Z, float? Distance,
    long? ReclaimDelayRemainingSeconds);
public sealed record PlayerbotItemFacts(uint Entry, uint Durability, uint MaxDurability);
public sealed record PlayerbotEquipmentFacts(PlayerbotItemFacts? MainHand, uint? MainHandWeaponSkill,
    uint? TotalArmor, PlayerbotItemFacts? Feet);
public sealed record PlayerbotInspection(string Name, PlayerbotGoalKind Goal, uint ReportedTarget, uint QuestId,
    uint MapId, byte Level, uint Health, uint MaxHealth, uint Money, bool InCombat, bool Ghost,
    float PlayerX, float PlayerY, float PlayerZ, DeathState DeathState, PlayerbotCorpseFacts? Corpse,
    PlayerbotUnitFacts? Target, PlayerbotUnitFacts? Victim, uint CurrentCast, uint MeleeCast,
    IReadOnlyList<uint> KnownSpells, IReadOnlyList<PlayerbotNpcFacts> NearbyServices,
    IReadOnlyList<PlayerbotUnitFacts> Attackers, int AttackerCount, PlayerbotEquipmentFacts Equipment)
{
    public MovementFlags MovementFlags { get; init; }
    public StandState StandState { get; init; }
    public uint MovementTimeMs { get; init; }

    /// <summary>Whether the bot is following a route (PlayerbotMotion), and how many loops it gave up so far.</summary>
    public bool Following { get; init; }
    public int LoopsGivenUp { get; init; }
}

internal static class PlayerbotInspector
{
    internal static PlayerbotInspection? Capture(WorldSession session, PlayerbotBrain brain)
    {
        if (!session.World.IsWorldThread) throw new InvalidOperationException("playerbot-inspection-thread");
        if (session.Player is not { } player) return null;
        SpellFeature? spells = session.Services.GetService<SpellFeature>();
        QuestNpcServices? npcs = session.Services.GetService<QuestNpcFeature>()?.Services;
        var nearby = new List<PlayerbotNpcFacts>();
        foreach (ObjectGuid guid in player.VisibleObjects.OrderBy(guid => guid.Value))
        {
            if (npcs?.Deps.Creatures?.Find(player, guid) is not { } npc
                || (npc.NpcFlags & (NpcFlags.Vendor | NpcFlags.Trainer | NpcFlags.QuestGiver)) == 0) continue;
            var teaching = new List<PlayerbotTeacherFacts>();
            if (npcs.Deps.Spells is { } learner)
                foreach (var row in npcs.Npcs.TrainerSpells(npc.Entry).Take(8))
                    if (learner.DescribeTrainerSpell(row.Spell) is { } info)
                        teaching.Add(new(row.Spell, info.LearnedSpell, learner.HasSpell(player, info.LearnedSpell),
                            learner.IsSpellFitByClassAndRace(player, info.LearnedSpell),
                            row.ReqLevel != 0 ? row.ReqLevel : info.SpellLevel, row.SpellCost));
            float dx = player.X - npc.X, dy = player.Y - npc.Y, dz = player.Z - npc.Z;
            nearby.Add(new(npc.Entry, npc.Guid.Value, (uint)npc.NpcFlags, npc.TrainerClass, (byte)npc.TrainerType,
                npc.IsHostile, npc.IsInCombat, MathF.Sqrt(dx * dx + dy * dy + dz * dz), teaching));
            if (nearby.Count == 16) break;
        }
        var state = spells?.System.GetState(player.Guid);
        Unit[] sameMapAttackers = [.. player.Combat.Attackers
            .Where(attacker => ReferenceEquals(attacker.Map, player.Map))
            .OrderBy(attacker => attacker.Guid.Value)];
        PlayerbotUnitFacts[] attackers = sameMapAttackers.Take(4).Select(attacker => Unit(attacker))
            .Where(facts => facts is not null).Select(facts => facts!).ToArray();
        PlayerbotEquipmentFacts equipment = Equipment(player);
        PlayerbotCorpseFacts? corpse = player.Combat.Corpse is { } body
            ? new(body.Guid.Value, body.MapId, body.X, body.Y, body.Z,
                CorpseDistance(player, body), CorpseDelayRemaining(session, player, body))
            : null;
        return new(player.Name, brain.Goal, brain.TargetEntry, brain.QuestId, player.MapId, player.Level,
            player.Health, player.MaxHealth, player.Money, player.Combat.IsInCombat,
            (player.Flags & PlayerFlags.Ghost) != 0, player.X, player.Y, player.Z, player.Combat.DeathState, corpse,
            Unit(brain.InspectionTarget), Unit(player.Combat.Victim),
            state?.CurrentCast?.Spell.Id ?? 0, state?.MeleeCast?.Spell.Id ?? 0,
            spells?.Spellbook.GetSpells(player).Take(128).ToArray() ?? [], nearby,
            attackers, sameMapAttackers.Length, equipment)
        {
            MovementFlags = player.Movement.Flags,
            StandState = player.StandState,
            MovementTimeMs = player.Movement.Time,
            Following = PlayerbotMotion.IsActive(player),
            LoopsGivenUp = PlayerbotMotion.LoopCount(player),
        };
    }

    private static PlayerbotEquipmentFacts Equipment(Player player)
    {
        Item? mainHand = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand);
        Item? feet = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet);
        uint? skill = player.Skills is { } skills
            ? (uint)PlayerCombatSkills.WeaponSkill(player, skills, WeaponAttackType.BaseAttack) : null;
        uint? armor = player.IsInWorld ? player.GetUInt32(UpdateFields.UnitFieldResistances) : null;
        return new(Item(mainHand), skill, armor, Item(feet));
    }

    private static PlayerbotItemFacts? Item(Item? item)
        => item is null ? null : new(item.Entry, item.Durability, item.MaxDurability);

    private static PlayerbotUnitFacts? Unit(Unit? unit) => unit switch
    {
        Creature creature => new(creature.Guid.Value, "creature", creature.Entry, creature.Template.NpcFlags,
            creature.FactionTemplate, creature.Template.CreatureType),
        null => null,
        _ => new(unit.Guid.Value, "unit", 0, 0, unit.FactionTemplate, 0),
    };

    private static float? CorpseDistance(Player player, Corpse body)
    {
        if (!ReferenceEquals(player.Map, body.Map)) return null;
        float distance = MathF.Sqrt(MathF.Pow(player.X - body.X, 2) + MathF.Pow(player.Y - body.Y, 2)
            + MathF.Pow(player.Z - body.Z, 2));
        return float.IsFinite(distance) ? distance : null;
    }

    private static long? CorpseDelayRemaining(WorldSession session, Player player, Corpse body)
    {
        if ((player.Flags & PlayerFlags.Ghost) == 0 || player.Map is not { } map
            || PlayerLife.Capture(player).Corpse is not { } snapshot) return null;
        long now = DeathHooks.For(session.World).Clock.UnixSeconds;
        uint delay = map.Combat.GetCorpseReclaimDelay(player, body.Type == CorpseType.ResurrectablePvp);
        return Math.Max(0, snapshot.GhostTimeUnix + delay - now);
    }
}
