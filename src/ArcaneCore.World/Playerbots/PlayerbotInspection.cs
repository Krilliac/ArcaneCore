using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

public sealed record PlayerbotUnitFacts(ulong Guid, string Kind, uint Entry, uint NpcFlags, uint Faction, uint CreatureType);
public sealed record PlayerbotTeacherFacts(uint TeachingSpell, uint LearnedSpell, bool Known, bool FitsClassRace, uint RequiredLevel, uint Cost);
public sealed record PlayerbotNpcFacts(uint Entry, ulong Guid, uint Flags, byte TrainerClass, byte TrainerType,
    bool Hostile, bool InCombat, float Distance, IReadOnlyList<PlayerbotTeacherFacts> Teaching);
public sealed record PlayerbotInspection(string Name, PlayerbotGoalKind Goal, uint ReportedTarget, uint QuestId,
    uint MapId, byte Level, uint Health, uint MaxHealth, uint Money, bool InCombat, bool Ghost,
    PlayerbotUnitFacts? Target, PlayerbotUnitFacts? Victim, uint CurrentCast, uint MeleeCast,
    IReadOnlyList<uint> KnownSpells, IReadOnlyList<PlayerbotNpcFacts> NearbyServices);

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
        return new(player.Name, brain.Goal, brain.TargetEntry, brain.QuestId, player.MapId, player.Level,
            player.Health, player.MaxHealth, player.Money, player.Combat.IsInCombat,
            (player.Flags & PlayerFlags.Ghost) != 0, Unit(brain.InspectionTarget), Unit(player.Combat.Victim),
            state?.CurrentCast?.Spell.Id ?? 0, state?.MeleeCast?.Spell.Id ?? 0,
            spells?.Spellbook.GetSpells(player).Take(128).ToArray() ?? [], nearby);
    }

    private static PlayerbotUnitFacts? Unit(Unit? unit) => unit switch
    {
        Creature creature => new(creature.Guid.Value, "creature", creature.Entry, creature.Template.NpcFlags,
            creature.FactionTemplate, creature.Template.CreatureType),
        null => null,
        _ => new(unit.Guid.Value, "unit", 0, 0, unit.FactionTemplate, 0),
    };
}
