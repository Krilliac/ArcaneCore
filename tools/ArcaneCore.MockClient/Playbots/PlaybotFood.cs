using ArcaneCore.Game;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;

namespace ArcaneCore.MockClient.Playbots;

/// <summary>Pure, observation-only selector for the known starter food item.</summary>
internal static class PlaybotFood
{
    private const uint LowHealthPercent = 60;

    internal static PlaybotCandidate? FindCandidate(long revision, ulong character,
        IReadOnlyDictionary<int, uint> playerFields,
        Func<ulong, IReadOnlyDictionary<int, uint>> fieldsOf)
    {
        if (character == 0 || !playerFields.TryGetValue(UpdateFields.UnitFieldHealth, out uint health)
            || !playerFields.TryGetValue(UpdateFields.UnitFieldMaxhealth, out uint maxHealth)
            || health == 0 || maxHealth == 0
            || (ulong)health * 100 >= (ulong)maxHealth * LowHealthPercent)
            return null;

        if (!playerFields.TryGetValue(UpdateFields.UnitFieldFlags, out uint flags)
            || (flags & (uint)UnitFlags.InCombat) != 0
            || StartingZoneFood.HasFoodAura(playerFields))
            return null;

        (ulong Guid, byte Slot, uint Stack) food;
        try
        {
            food = StartingZoneFood.FindFood(playerFields, character, fieldsOf);
        }
        catch (MockProtocolException)
        {
            // Missing, malformed, or foreign observed food is an expected absence for a selector.
            return null;
        }

        if (food.Stack == 0)
            return null;

        return new PlaybotCandidate($"{revision}:eat:{food.Guid}:{food.Stack}",
            PlaybotActionKind.Eat, 100, Target: food.Guid, Value: food.Slot, ExpectedStack: food.Stack);
    }
}
