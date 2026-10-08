using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>
/// Dungeon actions for the scenario harness (kept out of the shared harness files): bring a bot up to an entrance's level, walk it
/// into an area trigger the way a client reports it (CMSG_AREATRIGGER) and wait for the far teleport it starts.
/// </summary>
public static class ScenarioDungeons
{
    /// <summary>Raise a bot to <paramref name="level"/> through the ordinary level-up (vmangos Player::GiveLevel); a higher bot is left alone. Returns its level.</summary>
    public static Task<uint> RaiseLevelAsync(this ScenarioContext context, ScenarioBot bot, byte level)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(bot);
        ProgressionFeature progression = context.Services.GetRequiredService<ProgressionFeature>();
        return context.ReadAsync(() =>
        {
            Player player = bot.RequirePlayer();
            if (player.Level < level) progression.Progression.GiveLevel(player, level);
            return (uint)player.Level;
        });
    }

    /// <summary>
    /// Stand <paramref name="bot"/> in area trigger <paramref name="triggerId"/>, send CMSG_AREATRIGGER and wait until the teleport the
    /// trigger starts has landed on <paramref name="targetMap"/> (the bot acknowledges it like a client).
    /// </summary>
    public static Task TakeAreaTriggerAsync(this ScenarioContext context, ScenarioBot bot, uint triggerId, uint targetMap)
        => context.StepAsync($"{bot.Name} takes area trigger {triggerId} to map {targetMap}", async () =>
        {
            var trigger = context.FindAreaTrigger(triggerId) ?? throw new ScenarioAssertionException($"no area trigger {triggerId}");
            await context.PlaceAsync(bot, trigger.MapId, trigger.X, trigger.Y, trigger.Z).ConfigureAwait(false);
            ScenarioContext.Expect(await bot.AreaTriggerAsync(triggerId).ConfigureAwait(false), "area trigger refused");
            TeleportService teleports = context.Services.GetRequiredService<TeleportFeature>().Teleports;
            await context.WaitUntilAsync($"{bot.Name} is on map {targetMap}", () => bot.Session!.Player is { IsInWorld: true, Map: { } map } player
                && map.MapId == targetMap && teleports.StageOf(player) is null).ConfigureAwait(false);
        });
}

/// <summary>
/// Two bots group up and take a dungeon together (The Deadmines, map 36): the leader walks into the entrance trigger 78 and lands in a
/// new instance that the group is bound to (vmangos Map::CanEnter / DungeonMap::Add, <c>BindToInstance</c> for the group); the member
/// follows through the same trigger and lands in the same instance; party chat works inside. Where the bots may stay on the dungeon map
/// (<c>World:Playerbots:AllowedMaps</c> lists it), the member also logs out inside and logs back in, into the same instance. Both leave
/// through the exit trigger 119 and the group is disbanded. If a step fails after the entrance, both bots are still brought back to
/// where they stood and the group is disbanded (<c>finally</c>), so the shared scenario bots are never left saved in the dungeon. Needs the Deadmines map and its two area triggers with their teleports
/// (<c>map_template</c>, <c>areatrigger_template</c>, <c>areatrigger_teleport</c>); the bots are raised to level 10, the entrance's level.
/// </summary>
public sealed class DungeonEntryScenario : IPlayerbotScenario
{
    public const uint Deadmines = 36;
    public const uint EntranceTrigger = 78;
    public const uint ExitTrigger = 119;

    public string Name => "dungeon";

    public string Description => "two bots group, enter The Deadmines through its entrance into one instance, and leave through the exit";

