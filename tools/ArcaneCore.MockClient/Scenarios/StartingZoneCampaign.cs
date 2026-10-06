using ArcaneCore.Protocol;
using ArcaneCore.Game;
using ArcaneCore.MockClient.Protocol;
using System.Text.Json;
using System.Diagnostics;

namespace ArcaneCore.MockClient.Scenarios;

/// <summary>Opt-in, finite quest-7 campaign over ten distinct source-backed entry-6 spawns.</summary>
internal static class StartingZoneCampaign
{
    private const uint RequiredKills = 10;

    private static readonly (uint Spawn, float X, float Y, float Z)[] Candidates =
    [
        (80002, -8785.45996f, -171.22900f, 81.67640f),
        (79992, -8783.04980f, -161.56500f, 82.03520f),
        (79993, -8774.12988f, -184.49400f, 83.17640f),
        (80000, -8779.79980f, -195.35500f, 84.05140f),
        (79998, -8768.45996f, -176.43401f, 83.44460f),
        (79996, -8786.58984f, -143.45799f, 82.41020f),
        (80032, -8768.16992f, -190.50200f, 84.55140f),
        (80001, -8775.90039f, -148.46600f, 81.41020f),
        (80031, -8757.54980f, -180.68401f, 85.01160f),
        (79995, -8794.95996f, -134.14999f, 83.03520f),
    ];

    internal sealed record CampaignEvidence(
        IReadOnlyList<StartingZoneCombat.CombatEvidence> Kills,
        IReadOnlyList<StartingZoneFood.FoodEvidence> Food,
        bool JournalComplete, MockQuestComplete Reward, float TurnInX, float TurnInY, float TurnInZ,
        uint TurnInClientTime, int Frames, int Bytes);

    internal static bool IsRewardIdentity(MockQuestComplete reward, uint quest)
        => reward.QuestId == quest && reward.Type == 3;

