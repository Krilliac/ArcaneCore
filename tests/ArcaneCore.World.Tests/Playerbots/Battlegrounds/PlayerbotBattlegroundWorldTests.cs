using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;
using Content = ArcaneCore.World.Tests.Playerbots.Battlegrounds.BattlegroundBotTestContent;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// Autonomous managed bots in battlegrounds (<see cref="PlayerbotBattlegroundCoordinator"/>, docs/areas/playbots-battlegrounds.md), end to
/// end on the real world handlers and the manual clock: the coordinator picks free bots, walks them to a battlemaster, queues them through
/// CMSG_BATTLEMASTER_HELLO / _JOIN, takes the invitation through CMSG_BATTLEFIELD_PORT, plays the objective (a Warsong Gulch capture, an
/// Arathi Basin banner opened with spell 21651) and leaves with CMSG_LEAVE_BATTLEFIELD. Opponents that must not fight back are scripted
/// managed bots (the coordinator never takes a scripted bot); real players are socket clients. Content: <see cref="BattlegroundBotTestContent"/>.
/// </summary>
public abstract class PlayerbotBattlegroundWorldTests
{
    private protected sealed class Scenario(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    /// <summary>The start countdown is two minutes of game time; a wait for the gates gets three.</summary>
    internal static readonly TimeSpan StartWait = TimeSpan.FromMinutes(3);

    internal static async Task RunAsync(ScenarioTestWorld world, string name, Func<ScenarioContext, Task> body)
    {
        ScenarioReport report = await world.RunAsync(new Scenario(name, body), new ScenarioRunOptions
        {
            StepTimeout = TimeSpan.FromSeconds(60),
            MaxDuration = TimeSpan.FromMinutes(15),
        });
        Assert.True(report.Passed, report + Environment.NewLine + world.Trace());
    }

    internal static BattlegroundManager Manager(ScenarioContext context) => context.Services.GetRequiredService<BattlegroundFeature>().Manager;

    internal static PlayerbotBattlegroundCoordinator Coordinator(ScenarioContext context) => context.Services.GetRequiredService<PlayerbotBattlegroundCoordinator>();

    /// <summary>
    /// <see cref="ScenarioContext.WaitUntilAsync"/>, and on a timeout the facts of <paramref name="bots"/> (map, place, binding, the coordinator's
    /// view) in the failure: the scenario's own trace is taken after the run stopped every bot.
    /// </summary>
    internal static async Task WaitExplainedAsync(ScenarioContext context, string what, Func<bool> condition, TimeSpan timeout, params ScenarioBot[] bots)
    {
        try
        {
            await context.WaitUntilAsync(what, condition, timeout);
        }
        catch (ScenarioTimeoutException timedOut)
        {
            string facts = await context.ReadAsync(() => string.Join(" | ", bots.Select(b => b.Session?.Player is { } p
                ? FormattableString.Invariant($"{b.Name} map={p.MapId} inWorld={p.IsInWorld} alive={p.IsAlive} at=({p.X:F1},{p.Y:F1},{p.Z:F1}) bound={Manager(context).StateOf(p.Guid).InBattleground} queued={Manager(context).StateOf(p.Guid).InAnyQueue} {Coordinator(context).Describe(b.BotId)}")
                : $"{b.Name} offline")));
            throw new ScenarioAssertionException($"{timedOut.Message}; {facts}");
        }
    }

    /// <summary>An autonomous bot: logged in (scripted, so the run records its packets), then handed to its brain and the coordinators.</summary>
    internal static async Task<ScenarioBot> AutonomousAsync(ScenarioContext context, string name, byte race)
    {
        ScenarioBot bot = await context.LoginAsync(name, race, characterClass: 1);
        await context.AutonomousAsync(bot);
        return bot;
    }

    /// <summary>A scripted Horde opponent queues at its battlemaster for <paramref name="type"/> (it never fights; the coordinator never drives it).</summary>
    internal static async Task<ScenarioBot> ScriptedHordeQueuesAsync(ScenarioContext context, string name, BattlegroundType type, uint master)
    {
        ScenarioBot horde = await context.LoginAsync(name, race: 2, characterClass: 1);
        await context.StepAsync($"{name} queues for {type}", async () =>
        {
            ObjectGuid guid = ObjectGuid.WithEntry(HighGuid.Unit, master, master);
            System.Numerics.Vector3 at = Content.MasterAt(master);
            await context.PlaceAsync(horde, 0, at.X - 1.5f, at.Y, at.Z, 0f);
            long mark = horde.Mark();
            ScenarioContext.Expect(await horde.BattlemasterHelloAsync(guid), "hello refused");
            await horde.WaitForPacketAsync(WorldOpcode.SmsgBattlefieldList, p => p, since: mark);
            ScenarioContext.Expect(await horde.JoinBattlegroundAsync(guid, BattlegroundManager.MapOfType(type)), "join refused");
            await context.WaitUntilAsync($"{name} is queued", () => Manager(context).StateOf(horde.Guid).InAnyQueue);
        });
        return horde;
    }

    /// <summary>
    /// Flag a scripted player for PvP so the bots may attack it. A battleground's players are flagged by the zone rule (PvpAreaTracker: an
    /// unowned zone on a battleground map enforces PvP) but this fixture has no area table, so the player asks for PvP like a client does
    /// (CMSG_TOGGLE_PVP: it stays flagged, no timer). Only the tests that want a fight call it: a flagged Horde player parked out of the way would
    /// be hunted by the Alliance bots' duty plan.
    /// </summary>
    internal static Task FlagForPvpAsync(ScenarioBot bot) => bot.ReadAsync(p => { p.Map!.Combat.TogglePvp(p, true); return true; });

    /// <summary>The scripted bot takes its invitation and ports in, then is parked where it is out of everyone's way.</summary>
    internal static Task PortScriptedAsync(ScenarioContext context, ScenarioBot bot, uint map, (float X, float Y, float Z)? park)
        => context.StepAsync($"{bot.Name} ports in", async () =>
        {
            await context.WaitUntilAsync($"{bot.Name} is invited", () => Manager(context).StateOf(bot.Guid).InvitedInstance((BattlegroundQueueType)(byte)BattlegroundManager.TypeOfMap(map)) != 0, StartWait);
            ScenarioContext.Expect(await bot.PortBattlegroundAsync(map), "port refused");
            await context.WaitUntilAsync($"{bot.Name} is in the match", () => bot.Session!.Player is { IsInWorld: true } p && p.MapId == map
                && context.Services.GetRequiredService<BattlegroundFeature>().BattlegroundOf(p.Guid)?.PlayerTeam(p.Guid) is not null);
            if (park is { } at) await context.PlaceAsync(bot, map, at.X, at.Y, at.Z);
        });

    // --- a real player's party ---------------------------------------------------------------------------------------

    /// <summary>The real player <paramref name="inviter"/> puts <paramref name="bot"/> into its party (world thread): <c>.playerbot invite</c>.</summary>
    private protected static bool InviteIntoParty(ScenarioContext context, ObjectGuid inviter, ScenarioBot bot)
        => context.Services.GetRequiredService<ManagedPlayerbotFeature>().InviteToGroup(context.World.FindOnlinePlayer(inviter)!, bot.Name).Success;

    /// <summary>SMSG_SPELL_START / SMSG_SPELL_GO: packed caster item guid, packed caster guid, u32 spell.</summary>
    private protected static uint SpellIdOf(byte[] payload)
    {
        var reader = new PacketReader(payload);
        _ = reader.ReadPackedGuid();
        _ = reader.ReadPackedGuid();
        return reader.ReadUInt32();
    }

    /// <summary>
    /// A health pool no test guard can empty. The guard fight is melee with the real hit table (<see cref="SharedCombatRandom"/>) and a thin
    /// margin: the bot (60 health) walks to the guard (20 health, no melee damage in the fixture, so the combat fallback of 0 to 5 per swing
    /// every 2 s) and kills it in 28 to 40 s of game time while losing 24 to 54 health, over 20 runs of an instrumented copy of the test (it
    /// won all 20, the closest with 6 health left; the bot closed in, kept swinging and never dithered). A bot that loses the tail of that
    /// distribution dies at the start cave and the wait for the guard's death runs out its game budget at once, which is what one CI run
    /// showed; what attacked it there was not observed, and the CI trace's second dead bot (the parked Horde one) is not explained. The
    /// protection hides a stray attacker, so <see cref="ExpectOnlyTheGuardHurtTheBot"/> fails the test if anything but the guard hit the bot.
    /// </summary>
    private protected static Task FortifyAsync(ScenarioContext context, ScenarioBot bot) => context.ReadAsync(() =>
    {
        Player player = bot.RequirePlayer();
        player.MaxHealth = 5000;
        player.Health = 5000;
        return true;
    });

    /// <summary>
    /// Every damage packet the bot took since <paramref name="since"/> came from the guard: no stray hostile in the test world. Melee hits
    /// (SMSG_ATTACKERSTATEUPDATE) and spell damage (SMSG_SPELLNONMELEEDAMAGELOG) are both checked. The check cannot pass vacuously: it fails if
    /// the bot's bounded packet log (4096 packets, movement and updates included) evicted packets newer than <paramref name="since"/>, and if the
    /// guard landed no damaging hit. Returns the damage the guard's melee did and its largest single hit.
    /// </summary>
    private protected static (uint Total, uint Largest) ExpectOnlyTheGuardHurtTheBot(ScenarioBot bot, Game.Creatures.Creature guard, long since)
    {
        const int PacketLogCapacity = 4096; // the ScenarioPacketLog default the bot is built with
        IReadOnlyList<ScenarioPacket> log = bot.Log.Snapshot();
        ScenarioContext.Expect(log.Count < PacketLogCapacity || log[0].Sequence <= since,
            $"the bot's packet log evicted packets since the mark (oldest #{(log.Count > 0 ? log[0].Sequence : -1)}, mark #{since}): the attacker check would be blind");

        AttackerStateView[] melee = [.. bot.Received(WorldOpcode.SmsgAttackerstateupdate, ScenarioDecoders.AttackerState, since)
            .Where(h => h.Victim == bot.Guid.Value)];
        SpellDamageView[] spells = [.. bot.Received(WorldOpcode.SmsgSpellnonmeleedamagelog, ScenarioClassDecoders.SpellDamage, since)
            .Where(h => h.Target == bot.Guid.Value)];
        ulong[] attackers = [.. melee.Select(h => h.Attacker).Concat(spells.Select(h => h.Caster)).Distinct()];
        ScenarioContext.Expect(attackers.All(a => a == guard.Guid.Value),
            $"something other than the guard hurt the bot (attacker guids: {string.Join(", ", attackers.Select(a => a.ToString("X")))})");
        uint total = 0, largest = 0;
        foreach (AttackerStateView hit in melee)
        {
            total += hit.TotalDamage;
            largest = Math.Max(largest, hit.TotalDamage);
        }

        ScenarioContext.Expect(total > 0, "the guard never damaged the bot: the attacker check proved nothing");
        return (total, largest);
    }
}
