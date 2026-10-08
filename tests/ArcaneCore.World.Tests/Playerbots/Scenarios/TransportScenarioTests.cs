using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Transports;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Transports;
using Xunit;
using static ArcaneCore.World.Tests.Transports.TransportWorldContent;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Ships across sessions with scripted bots (docs/areas/transports.md): two bots board a ferry through their movement, duel
/// aboard while it sails (the ship is the duel area, so the distance from the flag does not count), and the duel is lost when
/// one steps off; one bot rides a ship through its map change and arrives aboard on the other map.
/// </summary>
public sealed class TransportScenarioTests
{
    [Fact]
    public async Task ShipDuel_SurvivesSailingAway_AndEndsFledWhenOneStepsOff()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services => Register(services));
        await world.RunPassingAsync(new ShipDuelScenario());
    }

    [Fact]
    public async Task Crossing_CarriesThePassengerToTheOtherMap_StillAboard()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services => Register(services));
        await world.RunPassingAsync(new CrossingScenario());
    }

    private sealed class ShipDuelScenario : IPlayerbotScenario
    {
        public string Name => "ship-duel";

        public string Description => "two bots board a ferry, duel aboard while it sails, one steps off and flees the duel";

        public async Task RunAsync(ScenarioContext context)
        {
            ScenarioBot a = await context.StepAsync("login A", () => context.LoginAsync(PlayerbotScenarioCatalog.BotA));
            ScenarioBot b = await context.StepAsync("login B", () => context.LoginAsync(PlayerbotScenarioCatalog.BotB));
            ShipTransport ferry = await context.StepAsync("the ferry waits at the start", async () =>
            {
                ShipTransport ship = await context.ShipAsync(Ferry);
                ScenarioContext.Expect(await context.ReadAsync(() => MathF.Abs(ship.X - StartX) < 0.01f && ReferenceEquals(ship.CurrentMap, context.World.FindMap(0))),
                    "the ferry is not waiting on map 0");
                return ship;
            });
            await context.StepAsync("both stand on the dock", async () =>
            {
                await context.PlaceAsync(a, 0, StartX + 2f, StartY, StartZ);
                await context.PlaceAsync(b, 0, StartX - 2f, StartY, StartZ);
            });
            await context.StepAsync("both board through their movement", async () =>
            {
                ScenarioContext.Expect(await a.BoardAsync(ferry, 2f, 0f, 5f), "A heartbeat refused");
                ScenarioContext.Expect(await b.BoardAsync(ferry, -2f, 0f, 5f), "B heartbeat refused");
                await context.WaitUntilAsync("both are passengers", () =>
                    ReferenceEquals(a.RequirePlayer().Transport, ferry) && ReferenceEquals(b.RequirePlayer().Transport, ferry));
                ScenarioContext.Expect(await a.TimeSkippedAsync(100), "A time skip refused");
            });
            await context.StepAsync("A knows Duel", () => context.LearnSpellAsync(a, ScenarioBot.DuelSpell));
            DuelRequestedView request = await context.StepAsync("A challenges B aboard", async () =>
            {
                long mark = b.Mark();
                ScenarioContext.Expect(await a.RequestDuelAsync(b.Guid), "duel cast refused");
                return await b.WaitForPacketAsync(WorldOpcode.SmsgDuelRequested, ScenarioDecoders.DuelRequested, since: mark);
            });
            await context.StepAsync("the duel is bound to the ferry and starts", async () =>
            {
                await context.ExpectAsync(a, "A's duel names the ferry", p => p.Duel?.TransportGuid == Ferry);
                await context.ExpectAsync(b, "B's duel names the ferry", p => p.Duel?.TransportGuid == Ferry);
                ScenarioContext.Expect(await b.AcceptDuelAsync(request.Arbiter), "duel accept refused");
                await context.WaitUntilAsync("the duel started", () => a.RequirePlayer().DuelTeam != 0 && b.RequirePlayer().DuelTeam != 0,
                    TimeSpan.FromSeconds(10));
            });
            long sailMark = b.Mark();
            await context.StepAsync("the ferry sails 80 yards with both aboard; nobody is out of bounds", async () =>
            {
                await context.WaitUntilAsync("the ferry is 80 yards out", () => ferry.X > StartX + 80f, TimeSpan.FromSeconds(FirstStopSeconds + 60));
                await context.ExpectAsync(b, "B moved with the ferry", p => p.X > StartX + 70f && ReferenceEquals(p.Transport, ferry));
                await context.ExpectAsync(a, "A still duels", p => p.Duel is { OutOfBoundSeconds: 0 });
                ScenarioContext.ExpectEqual(0, b.Received(WorldOpcode.SmsgDuelOutofbounds, static p => p, sailMark).Count, "out-of-bounds warnings while aboard");
            });
            long leaveMark = b.Mark();
            await context.StepAsync("B steps off: out of bounds at once", async () =>
            {
                ScenarioContext.Expect(await b.LeaveShipAsync(), "B heartbeat refused");
                await context.WaitUntilAsync("B is off the ferry", () => b.RequirePlayer().Transport is null);
                await b.WaitForPacketAsync(WorldOpcode.SmsgDuelOutofbounds, static p => p, since: leaveMark);
            });
            await context.StepAsync("ten seconds off the ferry: B fled, A wins", async () =>
            {
                DuelWinnerView winner = await a.WaitForPacketAsync(WorldOpcode.SmsgDuelWinner, ScenarioDecoders.DuelWinner, since: leaveMark,
                    timeout: TimeSpan.FromSeconds(20));
                ScenarioContext.ExpectEqual((byte)1, winner.Reason, "duel winner reason (1 = fled)");
                ScenarioContext.Expect(winner.Winner.Equals(a.Name, StringComparison.OrdinalIgnoreCase), $"winner {winner.Winner} is not {a.Name}");
                await context.WaitUntilAsync("the duel state is cleared", () => a.RequirePlayer().Duel is null && b.RequirePlayer().Duel is null);
            });
        }
    }

    private sealed class CrossingScenario : IPlayerbotScenario
    {
        public string Name => "ship-crossing";

        public string Description => "a bot rides a ship through its map change and arrives aboard";

        public async Task RunAsync(ScenarioContext context)
        {
            ScenarioBot a = await context.StepAsync("login A", () => context.LoginAsync(PlayerbotScenarioCatalog.BotA));
            ShipTransport ship = await context.StepAsync("the crossing ship waits on map 0", () => context.ShipAsync(Crossing));
            await context.StepAsync("A boards at the dock", async () =>
            {
                await context.PlaceAsync(a, 0, StartX + 1f, CrossingY, StartZ);
                ScenarioContext.Expect(await a.BoardAsync(ship, 1f, 0.5f, 6f), "heartbeat refused");
                await context.WaitUntilAsync("A is a passenger", () => ReferenceEquals(a.RequirePlayer().Transport, ship));
            });
            long mark = a.Mark();
            await context.StepAsync("the ship changes maps; the transfer names it and carries the offset", async () =>
            {
                TransferPendingView pending = await a.WaitForPacketAsync(WorldOpcode.SmsgTransferPending, ScenarioTransports.TransferPending,
                    since: mark, timeout: TimeSpan.FromSeconds(FirstStopSeconds + 60));
                ScenarioContext.ExpectEqual(new TransferPendingView(1, Crossing, 0), pending, "SMSG_TRANSFER_PENDING");
                NewWorldView arrival = await a.WaitForPacketAsync(WorldOpcode.SmsgNewWorld, ScenarioTransports.NewWorld, since: mark);
                ScenarioContext.ExpectEqual((1u, 1f, 0.5f, 6f), (arrival.MapId, arrival.X, arrival.Y, arrival.Z), "SMSG_NEW_WORLD");
            });
            await context.StepAsync("A arrives on map 1 aboard, at its offset from the ship", async () =>
            {
                await context.WaitUntilAsync("A is in map 1", () => a.Session!.Player is { IsInWorld: true, Map.MapId: 1 });
                await context.ExpectAsync(a, "still aboard", p => ReferenceEquals(p.Transport, ship) && p.Movement.HasFlag(MovementFlags.OnTransport));
                await context.ExpectAsync(a, "at the ship's position plus the offset", p =>
                {
                    float x = 1f, y = 0.5f, z = 6f, o = 0f;
                    ship.CalculatePassengerPosition(ref x, ref y, ref z, ref o);
                    return MathF.Abs(p.X - x) < 0.01f && MathF.Abs(p.Y - y) < 0.01f && MathF.Abs(p.Z - z) < 0.01f;
                });
            });
            await context.StepAsync("A sails on with the ship on map 1", async () =>
            {
                await context.WaitUntilAsync("the ship leaves the map 1 stop", () => ship.X > FarX + 20f, TimeSpan.FromSeconds(30));
                await context.ExpectAsync(a, "moved with it", p => p.X > FarX + 10f && p.MapId == 1);
            });
        }
    }
}
