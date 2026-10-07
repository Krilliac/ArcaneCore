using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Creature AI across sessions with scripted bots against the real world handlers: an orc walks up to a CALLS_GUARDS townsman, who
/// shouts for the guards (heard by a human bystander) while the town's guard post sends a guard that attacks the orc (vmangos
/// BasicAI::MoveInLineOfSight, GuardMgr::SummonGuard); and the bystander waves at a herald whose EventAI RECEIVE_EMOTE row greets it by
/// name through the real CMSG_TEXT_EMOTE handler (vmangos HandleTextEmoteOpcode -> CreatureAI::ReceiveEmote). The content is this
/// file's own; the area of every creature is Goldshire (87) through the <see cref="CreatureAiServices.AreaOf"/> seam, since the test
/// world has no extracted maps.
/// </summary>
public sealed class CreatureAiScenarioTests
{
    // Faction template 12 is one of vmangos GetTextId's Stormwind templates (the human "Guards! Help me!"); here it is an alliance town
    // without a reputation faction, hostile to the horde and to monsters.
    private const uint TownFaction = 12;
    private const uint OrcFaction = 2;             // InMemoryWorldStores: an orc player's faction template
    private const uint PeasantEntry = 991001;
    private const uint PeasantSpawn = 991001;
    private const uint HeraldEntry = 991002;
    private const uint HeraldSpawn = 991002;
    private const uint StormwindGuardEntry = 68;   // what Goldshire's post sends against the horde
    private const uint HeraldGreeting = 991100;    // broadcast text of the herald's greeting
    private const uint TextEmoteWave = 101;
    private const float PeasantX = StartX - 40f;

    private const string Orc = "Scnorc";
    private const string Watcher = "Scnwatch";