    internal static async Task<CampaignEvidence> RunAsync(ScenarioConnection connection, ulong character, uint quest,
        uint firstSpawn, (float X, float Y, float Z) firstPosition,
        float startX, float startY, float startZ, uint clientTime, bool heroicFirst,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var budget = new StartingZoneCombat.CombatBudget(20_000, 4 * 1024 * 1024);
        var selected = new List<(uint Spawn, float X, float Y, float Z)>(checked((int)RequiredKills))
        {
            (firstSpawn, firstPosition.X, firstPosition.Y, firstPosition.Z),
        };
        selected.AddRange(Candidates.Where(candidate => candidate.Spawn != firstSpawn)
            .Take(checked((int)RequiredKills - 1)));
        if (selected.Count != (int)RequiredKills)
            throw new MockProtocolException("quest-7 campaign did not have ten distinct source-backed entry-6 candidates");

        var kills = new List<StartingZoneCombat.CombatEvidence>(checked((int)RequiredKills));
        var food = new List<StartingZoneFood.FoodEvidence>();
        var completedTargets = new HashSet<ulong>();
        var allowedTargets = selected
            .Select(candidate => ObjectGuid.WithEntry(HighGuid.Unit, 6, candidate.Spawn).Value)
            .ToHashSet();
        float x = startX, y = startY, z = startZ;
        uint count = 1;
        int nextFallback = 0;
        (uint Spawn, float X, float Y, float Z)? planned = selected[0];
        while (count <= RequiredKills)
        {
            (uint spawn, float targetX, float targetY, float targetZ) = planned.Value;
            WriteProgress(connection, character, budget, "combat-start", count, spawn);
            StartingZoneCombat.CombatEvidence evidence = await StartingZoneCombat.RunAsync(
                connection, character, quest, spawn, (targetX, targetY, targetZ), x, y, z, clientTime,
                cancellationToken, heroic: heroicFirst && count == 1, expectedQuestCount: count, sharedBudget: budget,
                waitForCombatExit: false)
                .ConfigureAwait(false);
            kills.Add(evidence);
            completedTargets.Add(evidence.Target);
            count++;
            x = evidence.PlayerX;
            y = evidence.PlayerY;
            z = evidence.PlayerZ;
            clientTime = evidence.ClientTime;
            WriteProgress(connection, character, budget, "combat-complete", count - 1, spawn);
            IReadOnlyDictionary<int, uint> fields = connection.FieldsOf(character);
            Console.WriteLine("starting-zone campaign-transition=" + JsonSerializer.Serialize(new
            {
                Count = count - 1, ElapsedMs = elapsed.ElapsedMilliseconds,
                PlayerFlags = fields.TryGetValue(UpdateFields.UnitFieldFlags, out uint flags) ? (uint?)flags : null,
                Attackers = (evidence.ActiveAttackers ?? Array.Empty<ulong>()).Take(16).Select(guid =>
                {
                    IReadOnlyDictionary<int, uint> attacker = connection.FieldsOf(guid);
                    return new
                    {
                        Guid = guid,
                        Entry = attacker.TryGetValue(UpdateFields.ObjectFieldEntry, out uint entry) ? (uint?)entry : null,
                        Health = attacker.TryGetValue(UpdateFields.UnitFieldHealth, out uint health) ? (uint?)health : null,
                        Flags = attacker.TryGetValue(UpdateFields.UnitFieldFlags, out uint unitFlags) ? (uint?)unitFlags : null,
                        TargetLow = attacker.TryGetValue(UpdateFields.UnitFieldTarget, out uint low) ? (uint?)low : null,
                        TargetHigh = attacker.TryGetValue(UpdateFields.UnitFieldTarget + 1, out uint high) ? (uint?)high : null,
                    };
                }).ToArray(),
            }));

            ulong? unknown = UnknownAssistingTarget(evidence.ActiveAttackers ?? Array.Empty<ulong>(),
                character, allowedTargets, connection.FieldsOf);
            if (unknown is { } unknownGuid)
                throw new MockProtocolException($"UnknownAssistingTarget: attacker={unknownGuid}, "
                    + $"questCount={count - 1}/10; assisting unit is outside the vetted campaign targets");

            if (count > RequiredKills)
            {
                WriteProgress(connection, character, budget, "final-combat-exit-start", count - 1, spawn);
                await connection.SendAsync(WorldOpcode.CmsgAttackstop, [], cancellationToken).ConfigureAwait(false);
                await StartingZoneCombat.WaitForCombatExitAsync(connection, character, budget, cancellationToken).ConfigureAwait(false);
                WriteProgress(connection, character, budget, "final-combat-exit-complete", count - 1, spawn);
                if (connection.FieldsOf(character).TryGetValue(UpdateFields.UnitFieldHealth, out uint finalHealth)
                    && finalHealth == 0)
                    throw new MockProtocolException("player died after the tenth campaign kill before combat exit");
                break;
            }

            ulong? active = StartingZoneCombat.SelectActiveEligibleAttacker(
                evidence.ActiveAttackers ?? Array.Empty<ulong>(), character, allowedTargets,
                guid => !completedTargets.Contains(guid)
                    && StartingZoneCombat.IsActiveEntrySixTarget(connection.FieldsOf(guid), character));
            if (active is { } activeGuid)
            {
                (uint activeSpawn, _, _, _) = selected.First(candidate =>
                    ObjectGuid.WithEntry(HighGuid.Unit, 6, candidate.Spawn).Value == activeGuid);
                MockPosition activePosition = connection.PositionOf(activeGuid)
                    ?? throw new MockProtocolException($"active entry-6 attacker {activeGuid} had no observed position");
                if (!activePosition.IsFinite)
                    throw new MockProtocolException($"active entry-6 attacker {activeGuid} had a non-finite observed position");
                planned = (activeSpawn, activePosition.X, activePosition.Y, activePosition.Z);
                continue;
            }

            WriteProgress(connection, character, budget, "combat-exit-start", count - 1, spawn);
            await connection.SendAsync(WorldOpcode.CmsgAttackstop, [], cancellationToken).ConfigureAwait(false);
            await StartingZoneCombat.WaitForCombatExitAsync(connection, character, budget, cancellationToken).ConfigureAwait(false);
            WriteProgress(connection, character, budget, "combat-exit-complete", count - 1, spawn);
            fields = connection.FieldsOf(character);
            if (fields.TryGetValue(UpdateFields.UnitFieldHealth, out uint observedHealth) && observedHealth == 0)
                throw new MockProtocolException($"player died after campaign kill {count - 1}/10 before food or next walk");
            if (CanConsumeFood(fields))
            {
                WriteProgress(connection, character, budget, "food-start", count - 1, spawn);
                food.Add(await StartingZoneFood.RunAsync(connection, character, budget, cancellationToken).ConfigureAwait(false));
                WriteProgress(connection, character, budget, "food-complete", count - 1, spawn);
            }

            while (nextFallback < selected.Count
                && completedTargets.Contains(ObjectGuid.WithEntry(HighGuid.Unit, 6, selected[nextFallback].Spawn).Value))
                nextFallback++;
            if (nextFallback >= selected.Count)
                throw new MockProtocolException("quest-7 campaign exhausted its distinct source-backed targets before ten kills");
            planned = selected[nextFallback++];
        }

        if (!StartingZoneProbe.IsQuestComplete(connection.FieldsOf(character), quest))
            throw new MockProtocolException("quest-7 campaign reached ten kills without a complete journal state");
        ulong ender = ObjectGuid.WithEntry(HighGuid.Unit, StartingZoneProbe.NpcEntry, StartingZoneProbe.NpcSpawn).Value;
        MockPosition turnIn = connection.PositionOf(ender)
            ?? throw new MockProtocolException("quest ender has no observed position for the return walk");
        if (!turnIn.IsFinite)
            throw new MockProtocolException("quest ender has a non-finite observed position");
        clientTime = await StartingZoneCombat.WalkAsync(connection, budget, x, y, z,
            turnIn.X, turnIn.Y, turnIn.Z, clientTime, cancellationToken).ConfigureAwait(false);
        MockQuestComplete reward = await StartingZoneProbe.CompleteQuest7Async(connection, character, cancellationToken, budget)
            .ConfigureAwait(false);
        if (!IsRewardIdentity(reward, quest))
            throw new MockProtocolException("quest-7 completion packet had the wrong quest identity or completion type");
        if (StartingZoneProbe.HasQuest(connection.FieldsOf(character), quest))
            throw new MockProtocolException("quest-7 campaign reward did not clear the journal entry");

        return new CampaignEvidence(kills.AsReadOnly(), food.AsReadOnly(), true, reward,
            turnIn.X, turnIn.Y, turnIn.Z, clientTime, budget.Frames, budget.Bytes);
    }

