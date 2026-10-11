using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Every guard between a banner and the Arathi Basin node click, one test each, on the real banner shapes of <see cref="ArathiBasinTestContent"/>.
/// Each test makes the guarded attempt at the neutral stables banner, checks the stables are still neutral, undoes what it set up and then claims
/// the stables with an ordinary Opening cast, so a refusal can only come from the guard under test.
/// </summary>
public sealed class ArathiBasinBannerGuardTests
{
    private sealed class Scenario(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => Name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    /// <summary>The guarded attempts.</summary>
    public enum Guard
    {
        /// <summary>CMSG_GAMEOBJ_USE on the goober banner (no Opening): refused on the match's map (MatchRuntime.RefusesDirectUse).</summary>
        DirectUse,

        /// <summary>The trigger cast by a banner object that is not on the match's map.</summary>
        OtherMap,

        /// <summary>The trigger cast by a goober that does not route (its spell is not a banner trigger).</summary>
        NonRoutingGoober,

        GameMaster,
        Mounted,
        SpiritOfRedemption,

        /// <summary>Divine Shield: school immunities that together cover every school (Unit::IsTotalImmune).</summary>
        TotalImmune,

        Dead,
        Stunned,
        Confused,
        Fleeing,
        Possessed,
        FeignDeath,

        /// <summary>The player casts the trigger spell itself: no casting object (m_casterGo).</summary>
        NoCasterObject,

        /// <summary>The banner casts its trigger spell at a creature.</summary>
        NonPlayerTarget,
    }

    public static TheoryData<Guard> Guards() => [.. Enum.GetValues<Guard>()];

    [Theory]
    [MemberData(nameof(Guards))]
    public async Task AGuardedAttempt_LeavesTheStablesNeutral_AndTheOpeningThenClaimsThem(Guard guard)
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(ArathiBasinTestContent.Register);
        ScenarioReport report = await world.RunAsync(new Scenario("ab-guard-" + guard, async context =>
        {
            (ScenarioBot ally, _, Battleground match) = await context.EnterMatchAsync(BattlegroundType.ArathiBasin, "Scnabgally", "Scnabghorde", TimeSpan.FromMinutes(3));
            var ab = (ArathiBasin)match;
            await context.PlaceAsync(ally, 529, ArathiBasinTestContent.Stables.X + 1f, ArathiBasinTestContent.Stables.Y, ArathiBasinTestContent.Stables.Z);
            await context.WaitUntilAsync("the neutral banner stands", () => Spawned(ally, ArathiBasinTestContent.NeutralBanner));

            await context.StepAsync($"{guard}: the attempt is refused and the stables stay neutral", async () =>
            {
                string? problem = await context.ReadAsync(() => Attempt(guard, context, ally, match));
                ScenarioContext.ExpectEqual<string?>(null, problem, "attempt");
                ScenarioContext.ExpectEqual(ArathiBasin.StatusNeutral, await context.ReadAsync(() => ab.NodeStatus(ArathiBasin.NodeStables)), "stables status");
            });

            await context.StepAsync("control: the Opening cast claims the stables", async () =>
            {
                SpellCastResult opened = await context.ReadAsync(() => Open(context, ally, ArathiBasinTestContent.NeutralBanner));
                ScenarioContext.ExpectEqual(SpellCastResult.CastOk, opened, "Opening cast result");
                ScenarioContext.ExpectEqual(ArathiBasin.StatusAllianceContested, await context.ReadAsync(() => ab.NodeStatus(ArathiBasin.NodeStables)), "stables status");
            });
        }), new ScenarioRunOptions { StepTimeout = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromMinutes(5) });
        Assert.True(report.Passed, report.ToString());
    }

