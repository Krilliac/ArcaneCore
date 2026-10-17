using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>SMSG_BATTLEFIELD_STATUS as <see cref="BattlegroundPackets.BuildBattlefieldStatus"/> writes it (the empty form has map 0).</summary>
public sealed record BattlefieldStatusView(uint QueueSlot, uint MapId, byte Bracket, uint ClientInstanceId, BattlegroundStatus Status, uint Time1, uint Time2);

/// <summary>MSG_PVP_LOG_DATA from the server: ended flag, winner when ended, and the rows (guid, honorable kills, extra columns).</summary>
public sealed record PvpLogView(bool Ended, BattlegroundWinner? Winner, IReadOnlyList<(ulong Guid, uint KillingBlows, uint HonorableKills, uint Deaths, IReadOnlyList<uint> Extra)> Rows);

/// <summary>MSG_BATTLEGROUND_PLAYER_POSITIONS from the server: teammates and flag carriers (guid, x, y).</summary>
public sealed record PlayerPositionsView(IReadOnlyList<(ulong Guid, float X, float Y)> Teammates, IReadOnlyList<(ulong Guid, float X, float Y)> Carriers);

/// <summary>
/// Battleground actions and decoders for the scenario harness (docs/areas/playbots.md): the bot side of the queue (battlemaster hello and
/// join, port, leave, status poll), the scoreboard and map position requests, cancelling an aura, and the typed decoders of the replies.
/// Kept apart from <see cref="ScenarioBot"/> so the battleground lane adds no line to the shared harness files.
/// </summary>
public static class ScenarioBattlegrounds
{
    // --- client actions (layouts: BattlegroundPackets' parsers) --------------------------------------------------

    /// <summary>CMSG_BATTLEMASTER_HELLO: the battlemaster's guid.</summary>
    public static Task<bool> BattlemasterHelloAsync(this ScenarioBot bot, ObjectGuid battlemaster)
        => bot.SendAsync(WorldOpcode.CmsgBattlemasterHello, ScenarioPackets.Guid(battlemaster.Value));

    /// <summary>CMSG_BATTLEMASTER_JOIN: u64 battlemaster, u32 map, u32 instance (0: first available), u8 join as group.</summary>
    public static Task<bool> JoinBattlegroundAsync(this ScenarioBot bot, ObjectGuid battlemaster, uint mapId, uint instanceId = 0, bool asGroup = false)
    {
        var w = new PacketWriter(17);
        w.WriteUInt64(battlemaster.Value);
        w.WriteUInt32(mapId);
        w.WriteUInt32(instanceId);
        w.WriteByte(asGroup ? (byte)1 : (byte)0);
        return bot.SendAsync(WorldOpcode.CmsgBattlemasterJoin, w.ToArray());
    }

    /// <summary>CMSG_BATTLEFIELD_PORT: u32 map, u8 action (1 enter, 0 leave the queue).</summary>
    public static Task<bool> PortBattlegroundAsync(this ScenarioBot bot, uint mapId, bool enter = true)
    {
        var w = new PacketWriter(5);
        w.WriteUInt32(mapId);
        w.WriteByte(enter ? (byte)1 : (byte)0);
        return bot.SendAsync(WorldOpcode.CmsgBattlefieldPort, w.ToArray());
    }

    /// <summary>CMSG_LEAVE_BATTLEFIELD: u32 map.</summary>
    public static Task<bool> LeaveBattlefieldAsync(this ScenarioBot bot, uint mapId) => bot.SendAsync(WorldOpcode.CmsgLeaveBattlefield, ScenarioPackets.UInt32(mapId));

    /// <summary>CMSG_BATTLEFIELD_STATUS (empty).</summary>
    public static Task<bool> BattlefieldStatusAsync(this ScenarioBot bot) => bot.SendAsync(WorldOpcode.CmsgBattlefieldStatus, []);

