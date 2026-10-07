using ArcaneCore.Game;
using ArcaneCore.MockClient.Scenarios;

namespace ArcaneCore.MockClient.Playbots;

internal sealed record PlaybotObject(ulong Guid, uint? Entry, uint? Health, uint? Flags, uint? DynamicFlags, MockPosition? Position,
    ulong? TargetGuid = null);
internal sealed record PlaybotState(long Revision, uint? Health, uint? MaximumHealth, bool? InCombat,
    MockPosition Position, MockPosition Origin, IReadOnlyList<PlaybotObject> Objects,
    IReadOnlySet<ulong> Queried, IReadOnlySet<ulong> Looted, ulong AttackTarget,
    StartingZoneLoot.LootWindow? Loot, bool LootPending, bool Released, bool HeroicQueued, bool KnowsHeroic, uint? Rage,
    int MoveCount, ulong CharacterGuid = 0);

internal static class PlaybotPolicy
{
    internal static float Distance(MockPosition a, MockPosition b)
        => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2) + MathF.Pow(a.Z - b.Z, 2));

    internal static IReadOnlyList<PlaybotCandidate> Candidates(PlaybotState state, uint attackEntry, bool movement)
    {
        string Id(string name) => $"{state.Revision}:{name}";
        List<PlaybotCandidate> choices = [new(Id("observe"), PlaybotActionKind.Observe, 0)];
        if (state.Health is null || state.MaximumHealth is null or 0) return choices;
        if (state.Health == 0)
        {
            if (!state.Released) choices.Add(new(Id("release"), PlaybotActionKind.ReleaseSpirit, 100));
            return choices;
        }
        bool injured = (ulong)state.Health.Value * 100 < (ulong)state.MaximumHealth.Value * 60;
        PlaybotObject? attacked = state.Objects.FirstOrDefault(o => o.Guid == state.AttackTarget);
        if (state.AttackTarget != 0)
        {
            if (injured || attacked?.Health is null or 0)
                return [new(Id("stop"), PlaybotActionKind.StopAttack, 100)];
            else if (state.KnowsHeroic && !state.HeroicQueued && state.Rage >= 150)
                choices.Add(new(Id("heroic"), PlaybotActionKind.CastKnownSpell, 80, state.AttackTarget, Value: 78));
            return choices;
        }
        if (PlaybotDefense.FindCandidate(state, attackEntry) is { } defense) return [defense];
        if (state.InCombat != false || state.Released) return choices;
        if (state.LootPending) return choices;
        if (state.Loot is { } loot)
        {
            StartingZoneLoot.LootSlot? item = loot.Items.FirstOrDefault(item => item.SlotType == 0);
            if (item is not null) choices.Add(new(Id($"loot-{item.Slot}"), PlaybotActionKind.LootItem, 85, loot.Guid, Value: item.Slot));
            else if (loot.Gold > 0) choices.Add(new(Id("money"), PlaybotActionKind.LootMoney, 85, loot.Guid));
            else choices.Add(new(Id("close-loot"), PlaybotActionKind.CloseLoot, 85, loot.Guid));
            return choices;
        }
        foreach (PlaybotObject obj in state.Objects.Where(o => (o.Guid >> 48) == 0xF130
            && o.Entry is > 0 && o.Position is not null).OrderBy(o => Distance(state.Position, o.Position!)).Take(32))
        {
            if (!state.Queried.Contains(obj.Guid))
                choices.Add(new(Id($"query-{obj.Guid}"), PlaybotActionKind.QueryCreature, 70, obj.Guid, Value: obj.Entry!.Value));
            float distance = Distance(state.Position, obj.Position!);
            bool eligible = attackEntry != 0 && obj.Entry == attackEntry && obj.Flags is { } flags
                && (flags & (uint)(UnitFlags.Spawning | UnitFlags.NotAttackable1 | UnitFlags.NonAttackable2
                    | UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer | UnitFlags.PlayerControlled)) == 0;
            if (obj.Health == 0 && obj.DynamicFlags is { } dynamic && (dynamic & 1) != 0
                && distance <= 4 && !state.Looted.Contains(obj.Guid))
                choices.Add(new(Id($"open-{obj.Guid}"), PlaybotActionKind.OpenLoot, 90, obj.Guid));
            if (eligible && obj.Health > 0 && !injured)
            {
                if (distance <= 3)
                    choices.Add(new(Id($"attack-{obj.Guid}"), PlaybotActionKind.Attack, 60, obj.Guid));
                else if (movement && state.MoveCount < 20 && distance <= 25 && Distance(state.Origin, obj.Position!) <= 30
                    && Math.Abs(obj.Position!.Z - state.Position.Z) <= 2)
                {
                    float fraction = Math.Min(1, 3 / distance);
                    choices.Add(new(Id($"approach-{obj.Guid}"), PlaybotActionKind.Move, 50, obj.Guid,
                        state.Position.X + (obj.Position.X - state.Position.X) * fraction,
                        state.Position.Y + (obj.Position.Y - state.Position.Y) * fraction,
                        state.Position.Z + (obj.Position.Z - state.Position.Z) * fraction));
                }
            }
        }
        // A small local exploration square. No terrain/pathfinding claim: stop on server rejection.
        if (movement && !injured && state.MoveCount < 20 && !choices.Any(c => c.Kind is PlaybotActionKind.Move or PlaybotActionKind.Attack))
        {
            float angle = state.MoveCount % 4 * MathF.PI / 2;
            float x = state.Position.X + 3 * MathF.Cos(angle), y = state.Position.Y + 3 * MathF.Sin(angle);
            if (Distance(state.Origin, new(x, y, state.Position.Z)) <= 30)
                choices.Add(new(Id("explore"), PlaybotActionKind.Move, 10, X: x, Y: y, Z: state.Position.Z));
        }
        return choices.OrderByDescending(c => c.Priority).ThenBy(c => c.Id, StringComparer.Ordinal).Take(32).ToArray();
    }
}