    /// <summary>
    /// The button banners too: once the Alliance claimed the stables, a direct use of the Alliance contested banner (a button with
    /// noDamageImmune) by the Horde is refused and changes nothing; the Horde's Opening cast then contests the node.
    /// </summary>
    [Fact]
    public async Task ADirectUseOfAButtonBanner_IsRefused_AndTheOpeningThenContestsTheNode()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(ArathiBasinTestContent.Register);
        ScenarioReport report = await world.RunAsync(new Scenario("ab-guard-button", async context =>
        {
            (ScenarioBot ally, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(BattlegroundType.ArathiBasin, "Scnabbally", "Scnabbhorde", TimeSpan.FromMinutes(3));
            var ab = (ArathiBasin)match;
            await context.StepAsync("the Alliance claims the stables and the contested banner stands", async () =>
            {
                await context.PlaceAsync(ally, 529, ArathiBasinTestContent.Stables.X + 1f, ArathiBasinTestContent.Stables.Y, ArathiBasinTestContent.Stables.Z);
                await context.WaitUntilAsync("the neutral banner stands", () => Spawned(ally, ArathiBasinTestContent.NeutralBanner));
                ScenarioContext.ExpectEqual(SpellCastResult.CastOk, await context.ReadAsync(() => Open(context, ally, ArathiBasinTestContent.NeutralBanner)), "Opening");
                await context.WaitUntilAsync("the contested banner stands", () => Spawned(ally, ArathiBasinTestContent.AllianceContestedBanner));
            });

            await context.StepAsync("the Horde's direct use of the contested banner is refused", async () =>
            {
                await context.PlaceAsync(horde, 529, ArathiBasinTestContent.Stables.X - 1f, ArathiBasinTestContent.Stables.Y, ArathiBasinTestContent.Stables.Z);
                GameObjectUseResult used = await context.ReadAsync(() =>
                {
                    GameObject banner = Find(horde, ArathiBasinTestContent.AllianceContestedBanner);
                    return Objects(horde).Use(horde.RequirePlayer(), banner.Guid);
                });
                ScenarioContext.ExpectEqual(GameObjectUseResult.Locked, used, "direct use");
                ScenarioContext.ExpectEqual(ArathiBasin.StatusAllianceContested, await context.ReadAsync(() => ab.NodeStatus(ArathiBasin.NodeStables)), "stables status");
            });

            await context.StepAsync("control: the Horde's Opening cast contests the stables", async () =>
            {
                ScenarioContext.ExpectEqual(SpellCastResult.CastOk, await context.ReadAsync(() => Open(context, horde, ArathiBasinTestContent.AllianceContestedBanner)), "Opening");
                ScenarioContext.ExpectEqual(ArathiBasin.StatusHordeContested, await context.ReadAsync(() => ab.NodeStatus(ArathiBasin.NodeStables)), "stables status");
            });
        }), new ScenarioRunOptions { StepTimeout = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromMinutes(5) });
        Assert.True(report.Passed, report.ToString());
    }

    /// <summary>
    /// The guarded attempt (world thread), with its state undone afterwards; null when it went as the guard expects, else what went wrong
    /// before the guard (so the test cannot pass because the attempt never reached it).
    /// </summary>
    private static string? Attempt(Guard guard, ScenarioContext context, ScenarioBot bot, Battleground match)
    {
        Player player = bot.RequirePlayer();
        GameObject banner = Find(bot, ArathiBasinTestContent.NeutralBanner);
        SpellSystem spells = context.Services.GetRequiredService<SpellFeature>().System;
        MatchRuntime runtime = context.Services.GetRequiredService<BattlegroundFeature>().FindMatch(match.InstanceId)
            ?? throw new InvalidOperationException("no match runtime");
        const uint Trigger = 23932;

        // The object's own trigger cast, as the goober's use makes it (GameObject::Use -> CastGooberSpell).
        string? CastFromBanner(GameObject source, Unit target)
            => spells.CastForGameObject(source, Trigger, target, unitCaster: null) == SpellCastResult.CastOk ? null : "the trigger cast was refused";

        // A state the player is in around the trigger.
        string? With(Action set, Action undo)
        {
            set();
            try
            {
                runtime.OnBannerTriggered(player, banner);
                return null;
            }
            finally
            {
                undo();
            }
        }

        string? WithAura(uint spellId)
        {
            if (spells.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true) != SpellCastResult.CastOk || !spells.HasAura(player, spellId))
            {
                return $"aura {spellId} was not applied";
            }

            return With(() => { }, () => spells.RemoveAuras(player, spellId));
        }

        string? WithFlag(UnitFlags flag) => With(() => player.UnitFlags |= flag, () => player.UnitFlags &= ~flag);

