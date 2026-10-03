using Xunit;
using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Game.Tests.Conditions;

/// <summary>Builders shared by the condition tests.</summary>
internal static class ConditionTestSupport
{
    public static Player CreatePlayer(Race race = Race.Human, Class cls = Class.Warrior, Gender gender = Gender.Male,
        byte level = 1, float x = 0, uint guid = 1)
    {
        var character = new CharacterRecord
        {
            Id = (int)guid,
            AccountId = 1,
            Name = $"P{guid}",
            Race = (byte)race,
            Class = (byte)cls,
            Gender = (byte)gender,
            Level = level,
            MapId = 0,
            ZoneId = 12,
            X = x,
            Y = 0,
            Z = 83.5f,
        };
        var appearance = new PlayerAppearance(
            DisplayId: 49, FactionTemplate: 1, PowerType.Rage, BaseHealth: 60, BaseMana: 0,
            MaxHealth: 60, MaxPower: 1000, StartPower: 0, NextLevelXp: 400);
        return new Player(character, appearance, new FakeSession((int)guid));
    }

    public static NpcInfo CreateNpc(float x = 0, uint entry = 500)
        => new(ObjectGuid.WithEntry(HighGuid.Unit, entry, 77), entry, 77, NpcFlags.Gossip, 0, x, 0, 83.5f, 0.5f, true, false, false, false, 0);

    public static ConditionRecord Row(uint entry, ConditionType type, uint v1 = 0, uint v2 = 0, uint v3 = 0, uint v4 = 0, ConditionFlags flags = ConditionFlags.None)
        => new(entry, (int)type, v1, v2, v3, v4, (byte)flags);

    public static ConditionEvaluator Evaluator(ConditionContext? context = null, params ConditionRecord[] rows)
    {
        ConditionTable table = ConditionTable.Build(rows);
        Assert.Empty(table.Rejected);
        return new ConditionEvaluator(table, context ?? new ConditionContext());
    }

    /// <summary>Scripted quest state: statuses by quest id.</summary>
    internal sealed class FakeQuests : IConditionQuests
    {
        public Dictionary<uint, (bool Incomplete, bool CompleteNotRewarded, bool Rewarded)> State { get; } = [];

        public HashSet<uint> Takeable { get; } = [];

        public bool? IsRewarded(Player player, uint questId) => State.TryGetValue(questId, out var s) && s.Rewarded;

        public bool? IsCurrent(Player player, uint questId, byte mode)
        {
            if (!State.TryGetValue(questId, out var s))
            {
                return false;
            }

            return mode switch
            {
                1 => s.Incomplete,
                2 => s.CompleteNotRewarded,
                _ => s.Incomplete || s.CompleteNotRewarded,
            };
        }

        public bool? CanTakeQuest(Player player, uint questId) => Takeable.Contains(questId);
    }
}
