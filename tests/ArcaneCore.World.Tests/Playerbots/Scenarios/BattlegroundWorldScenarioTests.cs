using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The battleground rules that span the world, played by two managed bots in a Warsong Gulch match (the synthetic content of
/// <see cref="WarsongGulchTestContent"/>): the buff traps, the flag drop on a positive invulnerability and on death, the kill credit, Waiting to
/// Resurrect and the battleground graveyard on release, the initial world states, leaving through the opcode (Deserter, entry point), a far
/// teleport out, and a logout inside the match (the next login is at the entry point).
/// </summary>
public sealed class BattlegroundWorldScenarioTests
{
    private sealed class Scenario(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    private static readonly TimeSpan Start = TimeSpan.FromMinutes(3);

    private static async Task RunAsync(string name, Func<ScenarioContext, Task> body)
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WarsongGulchTestContent.Register);
        ScenarioReport report = await world.RunAsync(new Scenario(name, body), new ScenarioRunOptions
        {
            StepTimeout = TimeSpan.FromSeconds(30),
            MaxDuration = TimeSpan.FromMinutes(5),
        });
        Assert.True(report.Passed, report.ToString());
    }

    private static Task<T> OnPlayer<T>(ScenarioContext context, ScenarioBot bot, Func<Player, T> read) => bot.ReadAsync(read);

    private static SpellSystem Spells(ScenarioContext context) => context.Services.GetRequiredService<SpellFeature>().System;

    private static async Task TakeFlagAsync(ScenarioContext context, ScenarioBot bot, WarsongGulch wsg, uint standEntry, Team flagTeam)
    {
        var stand = context.FindObjectSpawn(489, standEntry) ?? throw new ScenarioAssertionException("no flag stand");
        await context.PlaceAsync(bot, 489, stand.X + 1f, stand.Y, stand.Z);
        ScenarioContext.Expect(await bot.UseGameObjectAsync(stand.Guid), "use refused");
        await context.WaitUntilAsync($"{bot.Name} carries the flag", () => wsg.FlagPicker(flagTeam) == bot.Guid);
    }

    [Fact]
    public Task Wsg_SpeedBuff_DivineShieldDrop_OwnTeamReturn_DeathDrop_KillCredit_AndRelease() => RunAsync("wsg-rules", async context =>
    {
        (ScenarioBot ally, ScenarioBot horde, WarsongGulch wsg) = await WarsongGulchScenario.EnterMatchAsync(context, Start);

        await context.StepAsync("a speed buff is taken by the player who touches it and comes back later", async () =>
        {
            (float x, float y, float z) = WarsongGulchTestContent.SpeedBuff;
            await context.PlaceAsync(ally, 489, x + 1f, y, z);
            await context.WaitUntilAsync("the speed buff is on the Alliance bot", () => Spells(context).HasAura(ally.RequirePlayer(), WarsongGulchTestContent.SpeedSpell));
            await context.WaitUntilAsync("the buff object is gone", () => !BuffSpawned(ally));
            await context.WaitUntilAsync("the buff object respawned", () => BuffSpawned(ally), TimeSpan.FromSeconds(120));
        });

        await context.StepAsync("a positive invulnerability drops the carried flag", async () =>
        {
            await TakeFlagAsync(context, ally, wsg, WarsongGulch.HordeFlagBaseEntry, Team.Horde);
            await context.ReadAsync(() => Spells(context).CastSpell(ally.RequirePlayer(), WarsongGulchTestContent.DivineShield, SpellCastTargets.ForSelf(), triggered: true));
            await context.WaitUntilAsync("the Horde flag is on the ground", () => wsg.FlagState(Team.Horde) == WsgFlagState.OnGround);
            ScenarioContext.Expect(!await context.ReadAsync(() => Spells(context).HasAura(ally.RequirePlayer(), WarsongGulch.SpellWarsongFlag)), "the flag aura is gone");
        });

        await context.StepAsync("the Horde returns its own dropped flag", async () =>
        {
            ObjectGuid dropped = await context.ReadAsync(() => wsg.DroppedFlagGuid(Team.Horde));
            ScenarioContext.Expect(!dropped.IsEmpty, "a dropped flag object exists");
            (float x, float y, float z) = await OnPlayer(context, ally, p => (p.X, p.Y, p.Z));
            await context.PlaceAsync(horde, 489, x + 1f, y, z);
            ScenarioContext.Expect(await horde.UseGameObjectAsync(dropped), "use refused");
            await context.WaitUntilAsync("the Horde flag is back", () => wsg.FlagState(Team.Horde) == WsgFlagState.OnBase);
            ScenarioContext.ExpectEqual(1u, await context.ReadAsync(() => ((WsgScore)wsg.ScoreOf(horde.Guid)!).FlagReturns), "Horde flag returns");
        });

        await context.StepAsync("a carrier's death drops the flag and credits the killer", async () =>
        {
            await context.WaitUntilAsync("the Divine Shield wore off", () => !Spells(context).HasAura(ally.RequirePlayer(), WarsongGulchTestContent.DivineShield), TimeSpan.FromSeconds(30));
            await TakeFlagAsync(context, ally, wsg, WarsongGulch.HordeFlagBaseEntry, Team.Horde);
            await context.ReadAsync(() =>
            {
                Player victim = ally.RequirePlayer();
                victim.Map!.Combat.Kill(horde.RequirePlayer(), victim);
                return true;
            });
            await context.WaitUntilAsync("the Horde flag dropped at the death", () => wsg.FlagState(Team.Horde) == WsgFlagState.OnGround);
            BattlegroundScore allyScore = await context.ReadAsync(() => wsg.ScoreOf(ally.Guid)!);
            BattlegroundScore hordeScore = await context.ReadAsync(() => wsg.ScoreOf(horde.Guid)!);
            ScenarioContext.ExpectEqual(1u, allyScore.Deaths, "Alliance deaths");
            ScenarioContext.ExpectEqual(1u, hordeScore.KillingBlows, "Horde killing blows");
            ScenarioContext.ExpectEqual(1u, hordeScore.HonorableKills, "Horde honorable kills");
        });

        await context.StepAsync("the released spirit waits to resurrect at the match's graveyard", async () =>
        {
            ScenarioContext.Expect(await ally.SendAsync(WorldOpcode.CmsgRepopRequest, []), "release refused");
            await context.WaitUntilAsync("Waiting to Resurrect is on the ghost", () => Spells(context).HasAura(ally.RequirePlayer(), BattlegroundConstants.SpellWaitingToResurrect));
            await context.WaitUntilAsync("the ghost is at the main Alliance graveyard (771)", () => ally.RequirePlayer() is { MapId: 489 } p
                && MathF.Abs(p.X - 1423.22f) < 1f && MathF.Abs(p.Y - 1554.03f) < 1f);
        });
    });

    [Fact]
    public Task Wsg_TheEntryWorldStates_LeavingGivesDeserter_AndAFarTeleportLeavesTheMatch() => RunAsync("wsg-leave", async context =>
    {
        (ScenarioBot ally, ScenarioBot horde, WarsongGulch wsg) = await WarsongGulchScenario.EnterMatchAsync(context, Start);

        await context.StepAsync("entering the match sent its world states", () =>
        {
            var init = horde.Received(WorldOpcode.SmsgInitWorldStates, InitWorldStates).LastOrDefault(s => s.Map == 489);
            ScenarioContext.Expect(init.States is not null, "no SMSG_INIT_WORLD_STATES for map 489");
            ScenarioContext.ExpectEqual(3, init.States!.GetValueOrDefault(WarsongGulch.WorldStateFlagCapturesMax), "max captures state");
            ScenarioContext.ExpectEqual(1, init.States!.GetValueOrDefault(WarsongGulch.WorldStateFlagStateHorde), "Horde flag icon state");
            return Task.CompletedTask;
        });

        await context.StepAsync("CMSG_LEAVE_BATTLEFIELD: Deserter, then back to the battlemaster", async () =>
        {
            ScenarioContext.Expect(await ally.LeaveBattlefieldAsync(489), "leave refused");
            await context.WaitUntilAsync("the Alliance bot is back on its continent", () => ally.Session!.Player is { IsInWorld: true, MapId: 0 });
            ScenarioContext.Expect(await context.ReadAsync(() => Spells(context).HasAura(ally.RequirePlayer(), BattlegroundConstants.SpellDeserter)), "no Deserter");
            ScenarioContext.Expect(await context.BattlegroundOfAsync(ally) is null, "still bound to the match");
            ScenarioContext.Expect(await context.ReadAsync(() => wsg.PlayerTeam(ally.Guid)) is null, "still a participant");
        });

        await context.StepAsync("a far teleport out of the match leaves it", async () =>
        {
            await context.PlaceAsync(horde, 1, WarsongGulchTestContent.OrcStartX, WarsongGulchTestContent.OrcStartY, WarsongGulchTestContent.OrcStartZ);
            ScenarioContext.Expect(await context.BattlegroundOfAsync(horde) is null, "still bound to the match");
            ScenarioContext.ExpectEqual(0, await context.ReadAsync(() => wsg.PlayerCount), "players left in the match");
        });
    });

    [Fact]
    public Task Wsg_ALogoutInsideTheMatch_LogsBackInAtTheEntryPoint() => RunAsync("wsg-relog", async context =>
    {
        (ScenarioBot ally, _, WarsongGulch wsg) = await WarsongGulchScenario.EnterMatchAsync(context, Start);
        BattlegroundEntryPoint entry = await context.ReadAsync(() => context.Services.GetRequiredService<BattlegroundFeature>().EntryPointOf(ally.Guid)!.Value);

        await context.StepAsync("the Alliance bot logs out inside the match and back in", async () =>
        {
            await context.Services.GetRequiredService<ManagedPlayerbotFeature>().StopAsync(ally.BotId.ToString(), context.CancellationToken);
            ScenarioContext.ExpectEqual(0, await context.ReadAsync(() => wsg.PlayerCount - 1), "the logged out bot left the match");
            ScenarioBot again = await context.LoginAsync(WarsongGulchScenario.AllianceBot, race: 1, characterClass: 1);
            (uint map, float x, float y) = await again.ReadAsync(p => (p.MapId, p.X, p.Y));
            ScenarioContext.ExpectEqual(entry.MapId, map, "login map");
            ScenarioContext.Expect(MathF.Abs(x - entry.X) < 1f && MathF.Abs(y - entry.Y) < 1f, $"login place ({x}, {y}) is not the entry point ({entry.X}, {entry.Y})");
        });
    });

    private static bool BuffSpawned(ScenarioBot bot)
        => bot.RequirePlayer().Map!.FindUpdater<GameObjectMapSystem>()!.GameObjects
            .Any(g => g.Entry == BattlegroundConstants.SpeedBuffEntry && g.IsSpawned);

    private static (uint Map, Dictionary<uint, int>? States) InitWorldStates(byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint map = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        ushort count = reader.ReadUInt16();
        var states = new Dictionary<uint, int>();
        for (int i = 0; i < count; i++)
        {
            states[reader.ReadUInt32()] = reader.ReadInt32();
        }

        return (map, states);
    }
}
