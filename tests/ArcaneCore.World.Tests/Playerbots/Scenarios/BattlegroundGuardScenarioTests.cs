using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The vmangos guards of the battleground world hooks, played by two managed bots in a Warsong Gulch match (<see cref="WarsongGulchTestContent"/>):
/// <list type="bullet">
/// <item>a buff trap hands its buff to any living player in reach, but only a player who is in the battleground uses the buff object up
/// (GameObject.cpp:546-552: <c>if (ok->InBattleGround()) bg->HandleTriggerBuff(this)</c>);</item>
/// <item>the flag drops when its aura goes, unless the carrier is possessed (Aura::HandleAuraModEffectImmunity, SpellAuras.cpp:4069:
/// <c>!target-&gt;HasAuraType(SPELL_AURA_MOD_POSSESS)</c>), and a positive school immunity takes the flag aura away only from a carrier who is
/// not charmed (Aura::HandleAuraModSchoolImmunity, SpellAuras.cpp:4113-4121: <c>!target-&gt;IsCharmed()</c>).</item>
/// </list>
/// </summary>
public sealed class BattlegroundGuardScenarioTests
{
    private const uint Possess = 991_301;

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

    private static SpellSystem Spells(ScenarioContext context) => context.Services.GetRequiredService<SpellFeature>().System;

    private static bool BuffSpawned(ScenarioBot bot)
        => bot.RequirePlayer().Map!.FindUpdater<GameObjectMapSystem>()!.GameObjects
            .Any(g => g.Entry == BattlegroundConstants.SpeedBuffEntry && g.IsSpawned);

    private static Task MoveWithinTheMapAsync(ScenarioContext context, ScenarioBot bot, float x, float y, float z) => context.ReadAsync(() =>
    {
        bot.RequirePlayer().SetPosition(x, y, z, 0f);
        return true;
    });

    [Fact]
    public Task ABuffTrap_TouchedByAPlayerOutsideTheMatch_IsNotUsedUp() => RunAsync("wsg-buff-outsider", async context =>
    {
        (ScenarioBot ally, ScenarioBot horde, WarsongGulch wsg) = await WarsongGulchScenario.EnterMatchAsync(context, Start);
        (float x, float y, float z) = WarsongGulchTestContent.SpeedBuff;

        await context.StepAsync("the Alliance bot stays on the map but is no longer in the match", async () =>
        {
            await context.ReadAsync(() =>
            {
                wsg.RemovePlayerAtLeave(ally.Guid, teleportToEntryPoint: false, sendStatus: false);
                return true;
            });
            ScenarioContext.Expect(await context.ReadAsync(() => wsg.PlayerTeam(ally.Guid)) is null, "still a participant");
            ScenarioContext.Expect(await ally.ReadAsync(p => p.MapId) == 489, "not on the battleground map");
        });

        await context.StepAsync("it gets the buff, but the buff object stays", async () =>
        {
            await MoveWithinTheMapAsync(context, ally, x + 1f, y, z);   // the map refuses a teleport in from a player who is not bound to it
            await context.WaitUntilAsync("the speed buff is on the outsider", () => Spells(context).HasAura(ally.RequirePlayer(), WarsongGulchTestContent.SpeedSpell));
            await context.IdleAsync(TimeSpan.FromSeconds(5));
            ScenarioContext.Expect(await context.ReadAsync(() => BuffSpawned(ally)), "an outsider used the buff object up");
        });

        await context.StepAsync("a participant uses it up", async () =>
        {
            await MoveWithinTheMapAsync(context, ally, x + 40f, y, z);
            await context.PlaceAsync(horde, 489, x + 1f, y, z);
            await context.WaitUntilAsync("the speed buff is on the Horde bot", () => Spells(context).HasAura(horde.RequirePlayer(), WarsongGulchTestContent.SpeedSpell));
            await context.WaitUntilAsync("the buff object is gone", () => !BuffSpawned(horde));
        });
    });

    [Fact]
    public Task APossessedCarrier_KeepsTheFlag_ThroughAnImmunityAndTheLossOfTheFlagAura() => RunAsync("wsg-flag-possessed", async context =>
    {
        (ScenarioBot ally, ScenarioBot horde, WarsongGulch wsg) = await WarsongGulchScenario.EnterMatchAsync(context, Start);
        SpellSystem spells = Spells(context);

        await context.StepAsync("the Alliance bot carries the Horde flag", async () =>
        {
            var stand = context.FindObjectSpawn(489, WarsongGulch.HordeFlagBaseEntry) ?? throw new ScenarioAssertionException("no flag stand");
            await context.PlaceAsync(ally, 489, stand.X + 1f, stand.Y, stand.Z);
            ScenarioContext.Expect(await ally.UseGameObjectAsync(stand.Guid), "use refused");
            await context.WaitUntilAsync("the Alliance bot carries the flag", () => wsg.FlagPicker(Team.Horde) == ally.Guid);
        });

        await context.StepAsync("the Horde bot possesses the carrier", async () =>
        {
            await context.PlaceFacingAsync(horde, ally);
            SpellCastResult result = await context.ReadAsync(() =>
            {
                spells.Store = new SpellStore([.. spells.Store.All, new SpellInfo
                {
                    Id = Possess, Name = "Scenario Mind Control", School = SpellSchool.Shadow, RangeIndex = 4, Range = new SpellRange(0, 30),
                    Duration = new SpellDuration(60_000, 0, 60_000), SpellVisual = 1,
                    Effects = [new SpellEffectInfo
                    {
                        Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModPossess, TargetA = SpellImplicitTarget.UnitEnemy,
                        BasePoints = 59, BaseDice = 1, DieSides = 1,
                    }],
                }], [], []);
                return spells.CastSpell(horde.RequirePlayer(), Possess, SpellCastTargets.ForUnit(ally.Guid), triggered: true);
            });
            ScenarioContext.ExpectEqual(SpellCastResult.CastOk, result, "possess cast");
            ScenarioContext.Expect(await ally.ReadAsync(p => p.CharmerGuid == horde.Guid), "the carrier is not charmed by the Horde bot");
            ScenarioContext.Expect(await context.ReadAsync(() => wsg.FlagPicker(Team.Horde) == ally.Guid), "the possession dropped the flag");
        });

        await context.StepAsync("a positive school immunity on the charmed carrier leaves the flag aura", async () =>
        {
            await context.ReadAsync(() => spells.CastSpell(ally.RequirePlayer(), WarsongGulchTestContent.DivineShield, SpellCastTargets.ForSelf(), triggered: true));
            ScenarioContext.Expect(await context.ReadAsync(() => spells.HasAura(ally.RequirePlayer(), WarsongGulchTestContent.DivineShield)), "no Divine Shield");
            ScenarioContext.Expect(await context.ReadAsync(() => spells.HasAura(ally.RequirePlayer(), WarsongGulch.SpellWarsongFlag)), "the immunity took the flag aura");
            ScenarioContext.Expect(await context.ReadAsync(() => wsg.FlagPicker(Team.Horde) == ally.Guid), "the immunity dropped the flag");
        });

        await context.StepAsync("the flag aura going from a possessed carrier does not drop the flag", async () =>
        {
            await context.ReadAsync(() =>
            {
                spells.RemoveAuras(ally.RequirePlayer(), WarsongGulch.SpellWarsongFlag);
                return true;
            });
            await context.IdleAsync(TimeSpan.FromSeconds(2));
            ScenarioContext.Expect(await context.ReadAsync(() => wsg.FlagState(Team.Horde) != WsgFlagState.OnGround), "the flag dropped");
        });
    });
}