    /// <summary>MSG_PVP_LOG_DATA (empty request).</summary>
    public static Task<bool> PvpLogDataAsync(this ScenarioBot bot) => bot.SendAsync(WorldOpcode.MsgPvpLogData, []);

    /// <summary>MSG_BATTLEGROUND_PLAYER_POSITIONS (empty request).</summary>
    public static Task<bool> PlayerPositionsAsync(this ScenarioBot bot) => bot.SendAsync(WorldOpcode.MsgBattlegroundPlayerPositions, []);

    /// <summary>CMSG_CANCEL_AURA: u32 spell (the player right-clicks a buff, e.g. a carried flag).</summary>
    public static Task<bool> CancelAuraAsync(this ScenarioBot bot, uint spellId) => bot.SendAsync(WorldOpcode.CmsgCancelAura, ScenarioPackets.UInt32(spellId));

    // --- decoders ------------------------------------------------------------------------------------------------

    public static BattlefieldStatusView BattlefieldStatus(byte[] payload)
    {
        var r = new PacketReader(payload);
        uint slot = r.ReadUInt32();
        uint map = r.ReadUInt32();
        if (map == 0)
        {
            return new BattlefieldStatusView(slot, 0, 0, 0, BattlegroundStatus.None, 0, 0);
        }

        byte bracket = r.ReadByte();
        uint instance = r.ReadUInt32();
        var status = (BattlegroundStatus)r.ReadUInt32();
        uint time1 = r.ReadUInt32();
        uint time2 = status is BattlegroundStatus.WaitQueue or BattlegroundStatus.InProgress ? r.ReadUInt32() : 0;
        return new BattlefieldStatusView(slot, map, bracket, instance, status, time1, time2);
    }

    public static PvpLogView PvpLog(byte[] payload)
    {
        var r = new PacketReader(payload);
        bool ended = r.ReadByte() != 0;
        BattlegroundWinner? winner = ended ? (BattlegroundWinner)r.ReadByte() : null;
        uint count = r.ReadUInt32();
        var rows = new List<(ulong, uint, uint, uint, IReadOnlyList<uint>)>();
        for (uint i = 0; i < count; i++)
        {
            ulong guid = r.ReadUInt64();
            _ = r.ReadUInt32();         // rank
            uint killingBlows = r.ReadUInt32();
            uint honorableKills = r.ReadUInt32();
            uint deaths = r.ReadUInt32();
            _ = r.ReadUInt32();         // bonus honor
            uint extra = r.ReadUInt32();
            var fields = new uint[extra];
            for (int f = 0; f < extra; f++)
            {
                fields[f] = r.ReadUInt32();
            }

            rows.Add((guid, killingBlows, honorableKills, deaths, fields));
        }

        return new PvpLogView(ended, winner, rows);
    }

    public static (uint Field, uint Value) WorldState(byte[] payload)
    {
        var r = new PacketReader(payload);
        return (r.ReadUInt32(), r.ReadUInt32());
    }

    public static PlayerPositionsView PlayerPositions(byte[] payload)
    {
        var r = new PacketReader(payload);
        uint count = r.ReadUInt32();
        var mates = new List<(ulong, float, float)>();
        for (uint i = 0; i < count; i++)
        {
            mates.Add((r.ReadUInt64(), r.ReadSingle(), r.ReadSingle()));
        }

        byte carriers = r.ReadByte();
        var list = new List<(ulong, float, float)>();
        for (int i = 0; i < carriers; i++)
        {
            list.Add((r.ReadUInt64(), r.ReadSingle(), r.ReadSingle()));
        }

        return new PlayerPositionsView(mates, list);
    }

    // --- world-thread reads and placement helpers (harness only) ------------------------------------------------

    /// <summary>The match the bot is bound to, read on the world thread.</summary>
    public static Task<Battleground?> BattlegroundOfAsync(this ScenarioContext context, ScenarioBot bot)
        => context.ReadAsync(() => context.Services.GetRequiredService<BattlegroundFeature>().BattlegroundOf(bot.Guid));

