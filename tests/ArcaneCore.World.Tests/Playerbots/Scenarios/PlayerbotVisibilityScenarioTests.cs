using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Scenarios;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Who sees whom across two sessions, through the real world and the map's visibility pass: two bots 150 yards apart
/// (out of each other's 100-yard sight) and a creature moved between them, then the bots brought together. The creature
/// moves exercise the players-only viewer visit of a moving non-player object (vmangos Map::UpdateObjectVisibility); the
/// bot moves exercise the full visit of a moving player.
/// </summary>
public sealed class PlayerbotVisibilityScenarioTests
{
    [Fact]
    public async Task TwoBots_AndAMovingCreature_SeeExactlyWhatIsInRange()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new DelegateScenario("visibility", async context =>
        {
            ScenarioBot a = await context.StepAsync("login A", () => context.LoginAsync(PlayerbotScenarioCatalog.BotA));
            ScenarioBot b = await context.StepAsync("login B", () => context.LoginAsync(PlayerbotScenarioCatalog.BotB));

            // The wolf stands 60 yards east of the start: 60 from A, 90 from B, both in sight; A and B are 150 apart.
            await context.StepAsync("stand 150 yards apart", async () =>
            {
                await context.PlaceAsync(a, 0, StartX, StartY, StartZ);
                await context.PlaceAsync(b, 0, StartX + 150, StartY, StartZ);
                await WaitSeesAsync(context, a, b.Guid, false);
                await WaitSeesAsync(context, b, a.Guid, false);
                await WaitSeesAsync(context, a, Wolf, true);
                await WaitSeesAsync(context, b, Wolf, true);
            });

            await context.StepAsync("the wolf moves next to B: only B still sees it", async () =>
            {
                long mark = a.Mark();
                await MoveWolfAsync(context, a, StartX + 200);
                await WaitSeesAsync(context, a, Wolf, false);
                await WaitSeesAsync(context, b, Wolf, true);
                ScenarioContext.Expect(
                    a.Log.Received(WorldOpcode.SmsgUpdateObject, mark).Count + a.Log.Received(WorldOpcode.SmsgCompressedUpdateObject, mark).Count > 0,
                    "A's client got no update (out-of-range block) when the wolf left its sight");
            });

            await context.StepAsync("the wolf moves next to A: only A sees it", async () =>
            {
                await MoveWolfAsync(context, a, StartX - 50);
                await WaitSeesAsync(context, a, Wolf, true);
                await WaitSeesAsync(context, b, Wolf, false);
            });

            await context.StepAsync("B walks to A: they see each other", async () =>
            {
                await context.PlaceAsync(b, 0, StartX + 40, StartY, StartZ);
                await WaitSeesAsync(context, a, b.Guid, true);
                await WaitSeesAsync(context, b, a.Guid, true);
                await WaitSeesAsync(context, b, Wolf, true); // 90 yards
            });
        }));
    }

    private static Task WaitSeesAsync(ScenarioContext context, ScenarioBot viewer, ObjectGuid target, bool sees)
        => context.WaitUntilAsync(
            $"{viewer.Name} {(sees ? "sees" : "does not see")} {target}",
            () => viewer.RequirePlayerForTests().VisibleObjects.Contains(target) == sees);

    /// <summary>Relocate the (idle, out-of-combat) wolf on the world thread, as a creature step would.</summary>
    private static Task MoveWolfAsync(ScenarioContext context, ScenarioBot bot, float x) => context.ReadAsync(() =>
    {
        Creature wolf = TestSteps.Find(bot, Wolf) ?? throw new ScenarioAssertionException("the wolf is not in the map");
        wolf.SetPosition(x, StartY, StartZ, 0f);
        return true;
    });
}
