using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Several matches of one battleground type and bracket, played end to end by managed bots through the client opcodes (the synthetic Warsong
/// Gulch content of <see cref="WarsongGulchTestContent"/> with a small template). The retail rules they pin, all from vmangos:
/// <list type="bullet">
/// <item>A queue run fills the running matches that have free slots first (<c>CheckFreeSlots</c> / <c>FillPlayersToBg</c>,
/// BattleGroundMgr.cpp:704-740, 456-529) and then creates a new match of the same type and bracket as soon as each side has the minimum
/// queued, however many matches already run (<c>CheckCreateNewBg</c> / <c>CheckNormalMatch</c>, BattleGroundMgr.cpp:742-868, 587-628;
/// <c>Update</c> :870-891). A running match's free slots are offered while it prepares (STATUS_WAIT_JOIN) and while it is in progress
/// (<c>GetFreeSlotsForTeam</c>, BattleGround.cpp:1185-1189).</item>
/// <item>Each match gets the lowest unused client instance id of its type and bracket (<c>CreateClientVisibleInstanceId</c>,
/// BattleGroundMgr.cpp:1217-1236), and SMSG_BATTLEFIELD_LIST lists those ids for the asker's bracket (<c>BuildBattleGroundListPacket</c>,
/// BattleGroundMgr.cpp:1432-1448; layout Server/Packets/Battleground.cpp:203-214 and wow_messages smsg_battlefield_list.wowm 1.12:
/// u64 battlemaster, u32 map, u8 bracket, u32 count, u32 ids).</item>
/// <item>CMSG_BATTLEMASTER_JOIN's instance id (0 = first available) is the group's <c>desiredInstanceId</c> (BattleGroundHandler.cpp:153-156,
/// 189); a queued group with one is only invited to that match (<c>SelectionPool::AddGroup</c>, BattleGroundMgr.cpp:125-138).</item>
/// <item>A participant who leaves frees an invitation, puts the match back in the free-slot list and schedules a queue run
/// (<c>RemovePlayerAtLeave</c>, BattleGround.cpp:966-980), so the slot is refilled from the queue.</item>
/// </list>
/// </summary>
public sealed class BattlegroundInstancesWorldTests
{
    private const uint Wsg = 489;

    private sealed class Scenario(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    private static async Task RunAsync(uint minPerTeam, uint maxPerTeam, string name, Func<ScenarioContext, Task> body)
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(s => WarsongGulchTestContent.Register(s, minPerTeam, maxPerTeam));
        ScenarioReport report = await world.RunAsync(new Scenario(name, body), new ScenarioRunOptions
        {
            StepTimeout = TimeSpan.FromSeconds(30),
            MaxDuration = TimeSpan.FromMinutes(6),
        });
        Assert.True(report.Passed, report.ToString());
    }

    private static BattlegroundManager Manager(ScenarioContext context) => context.Services.GetRequiredService<BattlegroundFeature>().Manager;

    private static Task<ScenarioBot> AllianceAsync(ScenarioContext context, string name) => context.LoginAsync(name, race: 1, characterClass: 1);

    private static Task<ScenarioBot> HordeAsync(ScenarioContext context, string name) => context.LoginAsync(name, race: 2, characterClass: 1);

    /// <summary>Stand at the side's battlemaster and talk to it; returns the battlemaster and the SMSG_BATTLEFIELD_LIST body it answered with.</summary>
    private static async Task<(ObjectGuid Master, byte[] List)> HelloAsync(ScenarioContext context, ScenarioBot bot)
    {
        uint home = await bot.ReadAsync(p => p.MapId);
        var master = context.FindBattlemaster(home, BattlegroundType.WarsongGulch) ?? throw new ScenarioAssertionException($"no battlemaster on map {home}");
        await context.PlaceAsync(bot, home, master.X + 2f, master.Y, master.Z, MathF.PI);
        long mark = bot.Mark();
        ScenarioContext.Expect(await bot.BattlemasterHelloAsync(master.Guid), "hello refused");
        byte[] list = await bot.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldList, p => p, since: mark);
        return (master.Guid, list);
    }

    /// <summary>Queue through CMSG_BATTLEMASTER_JOIN with <paramref name="instanceId"/>; returns the first status it was answered with.</summary>
    private static Task<BattlefieldStatusView> QueueAsync(ScenarioContext context, ScenarioBot bot, uint instanceId = 0)
        => context.StepAsync($"{bot.Name} queues for Warsong Gulch (instance {instanceId})", async () =>
        {
            (ObjectGuid master, _) = await HelloAsync(context, bot);
            long mark = bot.Mark();
            ScenarioContext.Expect(await bot.JoinBattlegroundAsync(master, Wsg, instanceId), "join refused");
            return await bot.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldStatus, ScenarioBattlegrounds.BattlefieldStatus,
                s => s.MapId == Wsg && s.Status is BattlegroundStatus.WaitQueue, mark);
        });

    /// <summary>The invitation (STATUS_WAIT_JOIN) the bot got for Warsong Gulch since <paramref name="since"/>.</summary>
    private static Task<BattlefieldStatusView> InvitationAsync(ScenarioBot bot, long since = 0)
        => bot.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldStatus, ScenarioBattlegrounds.BattlefieldStatus,
            s => s.MapId == Wsg && s.Status == BattlegroundStatus.WaitJoin, since);

    /// <summary>Accept the invitation and wait until the bot is a participant of a match; returns that match.</summary>
    private static Task<Battleground> PortInAsync(ScenarioContext context, ScenarioBot bot) => context.StepAsync($"{bot.Name} ports in", async () =>
    {
        ScenarioContext.Expect(await bot.PortBattlegroundAsync(Wsg), "port refused");
        await context.WaitUntilAsync($"{bot.Name} is in a match", () => bot.Session!.Player is { IsInWorld: true, Map: { MapId: Wsg } } p
            && context.Services.GetRequiredService<BattlegroundFeature>().BattlegroundOf(p.Guid)?.PlayerTeam(p.Guid) is not null);
        return (await context.BattlegroundOfAsync(bot))!;
    });

    /// <summary>The instance a queued bot is invited to (0 while it waits), read on the world thread.</summary>
    private static Task<uint> InvitedToAsync(ScenarioContext context, ScenarioBot bot)
        => context.ReadAsync(() => Manager(context).QueuedGroupOf(bot.Guid, BattlegroundType.WarsongGulch)?.InvitedToInstanceId ?? 0);

    /// <summary>One Alliance and one Horde bot queue, are invited and port in; returns the match they share.</summary>
    private static async Task<(ScenarioBot Alliance, ScenarioBot Horde, Battleground Match)> PairIntoMatchAsync(ScenarioContext context, string allianceName, string hordeName)
    {
        ScenarioBot ally = await context.StepAsync("login " + allianceName, () => AllianceAsync(context, allianceName));
        ScenarioBot horde = await context.StepAsync("login " + hordeName, () => HordeAsync(context, hordeName));
        await QueueAsync(context, ally);
        await QueueAsync(context, horde);
        await InvitationAsync(ally);
        await InvitationAsync(horde);
        Battleground a = await PortInAsync(context, ally);
        Battleground h = await PortInAsync(context, horde);
        ScenarioContext.Expect(ReferenceEquals(a, h), $"{allianceName} and {hordeName} are not in one match");
        return (ally, horde, a);
    }

    /// <summary>
    /// With a one-per-team template the first pair fills its match, so the second pair, queued while the first match runs, gets a second
    /// Warsong Gulch of the same bracket on its own map instance (client instance id 2); a battlemaster then lists both ids, byte for byte.
    /// </summary>
    [Fact]
    public Task TwoMatchesOfOneTypeAndBracketRunAtOnce_AndTheBattlemasterListsBothClientInstanceIds() => RunAsync(1, 1, "bg-two-instances", async context =>
    {
        (ScenarioBot a1, ScenarioBot h1, Battleground first) = await PairIntoMatchAsync(context, "Bgtwoallya", "Bgtwohordea");
        (ScenarioBot a2, ScenarioBot h2, Battleground second) = await PairIntoMatchAsync(context, "Bgtwoallyb", "Bgtwohordeb");

        await context.StepAsync("both matches run at once, same type and bracket, separate instances", async () =>
        {
            ScenarioContext.Expect(!ReferenceEquals(first, second), "the second pair joined the first match");
            ScenarioContext.ExpectEqual(BattlegroundType.WarsongGulch, second.Type, "second match type");
            ScenarioContext.ExpectEqual(first.Bracket, second.Bracket, "brackets");
            ScenarioContext.Expect(first.InstanceId != second.InstanceId, "the matches share an instance id");
            ScenarioContext.ExpectEqual(1u, first.ClientInstanceId, "first client instance id");
            ScenarioContext.ExpectEqual(2u, second.ClientInstanceId, "second client instance id");
            BattlegroundStatus[] statuses = await context.ReadAsync(() => new[] { first.Status, second.Status });
            ScenarioContext.Expect(statuses.All(s => s is BattlegroundStatus.WaitJoin or BattlegroundStatus.InProgress), $"a match is not running: {string.Join(", ", statuses)}");
            uint[] mapInstances = await Task.WhenAll(a1.ReadAsync(p => p.Map!.InstanceId), h1.ReadAsync(p => p.Map!.InstanceId),
                a2.ReadAsync(p => p.Map!.InstanceId), h2.ReadAsync(p => p.Map!.InstanceId));
            ScenarioContext.Expect(mapInstances[0] == first.InstanceId && mapInstances[1] == first.InstanceId, "the first pair is not on the first match's map instance");
            ScenarioContext.Expect(mapInstances[2] == second.InstanceId && mapInstances[3] == second.InstanceId, "the second pair is not on the second match's map instance");
            ScenarioContext.ExpectEqual("1,2", string.Join(",", await context.ReadAsync(() => Manager(context).ClientInstanceIds(BattlegroundType.WarsongGulch, first.Bracket))), "client ids");
        });

        await context.StepAsync("SMSG_BATTLEFIELD_LIST carries both client instance ids", async () =>
        {
            ScenarioBot asker = await AllianceAsync(context, "Bgtwoasker");
            (ObjectGuid master, byte[] list) = await HelloAsync(context, asker);

            // u64 battlemaster, u32 map, u8 bracket (the asker's), u32 count, u32 ids (Battleground.cpp:203-214, BattleGroundMgr.cpp:1432-1448).
            byte[] expected = new byte[8 + 4 + 1 + 4 + (2 * 4)];
            BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(0), master.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(8), Wsg);
            expected[12] = (byte)first.Bracket;
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(13), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(17), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(21), 2);
            ScenarioContext.ExpectEqual(Convert.ToHexString(expected), Convert.ToHexString(list), "SMSG_BATTLEFIELD_LIST body");
        });
    });

    /// <summary>
    /// Two full one-per-team matches. A bot joins for instance 1 (it waits for that match) and another for the first available; the Alliance
    /// participant of match 2 leaves, and only the first-available bot is invited into the freed slot; then the Alliance participant of
    /// match 1 leaves and the bot that asked for instance 1 gets that slot.
    /// </summary>
    [Fact]
    public Task AJoinForOneInstanceWaitsForThatMatch_AndALeaversSlotIsRefilledFromTheQueue() => RunAsync(1, 1, "bg-desired-instance", async context =>
    {
        (ScenarioBot a1, _, Battleground first) = await PairIntoMatchAsync(context, "Bgdesallya", "Bgdeshordea");
        (ScenarioBot a2, _, Battleground second) = await PairIntoMatchAsync(context, "Bgdesallyb", "Bgdeshordeb");
        ScenarioBot wantsFirst = await context.StepAsync("login the instance-1 joiner", () => AllianceAsync(context, "Bgdeswantone"));
        ScenarioBot anyMatch = await context.StepAsync("login the first-available joiner", () => AllianceAsync(context, "Bgdesanyone"));

        BattlefieldStatusView queued = await QueueAsync(context, wantsFirst, instanceId: first.ClientInstanceId);
        await context.StepAsync("the join kept the asked-for instance", async () =>
        {
            ScenarioContext.ExpectEqual(first.ClientInstanceId, queued.ClientInstanceId, "queued status names the asked-for instance");
            uint desired = await context.ReadAsync(() => Manager(context).QueuedGroupOf(wantsFirst.Guid, BattlegroundType.WarsongGulch)?.DesiredInstanceId ?? 0);
            ScenarioContext.ExpectEqual(first.ClientInstanceId, desired, "queued group's desired instance");
        });
        await QueueAsync(context, anyMatch);
        await context.StepAsync("both matches are full, so both joiners wait", async () =>
        {
            await context.IdleAsync(TimeSpan.FromSeconds(2));   // the scheduled queue runs
            ScenarioContext.ExpectEqual(0u, await InvitedToAsync(context, wantsFirst), "instance-1 joiner invited");
            ScenarioContext.ExpectEqual(0u, await InvitedToAsync(context, anyMatch), "first-available joiner invited");
        });

        await context.StepAsync("match 2's Alliance player leaves; the first-available joiner takes the slot", async () =>
        {
            long mark = anyMatch.Mark();
            ScenarioContext.Expect(await a2.LeaveBattlefieldAsync(Wsg), "leave refused");
            BattlefieldStatusView invite = await InvitationAsync(anyMatch, mark);
            ScenarioContext.ExpectEqual(second.ClientInstanceId, invite.ClientInstanceId, "invitation's client instance");
            ScenarioContext.ExpectEqual(second.InstanceId, await InvitedToAsync(context, anyMatch), "first-available joiner's match");
            ScenarioContext.ExpectEqual(0u, await InvitedToAsync(context, wantsFirst), "the instance-1 joiner was offered match 2");
        });

        await context.StepAsync("match 1's Alliance player leaves; the instance-1 joiner takes the slot", async () =>
        {
            long mark = wantsFirst.Mark();
            ScenarioContext.Expect(await a1.LeaveBattlefieldAsync(Wsg), "leave refused");
            BattlefieldStatusView invite = await InvitationAsync(wantsFirst, mark);
            ScenarioContext.ExpectEqual(first.ClientInstanceId, invite.ClientInstanceId, "invitation's client instance");
            ScenarioContext.ExpectEqual(first.InstanceId, await InvitedToAsync(context, wantsFirst), "instance-1 joiner's match");
        });

        Battleground joined = await PortInAsync(context, wantsFirst);
        ScenarioContext.Expect(ReferenceEquals(joined, first), "the instance-1 joiner did not enter match 1");
    });

    /// <summary>
    /// A one-to-two-per-team template: one player per side creates the match (preparation, gates closed); a third player queued during the
    /// preparation is invited into it, a fourth queued once the gates are open is invited into it too, and no second match is made.
    /// </summary>
    [Fact]
    public Task AMatchStartsAtTheMinimum_AndIsFilledToTheMaximumDuringPreparationAndWhileRunning() => RunAsync(1, 2, "bg-fill", async context =>
    {
        ScenarioBot a1 = await context.StepAsync("login a1", () => AllianceAsync(context, "Bgfillallya"));
        ScenarioBot h1 = await context.StepAsync("login h1", () => HordeAsync(context, "Bgfillhordea"));
        await QueueAsync(context, a1);
        await context.StepAsync("one side alone makes no match", async () =>
        {
            await context.IdleAsync(TimeSpan.FromSeconds(2));   // the scheduled queue runs
            ScenarioContext.ExpectEqual(0u, await InvitedToAsync(context, a1), "a1 invited without a Horde player");
            ScenarioContext.ExpectEqual(0, await context.ReadAsync(() => Manager(context).ClientInstanceIds(BattlegroundType.WarsongGulch, 0).Count), "matches");
        });

        await QueueAsync(context, h1);
        await InvitationAsync(a1);
        await InvitationAsync(h1);
        Battleground match = await PortInAsync(context, a1);
        ScenarioContext.ExpectEqual(BattlegroundStatus.WaitJoin, await context.ReadAsync(() => match.Status), "status after the minimum met");

        ScenarioBot a2 = await context.StepAsync("login a2", () => AllianceAsync(context, "Bgfillallyb"));
        await QueueAsync(context, a2);
        await context.StepAsync("a player queued during the preparation is invited into the match", async () =>
        {
            BattlefieldStatusView invite = await InvitationAsync(a2);
            ScenarioContext.ExpectEqual(match.ClientInstanceId, invite.ClientInstanceId, "a2's invitation");
            ScenarioContext.ExpectEqual(match.InstanceId, await InvitedToAsync(context, a2), "a2's match");
            ScenarioContext.ExpectEqual(BattlegroundStatus.WaitJoin, await context.ReadAsync(() => match.Status), "status at a2's invitation");
        });

        await PortInAsync(context, h1);
        await PortInAsync(context, a2);
        await context.StepAsync("the gates open", () => context.WaitUntilAsync("the match is in progress",
            () => match.Status == BattlegroundStatus.InProgress, TimeSpan.FromMinutes(3)));

        ScenarioBot h2 = await context.StepAsync("login h2", () => HordeAsync(context, "Bgfillhordeb"));
        await QueueAsync(context, h2);
        await context.StepAsync("a player queued while the match runs is invited into it", async () =>
        {
            BattlefieldStatusView invite = await InvitationAsync(h2);
            ScenarioContext.ExpectEqual(match.ClientInstanceId, invite.ClientInstanceId, "h2's invitation");
            ScenarioContext.ExpectEqual(match.InstanceId, await InvitedToAsync(context, h2), "h2's match");
        });

        Battleground joined = await PortInAsync(context, h2);
        await context.StepAsync("the match holds the maximum on both sides and no second match was made", async () =>
        {
            ScenarioContext.Expect(ReferenceEquals(joined, match), "h2 entered another match");
            (uint ally, uint horde) = await context.ReadAsync(() => (match.PlayersCountByTeam(Team.Alliance), match.PlayersCountByTeam(Team.Horde)));
            ScenarioContext.ExpectEqual(2u, ally, "Alliance players");
            ScenarioContext.ExpectEqual(2u, horde, "Horde players");
            ScenarioContext.ExpectEqual("1", string.Join(",", await context.ReadAsync(() => Manager(context).ClientInstanceIds(BattlegroundType.WarsongGulch, match.Bracket))), "client ids");
        });
    });
}