        switch (guard)
        {
            case Guard.DirectUse:
                GameObjectUseResult used = Objects(bot).Use(player, banner.Guid);
                return used == GameObjectUseResult.Locked && banner.UseCount == 0 ? null : $"direct use {used}, uses {banner.UseCount}";

            case Guard.OtherMap:
                // The same template and spawn, but an object of no map (another instance's copy is not this match's).
                var stray = new GameObject(0x00FF_FFF0, banner.Template, banner.Spawn);
                stray.SetPosition(banner.X, banner.Y, banner.Z, banner.Orientation);
                return CastFromBanner(stray, player);

            case Guard.NonRoutingGoober:
            {
                GameObjectTemplate real = banner.Template;
                uint[] data = [.. real.Data];
                data[GameObjectMapSystem.GooberSpellData] = 0;
                banner.ReplaceTemplate(real with { Data = data });
                try
                {
                    return CastFromBanner(banner, player);
                }
                finally
                {
                    banner.ReplaceTemplate(real);
                }
            }

            case Guard.GameMaster:
                return With(() => player.Flags |= PlayerFlags.Gm, () => player.Flags &= ~PlayerFlags.Gm);

            case Guard.Mounted:
                return With(() => player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 2410), () => player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0));

            case Guard.SpiritOfRedemption:
                return WithAura(BattlegroundConstants.SpellSpiritOfRedemption);

            case Guard.TotalImmune:
                return WithAura(ArathiBasinTestContent.DivineShield);

            case Guard.Dead:
            {
                uint health = player.Health;
                return With(() => player.Health = 0, () => player.Health = health);
            }

            case Guard.Stunned:
                return WithFlag(UnitFlags.Stunned);

            case Guard.Confused:
                return WithFlag(UnitFlags.Confused);

            case Guard.Fleeing:
                return WithFlag(UnitFlags.Fleeing);

            case Guard.Possessed:
                return WithFlag(UnitFlags.Possessed);

            case Guard.FeignDeath:
                return WithAura(ArathiBasinTestContent.FeignDeath);

            case Guard.NoCasterObject:
                return spells.CastSpell(player, Trigger, SpellCastTargets.ForUnit(player.Guid), triggered: true) == SpellCastResult.CastOk
                    ? null : "the player's own trigger cast was refused";

            case Guard.NonPlayerTarget:
            {
                // The match map has no creature spawns in this content: attach its creature system as a GM's .npc add would.
                CreatureMapSystem creatures = context.Services.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(player.Map!);
                Creature creature = creatures.SpawnTemporary(new CreatureTemplate
                {
                    Entry = 991_777, Name = "Bystander", Faction = 35, DisplayIds = [49], MinLevel = 60, MaxLevel = 60, MinLevelHealth = 100, MaxLevelHealth = 100,
                }, banner.X + 2f, banner.Y, banner.Z, 0f);
                return CastFromBanner(banner, creature);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(guard), guard, null);
        }
    }

    /// <summary>Partial immunity (one school immunity without the physical school) is not total: the banner still clicks.</summary>
    [Fact]
    public async Task APartialSchoolImmunity_DoesNotStopTheClaim()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(ArathiBasinTestContent.Register);
        ScenarioReport report = await world.RunAsync(new Scenario("ab-guard-partial-immunity", async context =>
        {
            (ScenarioBot ally, _, Battleground match) = await context.EnterMatchAsync(BattlegroundType.ArathiBasin, "Scnabpally", "Scnabphorde", TimeSpan.FromMinutes(3));
            var ab = (ArathiBasin)match;
            await context.PlaceAsync(ally, 529, ArathiBasinTestContent.Stables.X + 1f, ArathiBasinTestContent.Stables.Y, ArathiBasinTestContent.Stables.Z);
            await context.WaitUntilAsync("the neutral banner stands", () => Spawned(ally, ArathiBasinTestContent.NeutralBanner));
            await context.StepAsync("immune to every school but physical, the bot claims the stables", async () =>
            {
                SpellSystem spells = context.Services.GetRequiredService<SpellFeature>().System;
                ScenarioContext.ExpectEqual(SpellCastResult.CastOk, await context.ReadAsync(()
                    => spells.CastSpell(ally.RequirePlayer(), ArathiBasinTestContent.PartialImmunity, SpellCastTargets.ForSelf(), triggered: true)), "immunity");
                ScenarioContext.ExpectEqual(SpellCastResult.CastOk, await context.ReadAsync(() => Open(context, ally, ArathiBasinTestContent.NeutralBanner)), "Opening");
                ScenarioContext.ExpectEqual(ArathiBasin.StatusAllianceContested, await context.ReadAsync(() => ab.NodeStatus(ArathiBasin.NodeStables)), "stables status");
            });
        }), new ScenarioRunOptions { StepTimeout = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromMinutes(5) });
        Assert.True(report.Passed, report.ToString());
    }

    private static GameObjectMapSystem Objects(ScenarioBot bot) => bot.RequirePlayer().Map!.FindUpdater<GameObjectMapSystem>()!;

    private static bool Spawned(ScenarioBot bot, uint spawnGuid) => Objects(bot).GameObjects.Any(g => g.Spawn?.Guid == spawnGuid && g.IsSpawned);

    private static GameObject Find(ScenarioBot bot, uint spawnGuid) => Objects(bot).GameObjects.Single(g => g.Spawn?.Guid == spawnGuid && g.IsSpawned);

    private static SpellCastResult Open(ScenarioContext context, ScenarioBot bot, uint spawnGuid)
        => context.Services.GetRequiredService<SpellFeature>().System.CastSpell(bot.RequirePlayer(), ArathiBasinTestContent.OpeningSpell,
            new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = Find(bot, spawnGuid).Guid }, triggered: true);
}
