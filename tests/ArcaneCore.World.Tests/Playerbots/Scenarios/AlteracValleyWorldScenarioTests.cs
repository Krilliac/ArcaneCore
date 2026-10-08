using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Alterac Valley kill credit through the world kill event (vmangos Unit::Kill, Unit.cpp:981 and 1272-1283: the credited player is the
/// killer's <c>GetCharmerOrOwnerPlayerOrPlayerItself</c>, then <c>BattleGround::HandleKillPlayer</c> / <c>HandleKillUnit</c>). Two managed bots
/// enter one Alterac Valley match; the Alliance bot's hunter pet kills the Horde bot (the owner gets the killing blow and the honorable kill), a
/// creature the Alliance bot charms kills the Horde captain (the Horde loses 100 reinforcements), and the pet kills the Horde general (the
/// Alliance wins the match).
/// </summary>
public sealed class AlteracValleyWorldScenarioTests
{
    private sealed class Scenario(Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => "av-kill-credit";

        public string Description => Name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    private const uint PetNumber = 990778;

    [Fact]
    public async Task Av_PetAndCharmKillingBlows_CreditTheControllingPlayer_ForThePlayerVictim_TheCaptain_AndTheGeneral()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(AlteracValleyTestContent.Register);
        SummonService pets = world.Services.GetRequiredService<PetsFeature>().Service;
        ScenarioReport report = await world.RunAsync(new Scenario(async context =>
        {
            (ScenarioBot ally, ScenarioBot horde, Battleground match) = await context.EnterMatchAsync(BattlegroundType.AlteracValley, "Scnavally", "Scnavhorde", TimeSpan.FromMinutes(3));
            var av = (AlteracValley)match;
            Player allyPlayer = await ally.ReadAsync(p => p);
            (float x, float y, float z) = AlteracValleyTestContent.Field;

            await context.StepAsync("both bots stand on the field", async () =>
            {
                await context.PlaceAsync(ally, 30, x, y, z);
                await context.PlaceAsync(horde, 30, x + 2f, y, z);
            });

            Creature pet = await context.StepAsync("the Alliance bot, made a hunter, calls its pet", () => context.ReadAsync(() =>
            {
                allyPlayer.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
                Creature? summoned = pets.RestoreCurrentPet(allyPlayer, new PersistentPetSnapshot(
                    (int)allyPlayer.Guid.Low, PetNumber, AlteracValleyTestContent.PetEntry, 1, 0, 50, 0, 0, 1, [], []));
                ScenarioContext.Expect(summoned is not null, "the pet was summoned");
                ScenarioContext.ExpectEqual(allyPlayer.Guid, summoned!.OwnerGuid, "pet owner");
                return summoned!;
            }));

            await context.StepAsync("the pet kills the Horde bot: the owner is credited with the killing blow", async () =>
            {
                await context.ReadAsync(() =>
                {
                    Player victim = horde.RequirePlayer();
                    victim.Map!.Combat.Kill(pet, victim);
                    return true;
                });
                BattlegroundScore allyScore = await context.ReadAsync(() => av.ScoreOf(ally.Guid)!);
                BattlegroundScore hordeScore = await context.ReadAsync(() => av.ScoreOf(horde.Guid)!);
                ScenarioContext.ExpectEqual(1u, hordeScore.Deaths, "Horde deaths");
                ScenarioContext.ExpectEqual(1u, allyScore.KillingBlows, "Alliance killing blows");
                ScenarioContext.ExpectEqual(1u, allyScore.HonorableKills, "Alliance honorable kills");
            });

            await context.StepAsync("a creature the Alliance bot charms kills the Horde captain: the Horde loses 100 reinforcements", async () =>
            {
                int before = await context.ReadAsync(() => av.TeamScore(Team.Horde));
                await context.ReadAsync(() =>
                {
                    Creature charmed = SpawnedCreature(allyPlayer, AlteracValleyTestContent.CharmedSpawn);
                    charmed.SetUInt64(UpdateFields.UnitFieldCharmedby, allyPlayer.Guid.Value);
                    Creature captain = SpawnedCreature(allyPlayer, AlteracValleyTestContent.CaptainSpawn);
                    captain.Map!.Combat.Kill(charmed, captain);
                    return true;
                });
                ScenarioContext.ExpectEqual(before - AlteracValley.ResourcesLostPerCaptain, await context.ReadAsync(() => av.TeamScore(Team.Horde)), "Horde reinforcements");
                ScenarioContext.Expect(await context.ReadAsync(() => av.IsActiveEvent(AlteracValley.EventCaptainDeadHorde, 0)), "the dead-captain event is not active");
            });

            await context.StepAsync("the pet kills the Horde general: the Alliance wins", async () =>
            {
                await context.ReadAsync(() =>
                {
                    Creature general = SpawnedCreature(allyPlayer, AlteracValleyTestContent.GeneralSpawn);
                    general.Map!.Combat.Kill(pet, general);
                    return true;
                });
                ScenarioContext.ExpectEqual(BattlegroundStatus.WaitLeave, await context.ReadAsync(() => av.Status), "match status");
                ScenarioContext.ExpectEqual(BattlegroundWinner.Alliance, await context.ReadAsync(() => av.Winner), "winner");
            });
        }), new ScenarioRunOptions { StepTimeout = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromMinutes(5) });
        Assert.True(report.Passed, report.ToString());
    }

    private static Creature SpawnedCreature(Player near, uint spawnGuid)
        => near.Map!.FindUpdater<CreatureMapSystem>()!.Creatures.Single(c => c.Spawn?.Guid == spawnGuid && c.IsAlive);
}

/// <summary>
/// Synthetic Alterac Valley content: map 30, the start caves 611/610, a template of one to forty players per team from level 1, the classic-db
/// battlemasters 7410 and 347, and on the field the Horde general (Drek'Thar 11946 on the event (62, 0)), the Horde captain (Galvangar 11947 on
/// the event (49, 0)) and a plain creature for the charm; plus the pet's creature template.
/// </summary>
internal static class AlteracValleyTestContent
{
    public const uint GeneralSpawn = 300001;
    public const uint CaptainSpawn = 300002;
    public const uint CharmedSpawn = 300003;
    public const uint PetEntry = 990011;
    private const uint CharmedEntry = 990012;

    public static readonly (float X, float Y, float Z) Field = (-1370.90f, -219.79f, 98.43f);

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IMapDataStore>(new Maps());
        services.AddSingleton<IGraveyardDataStore>(new Graveyards());
        services.AddSingleton<IBattlegroundContentStore>(new Battlegrounds());
        var store = new Content();
        services.AddSingleton<ICreatureDataStore>(store);
        services.AddScoped<IGameObjectDataStore>(_ => store);
        services.AddSingleton(new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),
            new FactionTemplateRecord(2, 2, 0, OwnMask: 5, FriendlyMask: 4, HostileMask: 10),
            new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),
            new FactionTemplateRecord(12, 72, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),
            new FactionTemplateRecord(29, 76, 0, OwnMask: 4, FriendlyMask: 4, HostileMask: 8),
        ]));
    }

    private sealed class Maps : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [
                new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
                new MapTemplate(30, 0, MapType.Battleground, 2597, 30, 0, -1, 0, 0, "Alterac Valley", ""),
            ],
            [], [], [], []));
    }

    private sealed class Graveyards : IGraveyardDataStore
    {
        public Task<GraveyardContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GraveyardContent(
            [
                new WorldSafeLoc(611, 30, 873.00f, -491.28f, 96.54f, 3.14f, "AV Alliance start"),
                new WorldSafeLoc(610, 30, -1437.67f, -610.09f, 51.16f, 0.64f, "AV Horde start"),
            ],
            []));
    }

    private sealed class Battlegrounds : IBattlegroundContentStore
    {
        public Task<BattlegroundContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BattlegroundContent(
            [new BattlegroundTemplateRecord(1, 1, 40, 1, 60, 611, 610, 100, 0)],
            [
                new BattlegroundEventIndex(GeneralSpawn, AlteracValley.EventBossHorde, 0),
                new BattlegroundEventIndex(CaptainSpawn, AlteracValley.EventCaptainHorde, 0),
            ],
            [],
            [new BattlemasterRecord(7410, 1), new BattlemasterRecord(347, 1)]));
    }

    private sealed class Content : ICreatureDataStore, IGameObjectDataStore
    {
        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [
                Master(7410, "Thelman Slatefist", 12),
                Master(347, "Grizzle Halfmane", 29),
                FieldCreature(11946, "Drek'Thar"),
                FieldCreature(11947, "Captain Galvangar"),
                FieldCreature(CharmedEntry, "Scenario Thrall"),
                new CreatureTemplate
                {
                    Entry = PetEntry, Name = "Scenario Wolf", Faction = 14, CreatureType = 1, MinLevel = 1, MaxLevel = 1, DisplayIds = [903],
                    MinLevelHealth = 50, MaxLevelHealth = 50, MinMeleeDamage = 1, MaxMeleeDamage = 2, MeleeBaseAttackTime = 2000,
                },
            ],
            [
                new CreatureSpawn { Guid = 993001, Entry = 7410, MapId = 0, X = ScenarioTestContent.StartX + 5f, Y = ScenarioTestContent.StartY, Z = ScenarioTestContent.StartZ },
                new CreatureSpawn { Guid = 993002, Entry = 347, MapId = 1, X = WarsongGulchTestContent.OrcStartX + 5f, Y = WarsongGulchTestContent.OrcStartY, Z = WarsongGulchTestContent.OrcStartZ },
                Spawn(GeneralSpawn, 11946, 4f),
                Spawn(CaptainSpawn, 11947, 6f),
                Spawn(CharmedSpawn, CharmedEntry, 8f),
            ], [], [], []));

        Task<GameObjectContent> IGameObjectDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new GameObjectContent([], [], [], [], []));

        private static CreatureSpawn Spawn(uint guid, uint entry, float dy) => new()
        {
            Guid = guid, Entry = entry, MapId = 30, X = AlteracValleyTestContent.Field.X, Y = AlteracValleyTestContent.Field.Y + dy, Z = AlteracValleyTestContent.Field.Z,
        };

        private static CreatureTemplate Master(uint entry, string name, uint faction) => new()
        {
            Entry = entry, Name = name, Faction = faction, NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.BattleMaster), DisplayIds = [49],
            MinLevel = 60, MaxLevel = 60, MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = Creature.ExtraFlagNoAggro,
        };

        /// <summary>A creature on the field that never starts a fight of its own.</summary>
        private static CreatureTemplate FieldCreature(uint entry, string name) => new()
        {
            Entry = entry, Name = name, Faction = 14, CreatureType = 7, MinLevel = 60, MaxLevel = 60, DisplayIds = [49],
            MinLevelHealth = 1000, MaxLevelHealth = 1000, ExtraFlags = Creature.ExtraFlagNoAggro,
        };
    }
}