    public async Task RunAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        await context.StepAsync("the Deadmines content is there", () => context.ReadAsync(() =>
        {
            WorldMaps maps = WorldMaps.Of(context.World);
            ScenarioContext.Expect(maps.Registry.Find(Deadmines) is { IsDungeon: true }, $"map {Deadmines} is not a known dungeon (map_template)");
            foreach (uint trigger in new[] { EntranceTrigger, ExitTrigger })
            {
                ScenarioContext.Expect(maps.FindAreaTrigger(trigger) is not null, $"no areatrigger_template {trigger}");
                ScenarioContext.Expect(maps.FindAreaTriggerTeleport(trigger) is not null, $"no areatrigger_teleport {trigger}");
            }

            ScenarioContext.ExpectEqual(Deadmines, maps.FindAreaTriggerTeleport(EntranceTrigger)!.TargetMap, "entrance target map");
            return true;
        })).ConfigureAwait(false);

        (ScenarioBot leader, ScenarioBot member) = await PlayerbotScenarioCatalog.PairAsync(context).ConfigureAwait(false);
        // Where the bots stood before the entrance (an allowed map: they logged in there). A failed step inside must not leave them in
        // the dungeon: under the default AllowedMaps a bot saved there is refused at its next login, and every pair scenario uses these two.
        ScenarioBot[] bots = [leader, member];
        Origin[] origins = await context.ReadAsync(() => bots.Select(bot => bot.RequirePlayer())
            .Select(player => new Origin(player.MapId, player.X, player.Y, player.Z, player.Orientation)).ToArray()).ConfigureAwait(false);
        try
        {
            await ScenarioSteps.LeaveAnyGroupAsync(context, leader, member).ConfigureAwait(false);
            await ScenarioSteps.FormGroupAsync(context, leader, member).ConfigureAwait(false);
            Group group = await context.ReadAsync(() =>
                context.Services.GetRequiredService<Social.SocialFeature>().Context.Groups.GetGroup(leader.Guid)
                ?? throw new ScenarioAssertionException("the leader has no group")).ConfigureAwait(false);
            byte level = await context.ReadAsync(() => WorldMaps.Of(context.World).FindAreaTriggerTeleport(EntranceTrigger)!.RequiredLevel).ConfigureAwait(false);
            await context.StepAsync($"both bots reach level {level}", async () =>
            {
                ScenarioContext.Expect(await context.RaiseLevelAsync(leader, level).ConfigureAwait(false) >= level, $"{leader.Name} is below {level}");
                ScenarioContext.Expect(await context.RaiseLevelAsync(member, level).ConfigureAwait(false) >= level, $"{member.Name} is below {level}");
            }).ConfigureAwait(false);

            InstanceManager instances = context.Services.GetRequiredService<InstanceFeature>().Instances;
            await context.TakeAreaTriggerAsync(leader, EntranceTrigger, Deadmines).ConfigureAwait(false);
            uint instanceId = await context.StepAsync("the leader is in a new instance the group is bound to", async () =>
            {
                uint id = await leader.ReadAsync(p => p.Map!.InstanceId).ConfigureAwait(false);
                ScenarioContext.Expect(id != 0, "the dungeon map is not an instance");
                ScenarioContext.Expect(await context.ReadAsync(() => instances.GetGroupBind(group, Deadmines) is { Permanent: false } bind
                    && bind.Save.InstanceId == id).ConfigureAwait(false), "the group is not bound to the leader's instance");
                return id;
            }).ConfigureAwait(false);

            await context.TakeAreaTriggerAsync(member, EntranceTrigger, Deadmines).ConfigureAwait(false);
            await context.StepAsync("the member lands in the leader's instance", async () =>
                ScenarioContext.ExpectEqual(instanceId, await member.ReadAsync(p => p.Map!.InstanceId).ConfigureAwait(false), "member's instance")).ConfigureAwait(false);

            await context.StepAsync("party chat reaches the member inside", async () =>
            {
                long mark = member.Mark();
                ScenarioContext.Expect(await leader.PartyAsync("scenario dungeon").ConfigureAwait(false), "party chat refused");
                ChatMessageView heard = await member.WaitForPacketAsync(WorldOpcode.SmsgMessagechat, ScenarioDecoders.ChatMessage,
                    m => m.Type == ChatType.Party && m.Message == "scenario dungeon", mark).ConfigureAwait(false);
                ScenarioContext.ExpectEqual(leader.Guid.Value, heard.Sender, "party chat sender");
            }).ConfigureAwait(false);

            PlayerbotOptions options = context.Services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new PlayerbotOptions();
            if (options.AllowedMaps.Contains(Deadmines))
            {
                member = bots[1] = await context.StepAsync("the member logs out inside and back in, into the same instance", async () =>
                {
                    await context.Services.GetRequiredService<ManagedPlayerbotFeature>().StopAsync(member.BotId.ToString(), context.CancellationToken).ConfigureAwait(false);
                    ScenarioBot again = await context.LoginAsync(member.Name).ConfigureAwait(false);
                    (uint map, uint instance) = await again.ReadAsync(p => (p.MapId, p.Map!.InstanceId)).ConfigureAwait(false);
                    ScenarioContext.ExpectEqual(Deadmines, map, "login map");
                    ScenarioContext.ExpectEqual(instanceId, instance, "login instance");
                    return again;
                }).ConfigureAwait(false);
            }

            foreach (ScenarioBot bot in new[] { leader, member })
                await context.TakeAreaTriggerAsync(bot, ExitTrigger, 0).ConfigureAwait(false);
            await ScenarioSteps.LeaveAnyGroupAsync(context, leader, member).ConfigureAwait(false);
        }
        finally
        {
            await BringOutAsync(context, bots, origins).ConfigureAwait(false);
        }
    }

    private readonly record struct Origin(uint MapId, float X, float Y, float Z, float Orientation);

    /// <summary>
    /// After a failed run: any bot still in (or on its way into) the dungeon is teleported back to where it stood before the entrance,
    /// and the group is disbanded, so the group's bind to this instance goes with it. A passing run left both out and ungrouped
    /// already, and adds no step here. A bot that is offline (a failed relog inside, only tried when AllowedMaps lists the dungeon)
    /// cannot be moved; its next login is allowed there.
    /// </summary>
    private static async Task BringOutAsync(ScenarioContext context, ScenarioBot[] bots, Origin[] origins)
    {
        TeleportService teleports = context.Services.GetRequiredService<TeleportFeature>().Teleports;
        for (int i = 0; i < bots.Length; i++)
        {
            ScenarioBot bot = bots[i];
            Origin origin = origins[i];
            bool inside = await context.ReadAsync(() => bot.Session?.Player is { } player
                && (teleports.StageOf(player) is not null || (player.IsInWorld && player.Map?.MapId == Deadmines))).ConfigureAwait(false);
            if (!inside) continue;
            await context.CleanupAsync($"bring {bot.Name} out of the dungeon", async () =>
            {
                await context.WaitUntilAsync($"{bot.Name} is not between maps", () => bot.Session?.Player is not { } player
                    || (player.IsInWorld && teleports.StageOf(player) is null)).ConfigureAwait(false);
                if (await context.ReadAsync(() => bot.Session?.Player is { IsInWorld: true, Map.MapId: Deadmines }).ConfigureAwait(false))
                    await context.PlaceAsync(bot, origin.MapId, origin.X, origin.Y, origin.Z, origin.Orientation).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        // Online members leave (a two-member group dissolves when either does); an offline one cannot send the packet.
        ScenarioBot[] grouped = await context.ReadAsync(() => bots.Where(bot => bot.Session?.Player is { IsInWorld: true }
            && ScenarioSteps.InGroup(context, bot)).ToArray()).ConfigureAwait(false);
        if (grouped.Length == 0) return;
        await context.CleanupAsync("disband the group", async () =>
        {
            foreach (ScenarioBot bot in grouped)
            {
                if (await context.ReadAsync(() => ScenarioSteps.InGroup(context, bot)).ConfigureAwait(false))
                    await bot.LeaveGroupAsync().ConfigureAwait(false);
            }

            await context.WaitUntilAsync("no bot is grouped", () => grouped.All(bot => !ScenarioSteps.InGroup(context, bot))).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}
