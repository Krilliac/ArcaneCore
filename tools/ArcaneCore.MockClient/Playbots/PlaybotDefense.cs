using ArcaneCore.Game;
using ArcaneCore.MockClient.Scenarios;

namespace ArcaneCore.MockClient.Playbots;

/// <summary>
/// Deterministic defense candidate selection for an observed nearby attacker. This is deliberately
/// narrower than ordinary target selection: every fact needed to defend is present in the current
/// update state, including the full target GUID and the configured creature entry.
/// </summary>
internal static class PlaybotDefense
{
    private const uint ExcludedAttackFlags = (uint)(UnitFlags.Spawning | UnitFlags.NotAttackable1
        | UnitFlags.NonAttackable2 | UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer
        | UnitFlags.PlayerControlled);

    internal static PlaybotCandidate? FindCandidate(PlaybotState state, uint attackEntry)
    {
        if (state.Health is not { } health || state.MaximumHealth is not { } maximum
            || maximum == 0 || (ulong)health * 100 < (ulong)maximum * 60
            || state.InCombat != true || state.AttackTarget != 0 || state.CharacterGuid == 0 || state.Released
            || !Finite(state.Position))
        {
            return null;
        }

        if (attackEntry == 0)
        {
            return null;
        }

        PlaybotObject? attacker = state.Objects
            .Where(obj => (obj.Guid >> 48) == 0xF130
                && obj.Entry == attackEntry
                && obj.Health is > 0
                && obj.Flags is { } flags
                && (flags & (uint)UnitFlags.InCombat) != 0
                && (flags & ExcludedAttackFlags) == 0
                && obj.TargetGuid is { } target && target == state.CharacterGuid
                && obj.Position is { } position && Finite(position))
            .Select(obj => (Object: obj, Distance: Distance(state.Position, obj.Position!)))
            .Where(value => float.IsFinite(value.Distance) && value.Distance <= 3)
            .OrderBy(value => value.Distance)
            .ThenBy(value => value.Object.Guid)
            .Select(value => value.Object)
            .FirstOrDefault();

        return attacker is null
            ? null
            : new PlaybotCandidate($"{state.Revision}:defend-{attacker.Guid}", PlaybotActionKind.Attack, 100, attacker.Guid);
    }

    private static float Distance(MockPosition left, MockPosition right)
    {
        float dx = left.X - right.X;
        float dy = left.Y - right.Y;
        float dz = left.Z - right.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static bool Finite(MockPosition position)
        => float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
}
