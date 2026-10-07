using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.World.Playerbots;

internal sealed record PlayerbotPlanCandidate(string Id, PlayerbotGoalKind Goal, uint Entry, uint QuestId);
internal sealed record PlayerbotPlannerFacts(byte Level, uint Health, uint MaxHealth, bool InCombat,
    uint MapId, uint KnownSpells, uint FoodCount);