    internal static bool CanConsumeFood(IReadOnlyDictionary<int, uint> fields)
    {
        if (!fields.TryGetValue(UpdateFields.UnitFieldHealth, out uint health)
            || !fields.TryGetValue(UpdateFields.UnitFieldMaxhealth, out uint maxHealth)
            || health == 0 || maxHealth == 0)
            return false;
        return fields.TryGetValue(UpdateFields.UnitFieldFlags, out uint flags)
            && (flags & (uint)UnitFlags.InCombat) == 0
            && (ulong)health * 100 <= (ulong)maxHealth * 60;
    }

    internal static ulong? UnknownAssistingTarget(IEnumerable<ulong> incoming, ulong character,
        IReadOnlySet<ulong> allowed, Func<ulong, IReadOnlyDictionary<int, uint>> fieldsOf)
    {
        foreach (ulong attacker in incoming)
        {
            if (attacker == 0 || attacker == character || allowed.Contains(attacker)) continue;
            IReadOnlyDictionary<int, uint> fields = fieldsOf(attacker);
            if (fields.TryGetValue(UpdateFields.UnitFieldHealth, out uint health) && health == 0) continue;
            if (fields.TryGetValue(UpdateFields.UnitFieldTarget, out uint low) && low != (uint)character) continue;
            if (fields.TryGetValue(UpdateFields.UnitFieldTarget + 1, out uint high) && high != (uint)(character >> 32)) continue;
            if (fields.TryGetValue(UpdateFields.UnitFieldFlags, out uint flags) && (flags & (uint)UnitFlags.InCombat) == 0) continue;
            return attacker;
        }
        return null;
    }

    private static void WriteProgress(ScenarioConnection connection, ulong character,
        StartingZoneCombat.CombatBudget budget, string phase, uint count, uint spawn)
    {
        IReadOnlyDictionary<int, uint> fields = connection.FieldsOf(character);
        Console.WriteLine("starting-zone campaign-progress=" + JsonSerializer.Serialize(new
        {
            Phase = phase, Count = count, Spawn = spawn,
            Health = fields.TryGetValue(UpdateFields.UnitFieldHealth, out uint health) ? (uint?)health : null,
            MaxHealth = fields.TryGetValue(UpdateFields.UnitFieldMaxhealth, out uint maximum) ? (uint?)maximum : null,
            QuestCount = StartingZoneCombat.QuestProgress(fields, 7), budget.Frames, budget.Bytes,
        }));
    }
}
