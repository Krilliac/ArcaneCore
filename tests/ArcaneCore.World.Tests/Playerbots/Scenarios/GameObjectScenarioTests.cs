using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Lfg;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Duel;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Game object types that span sessions, driven by scripted managed bots against the real world handlers: a party queued at a meeting
/// stone takes in a solo player (vmangos LFGQueue), and a warlock's Ritual of Summoning gathers two helpers and summons a far player who
/// accepts (GameObject::Use of a summoning ritual, Spell::EffectSummonPlayer, HandleSummonResponseOpcode).
/// </summary>
public sealed class GameObjectScenarioTests
{
    private const uint StoneEntry = 178828;
    private const uint StoneSpawn = 990030;
    private const uint StoneArea = 719;
    private const uint RitualSpell = 990698;       // Ritual of Summoning: channelled TRANS_DOOR of the Summoning Portal
    private const uint RitualEffectSpell = 990720; // Ritual of Summoning Effect: SUMMON_PLAYER

    private static ObjectGuid Stone => ObjectGuid.WithEntry(HighGuid.GameObject, StoneEntry, StoneSpawn);

    private static void Configure(IServiceCollection services)
    {
        services.AddScoped<IGameObjectDataStore>(_ => new Objects());
        SpellContent duel = DuelWorldHost.Content();
        services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(duel with
        {
            Spells =
            [
                .. duel.Spells,
                new SpellTemplateRow
                {
                    Id = RitualSpell, SpellName = "Ritual of Summoning", RangeIndex = 1, AttributesEx = 0x4, DurationIndex = 3,
                    Effect1 = 50, EffectImplicitTargetA1 = 1, EffectMiscValue1 = (int)GameObjectMapSystem.PlayerSummoningRitualEntry, EffectRadiusIndex1 = 50,
                    EffectBaseDice1 = 1, EffectDieSides1 = 1,
                },
                new SpellTemplateRow
                {
                    Id = RitualEffectSpell, SpellName = "Ritual of Summoning Effect", RangeIndex = 1,
                    Effect1 = 85, EffectImplicitTargetA1 = 25, EffectBaseDice1 = 1, EffectDieSides1 = 1,
                },
            ],
            Radii = [.. duel.Radii, new SpellRadiusRow { Id = 50, Radius = 2.0f }],
        }));
    }

    /// <summary>The scenario content's mailbox and duel flag, a meeting stone beside the start, and the warlock Summoning Portal template.</summary>
    private sealed class Objects : IGameObjectDataStore
    {
        public Task<GameObjectContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            static GameObjectTemplate Template(uint entry, GameObjectType type, string name, params (int Index, uint Value)[] data)
            {
                var values = new uint[GameObjectTemplate.DataCount];
                foreach ((int index, uint value) in data)
                {
                    values[index] = value;
                }

                return new GameObjectTemplate { Entry = entry, Type = (uint)type, DisplayId = 1, Name = name, Data = values };
            }

            return Task.FromResult(new GameObjectContent(
                [
                    Template(MailboxEntry, GameObjectType.Mailbox, "Mailbox"),
                    Template(DuelWorldHost.FlagEntry, GameObjectType.DuelArbiter, "Duel Flag"),
                    Template(StoneEntry, GameObjectType.MeetingStone, "Meeting Stone", (0, 24), (1, 32), (2, StoneArea)),
                    // classic-db 36727: three participants, the summon spell, castersGrouped.
                    Template(GameObjectMapSystem.PlayerSummoningRitualEntry, GameObjectType.SummoningRitual, "Summoning Portal",
                        (0, 3), (1, RitualEffectSpell), (6, 1)),
                ],
                [
                    new GameObjectSpawn { Guid = MailboxSpawn, Entry = MailboxEntry, MapId = 0, X = StartX, Y = StartY - 2f, Z = StartZ },
                    new GameObjectSpawn { Guid = StoneSpawn, Entry = StoneEntry, MapId = 0, X = StartX - 2f, Y = StartY, Z = StartZ },
                ],
                [], [], []));
        }
    }

    [Fact]
    public async Task MeetingStone_AQueuedPartyTakesInASoloPlayer_WhoLaterLeaves()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(Configure);
        await world.RunPassingAsync(new DelegateScenario("meeting-stone", async context =>
        {
            ScenarioBot a = await context.StepAsync("login A", () => context.LoginAsync(PlayerbotScenarioCatalog.BotA));
            ScenarioBot b = await context.StepAsync("login B", () => context.LoginAsync(PlayerbotScenarioCatalog.BotB));
            ScenarioBot c = await context.StepAsync("login C", () => context.LoginAsync("Scngamma"));
            foreach (ScenarioBot bot in new[] { a, b, c })
            {
                await context.StepAsync($"place {bot.Name} at the stone", () => context.PlaceAsync(bot, 0, StartX, StartY, StartZ));
            }

            await ScenarioSteps.LeaveAnyGroupAsync(context, a, b, c);
            // The scenario content creates human warriors only: A tanks, B deals damage; C (a warrior too) fills a damage slot.
            await ScenarioSteps.FormGroupAsync(context, a, b);

            await context.StepAsync("A queues the party at the stone; both hear JOINED", async () =>
            {
                long mark = b.Mark();
                ScenarioContext.Expect(await a.JoinMeetingStoneAsync(Stone), "join refused");
                await b.WaitForPacketAsync(WorldOpcode.SmsgMeetingstoneSetqueue, ScenarioMeetingStones.SetQueue,
                    q => q == new MeetingStoneQueueView(StoneArea, MeetingStoneStatus.JoinedQueue), mark);
            });

            await context.StepAsync("B, who does not lead, is refused", async () =>
            {
                long mark = b.Mark();
                ScenarioContext.Expect(await b.JoinMeetingStoneAsync(Stone), "join refused");
                MeetingStoneFailure failure = await b.WaitForPacketAsync(WorldOpcode.SmsgMeetingstoneJoinfailed, ScenarioMeetingStones.JoinFailed, since: mark);
                ScenarioContext.ExpectEqual(MeetingStoneFailure.PartyLeader, failure, "join failure");
            });

            await context.StepAsync("C queues alone", async () =>
            {
                long mark = c.Mark();
                ScenarioContext.Expect(await c.JoinMeetingStoneAsync(Stone), "join refused");
                await c.WaitForPacketAsync(WorldOpcode.SmsgMeetingstoneSetqueue, ScenarioMeetingStones.SetQueue,
                    q => q == new MeetingStoneQueueView(StoneArea, MeetingStoneStatus.JoinedQueue), mark);
            });

            await context.StepAsync("the queue puts C into the party (MEMBER_ADDED to the party)", async () =>
            {
                long mark = a.Mark();
                ulong added = await a.WaitForPacketAsync(WorldOpcode.SmsgMeetingstoneMemberAdded, ScenarioMeetingStones.MemberAdded, since: mark);
                ScenarioContext.ExpectEqual(c.Guid.Value, added, "member added");
                await context.WaitUntilAsync("C is in A's party", () => ScenarioSteps.InGroup(context, c));
                await context.ExpectGroupAsync(a, b, c);
            });

            await context.StepAsync("C leaves: C hears NONE, the party hears MEMBER_LEFT and stays queued", async () =>
            {
                long markA = a.Mark();
                long markC = c.Mark();
                ScenarioContext.Expect(await c.LeaveGroupAsync(), "leave refused");
                await c.WaitForPacketAsync(WorldOpcode.SmsgMeetingstoneSetqueue, ScenarioMeetingStones.SetQueue,
                    q => q.Status == MeetingStoneStatus.None, markC);
                await a.WaitForPacketAsync(WorldOpcode.SmsgMeetingstoneSetqueue, ScenarioMeetingStones.SetQueue,
                    q => q == new MeetingStoneQueueView(StoneArea, MeetingStoneStatus.PartyMemberLeftLfg), markA);
                MeetingStoneFeature stones = context.Services.GetRequiredService<MeetingStoneFeature>();
                bool queued = await context.ReadAsync(() => stones.Groups!.GetGroup(a.Guid) is { } party && stones.Queue!.IsGroupQueued(party));
                ScenarioContext.Expect(queued, "the party left the queue");
            });

            await context.StepAsync("A takes the party out of the queue", async () =>
            {
                long mark = b.Mark();
                ScenarioContext.Expect(await a.LeaveMeetingStoneAsync(), "leave refused");
                await b.WaitForPacketAsync(WorldOpcode.SmsgMeetingstoneSetqueue, ScenarioMeetingStones.SetQueue,
                    q => q == new MeetingStoneQueueView(0, MeetingStoneStatus.LeaveQueue), mark);
            });
        }));
    }

    [Fact]
    public async Task RitualOfSummoning_TwoHelpersComplete_TheFarPlayerAcceptsAndArrives()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(Configure);
        await world.RunPassingAsync(new DelegateScenario("ritual-of-summoning", async context =>
        {
            ScenarioBot warlock = await context.StepAsync("login the warlock", () => context.LoginAsync(PlayerbotScenarioCatalog.BotA));
            ScenarioBot b = await context.StepAsync("login helper B", () => context.LoginAsync(PlayerbotScenarioCatalog.BotB));
            ScenarioBot c = await context.StepAsync("login helper C", () => context.LoginAsync("Scngamma"));
            ScenarioBot far = await context.StepAsync("login the far player", () => context.LoginAsync("Scndelta"));
            foreach (ScenarioBot bot in new[] { warlock, b, c })
            {
                await context.StepAsync($"place {bot.Name} at the start", () => context.PlaceAsync(bot, 0, StartX, StartY, StartZ));
            }

            await context.StepAsync("place the far player 80 yards away", () => context.PlaceAsync(far, 0, StartX + 60f, StartY + 50f, StartZ));
            await ScenarioSteps.LeaveAnyGroupAsync(context, warlock, b, c, far);
            await ScenarioSteps.FormGroupAsync(context, warlock, b, c);
            await context.StepAsync("the warlock learns the ritual", () => context.LearnSpellAsync(warlock, RitualSpell));

            GameObject ritual = await context.StepAsync("the warlock targets the far player and channels the ritual", async () =>
            {
                ScenarioContext.Expect(await warlock.TargetAsync(far.Guid), "selection refused");
                ScenarioContext.Expect(await warlock.CastAsync(RitualSpell), "cast refused");
                GameObject? created = null;
                await context.WaitUntilAsync("the Summoning Portal stands", () =>
                {
                    created = context.Services.GetRequiredService<GameObjectLootFeature>().FindSystem(0)?.GameObjects
                        .FirstOrDefault(g => g.Entry == GameObjectMapSystem.PlayerSummoningRitualEntry && g.IsSpawned);
                    return created is not null;
                });
                ScenarioContext.ExpectEqual(warlock.Guid, created!.OwnerGuid, "ritual owner");
                ScenarioContext.ExpectEqual(far.Guid, created.SummonTarget, "summon target");
                return created;
            });
            (float rx, float ry) = (ritual.X, ritual.Y);

            await context.StepAsync("B helps: not enough yet", async () =>
            {
                ScenarioContext.Expect(await b.UseGameObjectAsync(ritual.Guid), "use refused");
                bool counted = await context.ReadAsync(() => ritual.Participants.Contains(b.Guid));
                ScenarioContext.Expect(counted, "B is not a participant");
                ScenarioContext.Expect(await context.ReadAsync(() => ritual.State == GameObjectState.Ready), "the ritual went off early");
            });

            await context.StepAsync("the far player cannot help (not in the warlock's party)", async () =>
            {
                ScenarioContext.Expect(await far.UseGameObjectAsync(ritual.Guid), "use refused");
                ScenarioContext.Expect(await context.ReadAsync(() => !ritual.Participants.Contains(far.Guid)), "the far player was counted");
            });

            await context.StepAsync("C helps: the summon request reaches the far player", async () =>
            {
                long mark = far.Mark();
                ScenarioContext.Expect(await c.UseGameObjectAsync(ritual.Guid), "use refused");
                byte[] request = await far.WaitForPacketAsync(WorldOpcode.SmsgSummonRequest, p => p, since: mark);
                ScenarioContext.ExpectEqual(warlock.Guid.Value, BitConverter.ToUInt64(request, 0), "summoner");
            });

            await context.StepAsync("the far player accepts and arrives beside the ritual", async () =>
            {
                ScenarioContext.Expect(await far.SendAsync(WorldOpcode.CmsgSummonResponse, ScenarioPackets.Guid(warlock.Guid.Value)), "response refused");
                await context.WaitUntilAsync("the far player is beside the ritual", () => far.Session!.Player is { } p
                    && MathF.Sqrt(((p.X - rx) * (p.X - rx)) + ((p.Y - ry) * (p.Y - ry))) < 3f);
            });

            await context.StepAsync("the used-up ritual goes", () => context.WaitUntilAsync("the ritual is gone",
                () => context.Services.GetRequiredService<GameObjectLootFeature>().FindSystem(0)?.Find(ritual.Guid) is null));
        }));
    }
}