    [Fact]
    public async Task AnOrcNearATownsman_BringsTheGuard_AndAWaveAtTheHerald_IsAnswered()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(Register);
        await world.RunPassingAsync(new DelegateScenario("creature-guards-and-emotes", async context =>
        {
            ScenarioBot watcher = await context.StepAsync("login the human bystander", () => context.LoginAsync(Watcher, race: 1));
            ScenarioBot orc = await context.StepAsync("login the orc", () => context.LoginAsync(Orc, race: 2));
            CreatureMapSystem creatures = world.Services.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(0);

            await context.StepAsync("stand the bystander by the townsman and the orc out of his sight", async () =>
            {
                await context.PlaceAsync(watcher, 0, PeasantX, StartY + 8f, StartZ);
                await context.PlaceAsync(orc, 0, PeasantX - 30f, StartY, StartZ);
                await context.IdleAsync(TimeSpan.FromSeconds(2));
                Creature? guard = await context.ReadAsync(() => creatures.Creatures.FirstOrDefault(c => c.Template.Entry == StormwindGuardEntry));
                ScenarioContext.Expect(guard is null, "no guard was called while the orc stood 30 yd away");
            });

            long beforeWalk = orc.Mark();
            long heardFrom = watcher.Mark();
            await context.StepAsync("the orc walks up to the townsman", () => context.PlaceAsync(orc, 0, PeasantX - 10f, StartY, StartZ));

            await context.StepAsync("the guard post sends a guard that attacks the orc", async () =>
            {
                await context.WaitUntilAsync("a Stormwind City Guard fights the orc", () => creatures.Creatures.Any(c =>
                    c.Template.Entry == StormwindGuardEntry && c.Combat.Victim is Player { Name: Orc }), TimeSpan.FromSeconds(10));
                ObjectGuid guardGuid = await context.ReadAsync(() => creatures.Creatures.First(c => c.Template.Entry == StormwindGuardEntry).Guid);
                // The guard runs the 15 yd to the orc and swings: the orc's client sees the hits.
                await orc.WaitForPacketAsync(WorldOpcode.SmsgAttackerstateupdate, ScenarioDecoders.AttackerState,
                    hit => hit.Attacker == guardGuid.Value && hit.Victim == orc.Guid.Value, beforeWalk);
            });

            await context.StepAsync("the bystander heard the townsman shout for the guards", async () =>
            {
                MonsterChatView shout = await watcher.WaitForPacketAsync(WorldOpcode.SmsgMessagechat, ScenarioCreatureDecoders.MonsterChat,
                    m => m.Speaker == PeasantGuid.Value, heardFrom);
                ScenarioContext.ExpectEqual(ChatType.MonsterSay, shout.Type, "shout chat type");
                ScenarioContext.ExpectEqual("Guards! Help me!", shout.Message, "shout text");
                ScenarioContext.ExpectEqual(GuardPostTable.MaxCharges - 1, creatures.AiServices.GuardPosts.ChargesOf(87), "Goldshire post charges");
            });

            long greeted = watcher.Mark();
            await context.StepAsync("the bystander waves at the herald", async () =>
            {
                await context.PlaceAsync(watcher, 0, StartX + 3f, StartY + 3f, StartZ);
                ScenarioContext.Expect(await watcher.TextEmoteAsync(TextEmoteWave, HeraldGuid), "CMSG_TEXT_EMOTE admitted");
            });

            await context.StepAsync("the herald answers by name", async () =>
            {
                MonsterChatView greeting = await watcher.WaitForPacketAsync(WorldOpcode.SmsgMessagechat, ScenarioCreatureDecoders.MonsterChat,
                    m => m.Speaker == HeraldGuid.Value, greeted);
                ScenarioContext.ExpectEqual("Well met, Scnwatch!", greeting.Message, "greeting");
            });
        }));
    }

    private static ObjectGuid PeasantGuid => ObjectGuid.WithEntry(HighGuid.Unit, PeasantEntry, PeasantSpawn);

    private static ObjectGuid HeraldGuid => ObjectGuid.WithEntry(HighGuid.Unit, HeraldEntry, HeraldSpawn);

    private static void Register(IServiceCollection services)
    {
        services.AddSingleton<ICreatureDataStore>(new Content());
        services.AddSingleton(new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),        // human player
            new FactionTemplateRecord(OrcFaction, 76, 0, OwnMask: 5, FriendlyMask: 4, HostileMask: 10), // orc player
            new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),        // monster
            new FactionTemplateRecord(TownFaction, 990, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 12),   // the town
        ]));
        services.AddSingleton<Func<Creature, uint>>(_ => 87u); // CreatureAiServices.AreaOf: every creature stands in Goldshire
    }

    private sealed class Content : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            CreatureAiEvent wave = new()
            {
                Id = 9910021,
                CreatureId = HeraldEntry,
                EventType = 22,
                Flags = 1,
                Param1 = (int)TextEmoteWave,
                Action1 = new CreatureAiAction((byte)EventAiActionType.Text, (int)HeraldGreeting, 0, 0),
            };
            var texts = new BroadcastTextCatalog(
            [
                new BroadcastText(GuardPostTable.TextGuardHuman, "Guards! Help me!", "", 0, 7, 0, [], []),
                new BroadcastText(HeraldGreeting, "Well met, $N!", "", 0, 7, 0, [], []),
            ]);
            return Task.FromResult(new CreatureContent(
                [
                    Townsfolk(PeasantEntry, "Scenario Townsman") with { StaticFlags1 = 0x08000000 }, // CALLS_GUARDS
                    Townsfolk(HeraldEntry, "Scenario Herald") with { AIName = CreatureAiFactory.EventAIName },
                    Townsfolk(StormwindGuardEntry, "Stormwind City Guard") with { Civilian = false, ExtraFlags = 0x400, MinLevelHealth = 500, MaxLevelHealth = 500 },
                ],
                [
                    new CreatureSpawn { Guid = PeasantSpawn, Entry = PeasantEntry, MapId = 0, X = PeasantX, Y = StartY, Z = StartZ },
                    new CreatureSpawn { Guid = HeraldSpawn, Entry = HeraldEntry, MapId = 0, X = StartX + 6f, Y = StartY + 3f, Z = StartZ },
                ], [], [], [], new CreatureAiContent([wave], [], texts)));
        }

        private static CreatureTemplate Townsfolk(uint entry, string name) => new()
        {
            Entry = entry, Name = name, Faction = TownFaction, CreatureType = 7, MinLevel = 5, MaxLevel = 5, DisplayIds = [49], Civilian = true,
            MinLevelHealth = 100, MaxLevelHealth = 100, MinMeleeDamage = 1, MaxMeleeDamage = 2, MeleeBaseAttackTime = 2000, Detection = 18,
        };
    }
}