    /// <summary>A battlemaster spawn of <paramref name="type"/> on <paramref name="mapId"/> (the <c>battlemaster_entry</c> rows), or null.</summary>
    public static (ObjectGuid Guid, float X, float Y, float Z)? FindBattlemaster(this ScenarioContext context, uint mapId, BattlegroundType type)
    {
        BattlegroundFeature feature = context.Services.GetRequiredService<BattlegroundFeature>();
        CreatureContent content = context.Services.GetRequiredService<CreatureWorldFeature>().Content;
        foreach (CreatureSpawn spawn in content.GetSpawns(mapId))
        {
            if (feature.Battlemasters.TryGetValue(spawn.Entry, out BattlegroundType masterType) && masterType == type)
            {
                return (ObjectGuid.WithEntry(HighGuid.Unit, spawn.Entry, spawn.Guid), spawn.X, spawn.Y, spawn.Z);
            }
        }

        return null;
    }

    /// <summary>The database spawn of a game object entry on a map (the battleground flag stands), or null.</summary>
    public static (ObjectGuid Guid, float X, float Y, float Z)? FindObjectSpawn(this ScenarioContext context, uint mapId, uint entry)
    {
        GameObjectContent content = context.Services.GetRequiredService<GameObjectLootFeature>().Content;
        GameObjectSpawn? spawn = content.GetSpawns(mapId).FirstOrDefault(s => s.Entry == entry);
        return spawn is null ? null : (ObjectGuid.WithEntry(HighGuid.GameObject, spawn.Entry, spawn.Guid), spawn.X, spawn.Y, spawn.Z);
    }

    /// <summary>The centre of an area trigger, or null.</summary>
    public static (uint MapId, float X, float Y, float Z)? FindAreaTrigger(this ScenarioContext context, uint triggerId)
        => WorldMaps.Of(context.World).FindAreaTrigger(triggerId) is { } trigger ? (trigger.MapId, trigger.X, trigger.Y, trigger.Z) : null;

    /// <summary>
    /// The opening of a battleground scenario: log in an Alliance (human) and a Horde (orc) warrior, queue each at a battlemaster of
    /// <paramref name="type"/> on its home continent, take the invitation, port in, and wait for the gates to open. Returns the bots and their
    /// match.
    /// </summary>
    public static async Task<(ScenarioBot Alliance, ScenarioBot Horde, Battleground Match)> EnterMatchAsync(this ScenarioContext context,
        BattlegroundType type, string allianceBot, string hordeBot, TimeSpan startWait)
    {
        ArgumentNullException.ThrowIfNull(context);
        uint mapId = BattlegroundManager.MapOfType(type);
        ScenarioBot ally = await context.StepAsync("login " + allianceBot, () => context.LoginAsync(allianceBot, race: 1, characterClass: 1)).ConfigureAwait(false);
        ScenarioBot horde = await context.StepAsync("login " + hordeBot, () => context.LoginAsync(hordeBot, race: 2, characterClass: 1)).ConfigureAwait(false);

        foreach (ScenarioBot bot in new[] { ally, horde })
        {
            await context.StepAsync($"{bot.Name} queues at a battlemaster", async () =>
            {
                uint home = await bot.ReadAsync(p => p.MapId).ConfigureAwait(false);
                var master = context.FindBattlemaster(home, type) ?? throw new ScenarioAssertionException($"no {type} battlemaster on map {home}");
                await context.PlaceAsync(bot, home, master.X + 2f, master.Y, master.Z, MathF.PI).ConfigureAwait(false);
                long mark = bot.Mark();
                ScenarioContext.Expect(await bot.BattlemasterHelloAsync(master.Guid).ConfigureAwait(false), "hello refused");
                await bot.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldList, p => p, since: mark).ConfigureAwait(false);
                ScenarioContext.Expect(await bot.JoinBattlegroundAsync(master.Guid, mapId).ConfigureAwait(false), "join refused");
                await bot.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldStatus, BattlefieldStatus,
                    s => s.Status is BattlegroundStatus.WaitQueue or BattlegroundStatus.WaitJoin, mark).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        foreach (ScenarioBot bot in new[] { ally, horde })
        {
            await context.StepAsync($"{bot.Name} is invited and ports in", async () =>
            {
                await bot.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldStatus, BattlefieldStatus,
                    s => s.Status == BattlegroundStatus.WaitJoin && s.MapId == mapId).ConfigureAwait(false);
                ScenarioContext.Expect(await bot.PortBattlegroundAsync(mapId).ConfigureAwait(false), "port refused");
                await context.WaitUntilAsync($"{bot.Name} is in the match", () => bot.Session!.Player is { IsInWorld: true, Map: { } map } p
                    && map.MapId == mapId
                    && context.Services.GetRequiredService<BattlegroundFeature>().BattlegroundOf(p.Guid)?.PlayerTeam(p.Guid) is not null).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        Battleground match = await context.StepAsync("both bots are in the same match", async () =>
        {
            Battleground? a = await context.BattlegroundOfAsync(ally).ConfigureAwait(false);
            Battleground? h = await context.BattlegroundOfAsync(horde).ConfigureAwait(false);
            ScenarioContext.Expect(a is not null && a.Type == type && ReferenceEquals(a, h), $"the bots are not in one {type} match");
            return a!;
        }).ConfigureAwait(false);

        await context.StepAsync("the gates open after the start countdown", () => context.WaitUntilAsync("the match is in progress",
            () => match.Status == BattlegroundStatus.InProgress, startWait)).ConfigureAwait(false);
        return (ally, horde, match);
    }
}

/// <summary>
/// A short Warsong Gulch match between two managed bots (the battlegrounds lane's acceptance scenario): an Alliance and a Horde bot queue at a
/// battlemaster of their own continent, take the invitation and port in; after the two-minute start the Alliance bot takes the Horde flag and
/// captures it at its own base trigger; the Horde bot takes the Alliance flag and drops it by cancelling the flag aura (a vmangos drop trigger
/// other than death or leaving); the Alliance bot returns it; two more captures win the match (SMSG_BATTLEFIELD_WIN / _LOSE, the final
/// scoreboard), and two minutes later both bots are back where they queued. Needs the Warsong Gulch content: the map, its start and graveyard
/// safe locations, the flag stands and their <c>gameobject_battleground</c> rows, the flag room triggers 3646/3647, the dropped flag objects,
/// a battlemaster per side, and a template whose minimum is one player per team (a real realm's minimum is five).
/// </summary>
public sealed class WarsongGulchScenario : IPlayerbotScenario
{
    public const string AllianceBot = "Scnwsgally";
    public const string HordeBot = "Scnwsghorde";
    private const uint Map = 489;

    public string Name => "wsg";

    public string Description => "two bots play a short Warsong Gulch: queue, port, capture, drop and return, win, leave";

    /// <summary>How long one start, capture respawn or leave wait may take in game time (the start alone is two minutes).</summary>
    public TimeSpan LongWait { get; init; } = TimeSpan.FromMinutes(3);

    public async Task RunAsync(ScenarioContext context)
    {
        (ScenarioBot ally, ScenarioBot horde, WarsongGulch wsg) = await EnterMatchAsync(context, LongWait).ConfigureAwait(false);
        BattlegroundFeature feature = context.Services.GetRequiredService<BattlegroundFeature>();
        (uint allyHomeMap, uint hordeHomeMap) = await context.ReadAsync(() => (
            feature.EntryPointOf(ally.Guid)?.MapId ?? throw new ScenarioAssertionException($"{AllianceBot} has no entry point"),
            feature.EntryPointOf(horde.Guid)?.MapId ?? throw new ScenarioAssertionException($"{HordeBot} has no entry point"))).ConfigureAwait(false);

        await Capture(context, ally, wsg, expectedScore: 1).ConfigureAwait(false);

        await context.StepAsync("the flags respawn", () => context.WaitUntilAsync("both flags are home",
            () => wsg.FlagState(Team.Alliance) == WsgFlagState.OnBase && wsg.FlagState(Team.Horde) == WsgFlagState.OnBase
                && wsg.IsActiveEvent(WarsongGulch.EventFlagAlliance, 0), LongWait)).ConfigureAwait(false);

        await context.StepAsync($"{HordeBot} takes the Alliance flag", async () =>
        {
            var stand = context.FindObjectSpawn(Map, WarsongGulch.AllianceFlagBaseEntry) ?? throw new ScenarioAssertionException("no Alliance flag stand");
            await context.PlaceAsync(horde, Map, stand.X + 1f, stand.Y, stand.Z).ConfigureAwait(false);
            ScenarioContext.Expect(await horde.UseGameObjectAsync(stand.Guid).ConfigureAwait(false), "use refused");
            await context.WaitUntilAsync("the Horde bot carries the Alliance flag", () => wsg.FlagPicker(Team.Alliance) == horde.Guid).ConfigureAwait(false);
            long mark = horde.Mark();
            await horde.PlayerPositionsAsync().ConfigureAwait(false);
            PlayerPositionsView view = await horde.WaitForPacketAsync(WorldOpcode.MsgBattlegroundPlayerPositions, ScenarioBattlegrounds.PlayerPositions, since: mark).ConfigureAwait(false);
            ScenarioContext.Expect(view.Carriers.Any(c => c.Guid == horde.Guid.Value), "the Horde map shows its own carrier (vmangos GetAllianceFlagPickerGuid)");
        }).ConfigureAwait(false);

        await context.StepAsync($"{HordeBot} drops it by cancelling the flag aura", async () =>
        {
            ScenarioContext.Expect(await horde.CancelAuraAsync(WarsongGulch.SpellSilverwingFlag).ConfigureAwait(false), "cancel refused");
            await context.WaitUntilAsync("the Alliance flag is on the ground", () => wsg.FlagState(Team.Alliance) == WsgFlagState.OnGround
                && !wsg.DroppedFlagGuid(Team.Alliance).IsEmpty).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await context.StepAsync($"{AllianceBot} returns the dropped flag", async () =>
        {
            (float x, float y, float z) = await horde.ReadAsync(p => (p.X, p.Y, p.Z)).ConfigureAwait(false);
            await context.PlaceAsync(ally, Map, x + 1f, y, z).ConfigureAwait(false);
            ScenarioContext.Expect(await ally.UseGameObjectAsync(wsg.DroppedFlagGuid(Team.Alliance)).ConfigureAwait(false), "use refused");
            await context.WaitUntilAsync("the Alliance flag is back", () => wsg.FlagState(Team.Alliance) == WsgFlagState.OnBase).ConfigureAwait(false);
            ScenarioContext.ExpectEqual(1u, await context.ReadAsync(() => ((WsgScore)wsg.ScoreOf(ally.Guid)!).FlagReturns).ConfigureAwait(false), $"{AllianceBot} flag returns");
        }).ConfigureAwait(false);

        await Capture(context, ally, wsg, expectedScore: 2).ConfigureAwait(false);
        await context.StepAsync("the flags respawn again", () => context.WaitUntilAsync("both flags are home",
            () => wsg.FlagState(Team.Horde) == WsgFlagState.OnBase && wsg.IsActiveEvent(WarsongGulch.EventFlagHorde, 0), LongWait)).ConfigureAwait(false);

        long endMark = ally.Mark();
        await Capture(context, ally, wsg, expectedScore: 3).ConfigureAwait(false);

        await context.StepAsync("the Alliance wins", async () =>
        {
            ScenarioContext.ExpectEqual(BattlegroundStatus.WaitLeave, await context.ReadAsync(() => wsg.Status).ConfigureAwait(false), "match status");
            ScenarioContext.ExpectEqual(BattlegroundWinner.Alliance, await context.ReadAsync(() => wsg.Winner).ConfigureAwait(false), "winner");
            await ally.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldWin, p => p, since: endMark).ConfigureAwait(false);
            await horde.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldLose, p => p, since: endMark).ConfigureAwait(false);
            PvpLogView log = await ally.WaitForPacketAsync(WorldOpcode.MsgPvpLogData, ScenarioBattlegrounds.PvpLog, l => l.Ended, endMark).ConfigureAwait(false);
            ScenarioContext.ExpectEqual(BattlegroundWinner.Alliance, log.Winner, "scoreboard winner");
            var row = log.Rows.Single(r => r.Guid == ally.Guid.Value);
            ScenarioContext.ExpectEqual(3u, row.Extra[0], "scoreboard flag captures");
            ScenarioContext.ExpectEqual(1u, row.Extra[1], "scoreboard flag returns");
        }).ConfigureAwait(false);

        await context.StepAsync("two minutes later both bots are back where they queued", () => context.WaitUntilAsync("both bots left the battleground",
            () => ally.Session!.Player is { IsInWorld: true } a && a.MapId == allyHomeMap
                && horde.Session!.Player is { IsInWorld: true } h && h.MapId == hordeHomeMap
                && feature.BattlegroundOf(a.Guid) is null, LongWait)).ConfigureAwait(false);
    }

    /// <summary>The opening of every Warsong Gulch scenario (<see cref="ScenarioBattlegrounds.EnterMatchAsync"/> for Warsong Gulch).</summary>
    public static async Task<(ScenarioBot Alliance, ScenarioBot Horde, WarsongGulch Match)> EnterMatchAsync(ScenarioContext context, TimeSpan startWait)
    {
        (ScenarioBot ally, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(BattlegroundType.WarsongGulch, AllianceBot, HordeBot, startWait).ConfigureAwait(false);
        return (ally, horde, (WarsongGulch)match);
    }

    /// <summary>The Alliance bot takes the Horde flag from its stand and captures it at the Alliance flag room trigger (3646).</summary>
    private static Task Capture(ScenarioContext context, ScenarioBot ally, WarsongGulch wsg, int expectedScore)
        => context.StepAsync($"{AllianceBot} captures the Horde flag ({expectedScore})", async () =>
        {
            var stand = context.FindObjectSpawn(Map, WarsongGulch.HordeFlagBaseEntry) ?? throw new ScenarioAssertionException("no Horde flag stand");
            await context.PlaceAsync(ally, Map, stand.X + 1f, stand.Y, stand.Z).ConfigureAwait(false);
            ScenarioContext.Expect(await ally.UseGameObjectAsync(stand.Guid).ConfigureAwait(false), "use refused");
            await context.WaitUntilAsync("the Alliance bot carries the Horde flag", () => wsg.FlagPicker(Team.Horde) == ally.Guid).ConfigureAwait(false);
            var trigger = context.FindAreaTrigger(WarsongGulch.AreaTriggerAllianceFlagSpawn) ?? throw new ScenarioAssertionException("no Alliance flag room trigger");
            await context.PlaceAsync(ally, trigger.MapId, trigger.X, trigger.Y, trigger.Z).ConfigureAwait(false);
            long mark = ally.Mark();
            ScenarioContext.Expect(await ally.AreaTriggerAsync(WarsongGulch.AreaTriggerAllianceFlagSpawn).ConfigureAwait(false), "trigger refused");
            await context.WaitUntilAsync("the capture counts", () => wsg.TeamScore(Team.Alliance) == expectedScore).ConfigureAwait(false);
            (uint field, uint value) = await ally.WaitForPacketAsync(WorldOpcode.SmsgUpdateWorldState, ScenarioBattlegrounds.WorldState,
                s => s.Field == WarsongGulch.WorldStateFlagCapturesAlliance, mark).ConfigureAwait(false);
            ScenarioContext.ExpectEqual((uint)expectedScore, value, $"world state {field}");
        });
}
