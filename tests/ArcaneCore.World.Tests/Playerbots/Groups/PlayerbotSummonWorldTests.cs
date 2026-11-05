using System.Numerics;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Duel;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Bots summon far group members (docs/areas/playbots-groups.md, Summons): a warlock bot casts Ritual of Summoning, group bots click the portal, the
/// member accepts (a bot with CMSG_SUMMON_RESPONSE, a real player on his own client); with no warlock, two bots at a dungeon meeting stone summon
/// through it (non-vanilla). Runs on the manual clock of <see cref="GroupTestWorld"/>; the bots hold their ground with a scenario stay and the party leash in stay mode.
/// </summary>
public sealed class PlayerbotSummonWorldTests
{
    private const byte Human = 1, Warlock = 9, Warrior = 1;
    private const uint RitualSpell = 990698, PreparedSpell = 990699, PreparedCastTimeRow = 990, RitualEffectSpell = 990720, SoulShard = 6265, StoneEntry = 178828, StoneSpawn = 990030;
    private static readonly Vector3 Camp = new(StartX, StartY, StartZ);
    private static readonly Vector3 FarCamp = new(StartX + 300f, StartY, StartZ);

    private static void Configure(IServiceCollection services, bool stone)
    {
        services.AddScoped<IGameObjectDataStore>(_ => new Objects(stone));
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
                    EffectBaseDice1 = 1, EffectDieSides1 = 1, Reagent1 = (int)SoulShard, ReagentCount1 = 1,
                },
                new SpellTemplateRow
                {
                    Id = PreparedSpell, SpellName = "Slow Ritual", RangeIndex = 1, CastingTimeIndex = PreparedCastTimeRow,
                    Effect1 = 3, EffectImplicitTargetA1 = 1, EffectBaseDice1 = 1, EffectDieSides1 = 1,
                },
                new SpellTemplateRow
                {
                    Id = RitualEffectSpell, SpellName = "Ritual of Summoning Effect", RangeIndex = 1,
                    Effect1 = 85, EffectImplicitTargetA1 = 25, EffectBaseDice1 = 1, EffectDieSides1 = 1,
                },
            ],
            CastTimes = [.. duel.CastTimes, new SpellCastTimeRow { Id = PreparedCastTimeRow, CastTime = 5000, MinCastTime = 5000 }],
            Radii = [.. duel.Radii, new SpellRadiusRow { Id = 50, Radius = 2.0f }],
        }));
    }

    private sealed class Objects(bool stone) : IGameObjectDataStore
    {
        public Task<GameObjectContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            static GameObjectTemplate Template(uint entry, GameObjectType type, string name, params (int Index, uint Value)[] data)
            {
                var values = new uint[GameObjectTemplate.DataCount];
                foreach ((int index, uint value) in data) values[index] = value;
                return new GameObjectTemplate { Entry = entry, Type = (uint)type, DisplayId = 1, Name = name, Data = values };
            }

            return Task.FromResult(new GameObjectContent(
                [
                    Template(StoneEntry, GameObjectType.MeetingStone, "Meeting Stone", (0, 24), (1, 32), (2, 719)),
                    Template(GameObjectMapSystem.PlayerSummoningRitualEntry, GameObjectType.SummoningRitual, "Summoning Portal",
                        (0, 3), (1, RitualEffectSpell), (6, 1)),
                ],
                stone ? [new GameObjectSpawn { Guid = StoneSpawn, Entry = StoneEntry, MapId = 0, X = Camp.X + 6f, Y = Camp.Y, Z = Camp.Z }] : [],
                [], [], []));
        }
    }

    private static Task<GroupTestWorld> StartAsync(Action<PlayerbotOptions>? configure = null, bool stone = false)
        => GroupTestWorld.StartAsync([], options =>
        {
            options.Summon.MeetingStones = stone; // the stone summon is off by default: the stone tests switch it on, a test's own configure may switch it off again
            configure?.Invoke(options);
        }, services: services => Configure(services, stone));

    private static void GiveShards(Player player, uint count)
    {
        var shard = new ItemTemplate { Entry = SoulShard, Name = "Soul Shard", Class = 6, InventoryType = 0, Stackable = 20 };
        player.Inventory.Templates = new ItemTemplateStore([.. (player.Inventory.Templates as ItemTemplateStore)?.All ?? [], shard]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(SoulShard, count, out _));
    }

    private static async Task<(Guid Id, string Name)> BotAsync(GroupTestWorld world, string name, byte cls, Vector3 at, bool ritual = false, uint shards = 0)
    {
        var bot = await world.AddBotAsync(name, Human, cls, 10, ritual ? [RitualSpell] : [], at);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player(bot.Id);
            if (shards > 0) GiveShards(player, shards);
            world.Bots.FindBrain(bot.Id)!.Stay = new PlayerbotStay(player.MapId, at, 30f, 1, unchecked(world.World.Host.World.NowMs + 3_600_000));
            return true;
        });
        return bot;
    }

    /// <summary>
    /// The bots and the real player <paramref name="real"/> (already in the world) join <paramref name="leader"/>'s party, and every bot is leashed to the
    /// real player in stay mode (world thread). A group of bots with no real player online is one nobody leads: the coordinator has a free bot leave it
    /// at once (see its stale-group rule), and a bot in a real player's group follows its party AI, which holds the bot still in stay mode (no walk, no
    /// teleport to the master), so the summon is what brings a far member over.
    /// </summary>
    private static Task GroupAsync(GroupTestWorld world, string real, (Guid Id, string Name) leader, params (Guid Id, string Name)[] members)
        => world.OnWorldAsync(() =>
        {
            Player lead = world.World.Host.World.FindOnlinePlayer(leader.Name)!;
            Player master = world.World.Host.World.FindOnlinePlayer(real)!;
            foreach (string name in members.Select(m => m.Name).Append(real))
            {
                Player member = world.World.Host.World.FindOnlinePlayer(name)!;
                world.GroupManager.Invite(lead, name);
                world.GroupManager.Accept(member);
            }

            foreach ((Guid id, string name) in members.Prepend(leader))
            {
                var leashed = world.Bots.Leash(id, master, stay: true);
                Assert.True(leashed.Success, $"{name}: leash refused ({leashed.Code})");
            }

            return true;
        });

    private static Task PlaceRealAsync(GroupTestWorld world, string name, Vector3 at)
        => world.OnWorldAsync(() =>
        {
            world.World.Host.World.FindOnlinePlayer(name)!.Relocate(at.X, at.Y, at.Z, 0, world.World.Host.World.NowMs);
            return true;
        });

    private static Player Real(GroupTestWorld world, string name) => world.World.Host.World.FindOnlinePlayer(name)!;

    private static float Distance(Player a, Player b) => Vector3.Distance(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z));

    private static IEnumerable<GameObject> Portals(GroupTestWorld world)
        => world.World.Services.GetRequiredService<GameObjectLootFeature>().FindSystem(0)?.GameObjects
            .Where(g => g.Entry == GameObjectMapSystem.PlayerSummoningRitualEntry) ?? [];

    [Fact]
    public async Task AWarlockBot_SummonsAFarBotMember_TwoBotHelpersClick_AndTheMemberAcceptsAndArrives()
    {
        await using GroupTestWorld world = await StartAsync();
        var warlock = await BotAsync(world, "Summonlock", Warlock, Camp, ritual: true, shards: 3);
        var helperOne = await BotAsync(world, "Summonhelpa", Warrior, Camp + new Vector3(4f, 4f, 0f));
        var helperTwo = await BotAsync(world, "Summonhelpb", Warrior, Camp + new Vector3(-4f, 4f, 0f));
        var far = await BotAsync(world, "Summonfar", Warrior, FarCamp);
        await using WorldTestClient anchor = await world.World.EnterWorldAsync("SUMMONANCHOR", "Summonanchor", AccountSecurity.Player);
        await GroupAsync(world, "Summonanchor", warlock, helperOne, helperTwo, far);

        int helpers = 0;
        bool arrived = await world.RunUntilAsync(180_000, () =>
        {
            helpers = Math.Max(helpers, world.Bots.Summons.JobOf(world.Player(warlock.Id).Guid)?.Helpers.Count ?? 0); // the portal fires (and goes) in the tick of the last click, so its own participant list is never seen full
            return world.Bots.Summons.Accepted >= 1 && Distance(world.Player(far.Id), world.Player(warlock.Id)) < 12f;
        });

        Assert.True(arrived, await world.TraceAsync());
        Assert.Equal(1, world.Bots.Summons.Started);
        Assert.True(world.Bots.Summons.Accepted >= 1, "the far member never answered a summon request"); // the bot answers within the tick the request lands in, so no tick boundary sees the pending request
        Assert.Equal(2, helpers); // the portal asks for three participants: the warlock and exactly two helpers (the request itself proves the server counted all three)
        Assert.Equal(2u, await world.OnWorldAsync(() => world.Player(warlock.Id).Inventory.GetItemCount(SoulShard)));
    }

    [Fact]
    public async Task AWarlockBot_SummoningARealPlayer_TellsThePartyAndTheClientGetsTheRequest()
    {
        await using GroupTestWorld world = await StartAsync();
        var warlock = await BotAsync(world, "Summonlockb", Warlock, Camp, ritual: true, shards: 1);
        var helperOne = await BotAsync(world, "Summonhelpc", Warrior, Camp + new Vector3(4f, 4f, 0f));
        var helperTwo = await BotAsync(world, "Summonhelpd", Warrior, Camp + new Vector3(-4f, 4f, 0f));
        await using WorldTestClient player = await world.World.EnterWorldAsync("SUMMONREAL", "Summonreal", AccountSecurity.Player);
        await GroupAsync(world, "Summonreal", warlock, helperOne, helperTwo);
        await PlaceRealAsync(world, "Summonreal", FarCamp);

        bool offered = await world.RunUntilAsync(180_000, () => Real(world, "Summonreal").PendingSummon is not null);
        Assert.True(offered, await world.TraceAsync());
        // The warlock tells the party when its cast is taken, before the portal turns active and the request is sent: read the line first (reading to the request would skip it).
        ChatMessage line;
        do { line = await player.ReadChatAsync(); }
        while (line.Type != ChatType.Party);
        Assert.Equal("Summoning Summonreal, click the portal.", line.Text);
        byte[] request = await player.ReadUntilAsync(WorldOpcode.SmsgSummonRequest);
        Assert.Equal(await world.OnWorldAsync(() => world.Player(warlock.Id).Guid.Value), BitConverter.ToUInt64(request, 0));
    }

    [Fact]
    public async Task NoSoulShard_MeansNoCast_AndWithOneAWarlockInCombatDoesNotCastEither()
    {
        await using GroupTestWorld world = await StartAsync();
        var warlock = await BotAsync(world, "Summonnoshd", Warlock, Camp, ritual: true, shards: 0);
        var helperOne = await BotAsync(world, "Summonhelpe", Warrior, Camp + new Vector3(4f, 4f, 0f));
        var helperTwo = await BotAsync(world, "Summonhelpf", Warrior, Camp + new Vector3(-4f, 4f, 0f));
        var far = await BotAsync(world, "Summonfarb", Warrior, FarCamp);
        await using WorldTestClient anchor = await world.World.EnterWorldAsync("SUMMONANCHOR", "Summonanchor", AccountSecurity.Player);
        await GroupAsync(world, "Summonanchor", warlock, helperOne, helperTwo, far);
        await world.RunAsync(60_000);
        Assert.Equal(0, world.Bots.Summons.Started);
        Assert.Empty(await world.OnWorldAsync(() => Portals(world).ToArray()));

        // A shard now: the warlock would go, but not while it fights.
        await world.OnWorldAsync(() =>
        {
            GiveShards(world.Player(warlock.Id), 2);
            return true;
        });
        for (int i = 0; i < 40; i++)
        {
            await world.OnWorldAsync(() =>
            {
                world.Player(warlock.Id).Map!.Combat.SetInCombatState(world.Player(warlock.Id), 60_000);
                return true;
            });
            await world.RunAsync(1_000);
        }

        Assert.Equal(0, world.Bots.Summons.Started);
        Assert.Empty(await world.OnWorldAsync(() => Portals(world).ToArray()));
    }

    [Fact]
    public async Task TheOptionOff_NothingHappens()
    {
        await using GroupTestWorld world = await StartAsync(options => options.Summon.Enabled = false);
        var warlock = await BotAsync(world, "Summonlockc", Warlock, Camp, ritual: true, shards: 3);
        var helperOne = await BotAsync(world, "Summonhelpg", Warrior, Camp + new Vector3(4f, 4f, 0f));
        var helperTwo = await BotAsync(world, "Summonhelph", Warrior, Camp + new Vector3(-4f, 4f, 0f));
        var far = await BotAsync(world, "Summonfarc", Warrior, FarCamp);
        await using WorldTestClient anchor = await world.World.EnterWorldAsync("SUMMONANCHOR", "Summonanchor", AccountSecurity.Player);
        await GroupAsync(world, "Summonanchor", warlock, helperOne, helperTwo, far);
        await world.RunAsync(60_000);
        Assert.Equal(0, world.Bots.Summons.Started);
        Assert.Empty(await world.OnWorldAsync(() => Portals(world).ToArray()));
    }

    [Fact]
    public async Task ATargetThatDoesNotAnswer_IsNotSummonedAgainWithinTheRetryGap()
    {
        await using GroupTestWorld world = await StartAsync(options => options.Summon.RetrySeconds = 120);
        var warlock = await BotAsync(world, "Summonlockd", Warlock, Camp, ritual: true, shards: 3);
        var helperOne = await BotAsync(world, "Summonhelpi", Warrior, Camp + new Vector3(4f, 4f, 0f));
        var helperTwo = await BotAsync(world, "Summonhelpj", Warrior, Camp + new Vector3(-4f, 4f, 0f));
        await using WorldTestClient player = await world.World.EnterWorldAsync("SUMMONSILENT", "Summonsilent", AccountSecurity.Player);
        await GroupAsync(world, "Summonsilent", warlock, helperOne, helperTwo);
        await PlaceRealAsync(world, "Summonsilent", FarCamp);
        Assert.True(await world.RunUntilAsync(180_000, () => Real(world, "Summonsilent").PendingSummon is not null), await world.TraceAsync());
        Assert.Equal(1, world.Bots.Summons.Started);

        // The player declines (the offer is dropped); the warlock does not try again for the retry gap.
        await world.OnWorldAsync(() =>
        {
            Real(world, "Summonsilent").PendingSummon = null;
            return true;
        });
        await world.RunAsync(60_000);
        Assert.Equal(1, world.Bots.Summons.Started);

        // After the gap it may try again.
        Assert.True(await world.RunUntilAsync(180_000, () => world.Bots.Summons.Started == 2), await world.TraceAsync());
    }

    [Fact]
    public async Task StoppingARitual_EndsTheChannel_EvenWhenTheTicksActionBudgetIsSpent()
    {
        await using GroupTestWorld world = await StartAsync();
        var warlock = await BotAsync(world, "Summonlocke", Warlock, Camp, ritual: true, shards: 3);
        var helperOne = await BotAsync(world, "Summonhelpk", Warrior, Camp + new Vector3(4f, 4f, 0f));
        var helperTwo = await BotAsync(world, "Summonhelpl", Warrior, Camp + new Vector3(-4f, 4f, 0f));
        await using WorldTestClient player = await world.World.EnterWorldAsync("SUMMONBUDGET", "Summonbudget", AccountSecurity.Player);
        await GroupAsync(world, "Summonbudget", warlock, helperOne, helperTwo);
        await PlaceRealAsync(world, "Summonbudget", FarCamp);

        SpellSystem spells = world.World.Services.GetRequiredService<SpellFeature>().System;
        Func<SpellCastState?> state = () => spells.GetState(world.Player(warlock.Id).Guid)?.CurrentCast is { } cast && cast.Spell.Id == RitualSpell ? cast.State : null;
        Assert.True(await world.RunUntilAsync(180_000, () => state() == SpellCastState.Casting), await world.TraceAsync());

        // The think that ends a summon may have nothing left of the tick's shared budget; the channel must still end, or the warlock stays "casting" and cannot summon again.
        (SpellCastState? after, bool sent, bool budgetKept) = await world.OnWorldAsync(() =>
        {
            WorldSession session = world.Session(warlock.Id);
            ManagedActionBudget? saved = session.ManagedBudget;
            var spent = new ManagedActionBudget(0);
            session.ManagedBudget = spent;
            bool sentStop;
            bool kept;
            try
            {
                sentStop = PlayerbotSummons.StopRitual(session, world.Player(warlock.Id), RitualSpell);
                kept = ReferenceEquals(session.ManagedBudget, spent); // the spent budget is put back, not dropped: the rest of the tick stays refused
            }
            finally { session.ManagedBudget = saved; }

            return (state(), sentStop, kept);
        });
        Assert.True(sent, "the stop packet was refused");
        Assert.True(budgetKept, "StopRitual did not restore the session's action budget");
        Assert.Null(after);
    }

    [Fact]
    public async Task StoppingARitualStillBeingPrepared_CancelsTheCast_WithCancelCast()
    {
        await using GroupTestWorld world = await StartAsync();
        var warlock = await BotAsync(world, "Summonlockf", Warlock, Camp);
        SpellSystem spells = world.World.Services.GetRequiredService<SpellFeature>().System;

        (SpellCastState? before, bool sent, SpellCastState? after) = await world.OnWorldAsync(() =>
        {
            Player player = world.Player(warlock.Id);
            Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, PreparedSpell, SpellCastTargets.ForSelf(), triggered: false));
            SpellCastState? preparing = spells.GetState(player.Guid)?.CurrentCast?.State;
            bool sentStop = PlayerbotSummons.StopRitual(world.Session(warlock.Id), player, PreparedSpell);
            return (preparing, sentStop, spells.GetState(player.Guid)?.CurrentCast?.State);
        });

        Assert.Equal(SpellCastState.Preparing, before); // a 5 s cast bar: the test proves nothing if it had already gone off
        Assert.True(sent, "the cancel packet was refused");
        Assert.Null(after);
    }

    /// <summary>
    /// One think of the summons seam for a bot through <paramref name="probe"/>, a summons table of its own: the world's own table has the option
    /// switched off (the test's configure) so the bots' real thinks never start a job, and the option is on only inside this call (world thread).
    /// </summary>
    private static bool ProbeDrive(GroupTestWorld world, PlayerbotSummons probe, Guid botId, PlayerbotGroupState? groupState)
    {
        PlayerbotSummonOptions summon = world.Options.Summon;
        summon.Enabled = true;
        try { return probe.Drive(world.Session(botId), world.Player(botId), world.Options, groupState, 100); }
        finally { summon.Enabled = false; }
    }

    /// <summary>
    /// The state of the bot-led group the bot is in decides whether it may start a summon (PlayerbotSummons.TryStart): only Gathering, Travelling and
    /// Regrouping do (the members are walking or waiting, a summon is the way to bring a far one over); every other state refuses. The same warlock,
    /// helpers and far member are driven for 20 s in each case, so the refusal is the state's and not the setup's (the Gathering, Travelling and
    /// Regrouping cases prove the setup starts a job).
    /// </summary>
    [Theory]
    [InlineData("Gathering", true)]
    [InlineData("Travelling", true)]
    [InlineData("Regrouping", true)]
    [InlineData("Forming", false)]
    [InlineData("Entering", false)]
    [InlineData("Engaging", false)]
    [InlineData("Wiped", false)]
    [InlineData("Leaving", false)]
    [InlineData("Disbanding", false)]
    public async Task ABotInABotLedGroup_StartsASummon_OnlyInTheGatheringTravellingAndRegroupingStates(string stateName, bool starts)
    {
        var groupState = Enum.Parse<PlayerbotGroupState>(stateName);
        await using GroupTestWorld world = await StartAsync(options => options.Summon.Enabled = false);
        var warlock = await BotAsync(world, "Gatelock", Warlock, Camp, ritual: true, shards: 3);
        var helperOne = await BotAsync(world, "Gatehelpa", Warrior, Camp + new Vector3(4f, 4f, 0f));
        var helperTwo = await BotAsync(world, "Gatehelpb", Warrior, Camp + new Vector3(-4f, 4f, 0f));
        await using WorldTestClient player = await world.World.EnterWorldAsync("GATEREAL", "Gatereal", AccountSecurity.Player);
        await GroupAsync(world, "Gatereal", warlock, helperOne, helperTwo);
        await PlaceRealAsync(world, "Gatereal", FarCamp);

        var probe = new PlayerbotSummons();
        for (int step = 0; step < 8; step++)
        {
            await world.OnWorldAsync(() => ProbeDrive(world, probe, warlock.Id, groupState));
            await world.RunAsync(2_500); // under FarStaleMs (6 s) and over EvalGapMs (2 s): the far debounce (10 s) builds up and every think evaluates
        }

        int started = await world.OnWorldAsync(() => probe.Started);
        Assert.Equal(starts ? 1 : 0, started);
    }

    /// <summary>
    /// A stop the world refuses (a quest settlement is pending on the warlock, so TryManagedAction says no) leaves the channel running: StopRitual says
    /// false, the offered summoner's job stays (the bot holds still) and the next think tries again; once the settlement clears the same think stops the
    /// channel and ends the job. Deleting the stop's result check would end the job with the channel still running.
    /// </summary>
    [Fact]
    public async Task ARefusedStop_KeepsTheJob_AndTheNextThinkAfterTheSettlementStopsTheChannelAndEndsIt()
    {
        await using GroupTestWorld world = await StartAsync(options => options.Summon.Enabled = false);
        var warlock = await BotAsync(world, "Refuselock", Warlock, Camp, ritual: true, shards: 3);
        var helperOne = await BotAsync(world, "Refusehelpa", Warrior, Camp + new Vector3(4f, 4f, 0f));
        var helperTwo = await BotAsync(world, "Refusehelpb", Warrior, Camp + new Vector3(-4f, 4f, 0f));
        await using WorldTestClient player = await world.World.EnterWorldAsync("REFUSEREAL", "Refusereal", AccountSecurity.Player);
        await GroupAsync(world, "Refusereal", warlock, helperOne, helperTwo);
        await PlaceRealAsync(world, "Refusereal", FarCamp);

        SpellSystem spells = world.World.Services.GetRequiredService<SpellFeature>().System;
        Func<SpellCastState?> state = () => spells.GetState(world.Player(warlock.Id).Guid)?.CurrentCast is { } cast && cast.Spell.Id == RitualSpell ? cast.State : null;
        var probe = new PlayerbotSummons();
        Assert.True(await world.RunUntilAsync(180_000, () =>
        {
            ProbeDrive(world, probe, warlock.Id, PlayerbotGroupState.Gathering);
            return probe.JobOf(world.Player(warlock.Id).Guid)?.Cast is { Stage: TravelCastStage.Casting } && state() == SpellCastState.Casting;
        }), await world.TraceAsync());

        // The member's offer is out (the portal's request); the settlement makes the world refuse the warlock's stop.
        Guid operation = Guid.NewGuid();
        (bool heldOne, bool heldTwo, bool jobKept, SpellCastState? whilePending) = await world.OnWorldAsync(() =>
        {
            Player caster = world.Player(warlock.Id);
            Real(world, "Refusereal").PendingSummon = new PendingSummon(caster.Guid, caster.MapId, caster.X, caster.Y, caster.Z, (long)world.World.Host.World.NowMs + 60_000);
            Assert.True(caster.BeginQuestSettlement(operation));
            Assert.False(PlayerbotSummons.StopRitual(world.Session(warlock.Id), caster, RitualSpell));
            bool first = ProbeDrive(world, probe, warlock.Id, PlayerbotGroupState.Gathering);
            bool second = ProbeDrive(world, probe, warlock.Id, PlayerbotGroupState.Gathering); // the next think: still refused, still held
            return (first, second, probe.JobOf(caster.Guid) is not null, state());
        });
        Assert.True(heldOne, "the bot let go of the job while its stop was refused");
        Assert.True(heldTwo, "the bot let go of the job on the next think while its stop was still refused");
        Assert.True(jobKept, "the job ended while the channel was still running");
        Assert.Equal(SpellCastState.Casting, whilePending);

        (bool heldAfter, bool jobLeft, SpellCastState? afterwards) = await world.OnWorldAsync(() =>
        {
            Player caster = world.Player(warlock.Id);
            Assert.True(caster.EndQuestSettlement(operation));
            bool held = ProbeDrive(world, probe, warlock.Id, PlayerbotGroupState.Gathering);
            return (held, probe.JobOf(caster.Guid) is not null, state());
        });
        Assert.False(heldAfter);
        Assert.False(jobLeft, "the job stayed after the stop went out");
        Assert.Null(afterwards);
    }

    [Fact]
    public async Task TwoBotsAtAMeetingStone_SummonAFarMember_WhoAcceptsAndArrives()
    {
        await using GroupTestWorld world = await StartAsync(stone: true);
        var userOne = await BotAsync(world, "Stoneusera", Warrior, Camp + new Vector3(6f, 3f, 0f));
        var userTwo = await BotAsync(world, "Stoneuserb", Warrior, Camp + new Vector3(6f, -3f, 0f));
        var far = await BotAsync(world, "Stonefar", Warrior, FarCamp);
        await using WorldTestClient anchor = await world.World.EnterWorldAsync("SUMMONANCHOR", "Summonanchor", AccountSecurity.Player);
        await GroupAsync(world, "Summonanchor", userOne, userTwo, far);

        bool arrived = await world.RunUntilAsync(180_000, () =>
        {
            return world.Bots.Summons.Accepted >= 1 && Distance(world.Player(far.Id), world.Player(userOne.Id)) < 20f;
        });
        Assert.True(arrived, await world.TraceAsync());
        Assert.Equal(1, world.Bots.Summons.Started);
    }

    [Fact]
    public async Task AMeetingStone_WithOnlyOneBotAtIt_SummonsNobody()
    {
        await using GroupTestWorld world = await StartAsync(stone: true);
        var user = await BotAsync(world, "Stonealone", Warrior, Camp + new Vector3(6f, 3f, 0f));
        var other = await BotAsync(world, "Stoneelsewhr", Warrior, Camp + new Vector3(-150f, 0f, 0f));
        var far = await BotAsync(world, "Stonefarb", Warrior, FarCamp);
        await using WorldTestClient anchor = await world.World.EnterWorldAsync("SUMMONANCHOR", "Summonanchor", AccountSecurity.Player);
        await GroupAsync(world, "Summonanchor", user, other, far);
        await world.RunAsync(60_000);
        Assert.Equal(0, world.Bots.Summons.Started);
    }

    [Fact]
    public async Task AMeetingStone_WithTheOptionOff_SummonsNobody()
    {
        await using GroupTestWorld world = await StartAsync(options => options.Summon.MeetingStones = false, stone: true);
        var userOne = await BotAsync(world, "Stoneoffa", Warrior, Camp + new Vector3(6f, 3f, 0f));
        var userTwo = await BotAsync(world, "Stoneoffb", Warrior, Camp + new Vector3(6f, -3f, 0f));
        var far = await BotAsync(world, "Stonefarc", Warrior, FarCamp);
        await using WorldTestClient anchor = await world.World.EnterWorldAsync("SUMMONANCHOR", "Summonanchor", AccountSecurity.Player);
        await GroupAsync(world, "Summonanchor", userOne, userTwo, far);
        await world.RunAsync(60_000);
        Assert.Equal(0, world.Bots.Summons.Started);
    }

    [Fact]
    public async Task AMeetingStone_WithTheTargetInCombat_SummonsNobody()
    {
        await using GroupTestWorld world = await StartAsync(stone: true);
        var userOne = await BotAsync(world, "Stonefighta", Warrior, Camp + new Vector3(6f, 3f, 0f));
        var userTwo = await BotAsync(world, "Stonefightb", Warrior, Camp + new Vector3(6f, -3f, 0f));
        var far = await BotAsync(world, "Stonefard", Warrior, FarCamp);
        await using WorldTestClient anchor = await world.World.EnterWorldAsync("SUMMONANCHOR", "Summonanchor", AccountSecurity.Player);
        await GroupAsync(world, "Summonanchor", userOne, userTwo, far);
        for (int i = 0; i < 40; i++)
        {
            await world.OnWorldAsync(() =>
            {
                world.Player(far.Id).Map!.Combat.SetInCombatState(world.Player(far.Id), 60_000);
                return true;
            });
            await world.RunAsync(1_000);
        }

        Assert.Equal(0, world.Bots.Summons.Started);
    }

    [Fact]
    public async Task AMeetingStoneSummon_OfARealPlayer_SendsTheOrdinarySummonRequest()
    {
        await using GroupTestWorld world = await StartAsync(stone: true);
        var userOne = await BotAsync(world, "Stoneuserc", Warrior, Camp + new Vector3(6f, 3f, 0f));
        var userTwo = await BotAsync(world, "Stoneuserd", Warrior, Camp + new Vector3(6f, -3f, 0f));
        await using WorldTestClient player = await world.World.EnterWorldAsync("STONEREAL", "Stonereal", AccountSecurity.Player);
        await GroupAsync(world, "Stonereal", userOne, userTwo);
        await PlaceRealAsync(world, "Stonereal", FarCamp);
        Assert.True(await world.RunUntilAsync(180_000, () => Real(world, "Stonereal").PendingSummon is not null), await world.TraceAsync());
        byte[] request = await player.ReadUntilAsync(WorldOpcode.SmsgSummonRequest);
        Assert.True(request.Length >= 8);
    }

    [Fact]
    public async Task AWarlockInTheGroup_IsPreferredToTheMeetingStone()
    {
        await using GroupTestWorld world = await StartAsync(stone: true);
        var warlock = await BotAsync(world, "Preferlock", Warlock, Camp + new Vector3(6f, 3f, 0f), ritual: true, shards: 3);
        var helper = await BotAsync(world, "Preferhelp", Warrior, Camp + new Vector3(6f, -3f, 0f));
        var third = await BotAsync(world, "Preferthird", Warrior, Camp + new Vector3(-6f, -3f, 0f));
        var far = await BotAsync(world, "Preferfar", Warrior, FarCamp);
        await using WorldTestClient anchor = await world.World.EnterWorldAsync("SUMMONANCHOR", "Summonanchor", AccountSecurity.Player);
        await GroupAsync(world, "Summonanchor", warlock, helper, third, far);
        Assert.True(await world.RunUntilAsync(180_000, () => world.Bots.Summons.Started >= 1), await world.TraceAsync());
        Assert.Equal(SummonKind.Ritual, await world.OnWorldAsync(() => world.Bots.Summons.JobOf(world.Player(warlock.Id).Guid)?.Kind ?? SummonKind.Stone));
    }

    [Fact]
    public async Task AStoneJob_WhoseSummonerLogsOutMidJob_IsDropped_AndTheHelperIsFreed()
    {
        await using GroupTestWorld world = await StartAsync(stone: true);
        var userOne = await BotAsync(world, "Stonelogouta", Warrior, Camp + new Vector3(6f, 3f, 0f));
        var userTwo = await BotAsync(world, "Stonelogoutb", Warrior, Camp + new Vector3(6f, -3f, 0f));
        var far = await BotAsync(world, "Stonelogoutc", Warrior, FarCamp);
        await using WorldTestClient anchor = await world.World.EnterWorldAsync("SUMMONANCHOR", "Summonanchor", AccountSecurity.Player);
        await GroupAsync(world, "Summonanchor", userOne, userTwo, far);
        Assert.True(await world.RunUntilAsync(180_000, () => world.Bots.Summons.Active >= 1), await world.TraceAsync());
        string summonerName = await world.OnWorldAsync(() =>
            world.Bots.Summons.JobOf(world.Player(userOne.Id).Guid) is not null ? userOne.Name : userTwo.Name);
        string helperName = summonerName == userOne.Name ? userTwo.Name : userOne.Name;

        // The other bot takes the helper place at the stone before the summoner goes: that is the hold the drop has to release.
        Assert.True(await world.RunUntilAsync(60_000, () => world.Bots.Summons.IsHelper(world.World.Host.World.FindOnlinePlayer(helperName)!.Guid)), await world.TraceAsync());
        ObjectGuid helperGuid = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer(helperName)!.Guid);

        await world.OnWorldAsync(() =>
        {
            world.World.Host.World.LogoutPlayer(world.World.Host.World.FindOnlinePlayer(summonerName)!);
            return true;
        });
        Assert.Null(await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer(summonerName)));

        // The other bots keep thinking: any of them drops the job (its summoner is gone) instead of holding still for it until the summoner thinks again.
        Assert.True(await world.RunUntilAsync(20_000, () => world.Bots.Summons.Active == 0), await world.TraceAsync());
        // Independent of the job table: the helper's own think no longer holds still for the dropped job (Drive is false), and it holds no place.
        var helperBot = helperName == userOne.Name ? userOne : userTwo;
        bool held = await world.OnWorldAsync(() =>
            world.Bots.Summons.Drive(world.Session(helperBot.Id), world.Player(helperBot.Id), world.Options, null, 100));
        Assert.False(held, "the helper still holds still for a job that is gone");
        Assert.False(await world.OnWorldAsync(() => world.Bots.Summons.IsHelper(helperGuid)));
    }

    /// <summary>A far-since entry that was not looked at while the picks were gated starts over, so the 10 s debounce is not skipped when the gate lifts.</summary>
    [Fact]
    public void AFarSinceEntry_NotRefreshedForTheStaleTime_StartsOver()
    {
        Assert.Equal(5_000u, PlayerbotSummons.FarStart(null, 5_000));
        Assert.Equal(1_000u, PlayerbotSummons.FarStart((1_000, 5_000), 5_000 + PlayerbotSummons.FarStaleMs - 1));
        Assert.Equal(40_000u, PlayerbotSummons.FarStart((1_000, 5_000), 40_000));
        Assert.Equal(5_000u + PlayerbotSummons.FarStaleMs, PlayerbotSummons.FarStart((1_000, 5_000), 5_000 + PlayerbotSummons.FarStaleMs));
    }

    /// <summary>The stale drop is bounded by the job's own limit plus the grace: a job exactly at the bound stays, one millisecond past it is dropped.</summary>
    [Fact]
    public async Task AStoneJob_IsDroppedByAnyThink_OnlyOnceItsLimitAndGraceHavePassed()
    {
        await using GroupTestWorld world = await StartAsync(stone: true);
        var userOne = await BotAsync(world, "Stonebounda", Warrior, Camp + new Vector3(6f, 3f, 0f));
        var userTwo = await BotAsync(world, "Stoneboundb", Warrior, Camp + new Vector3(6f, -3f, 0f));
        var far = await BotAsync(world, "Stoneboundc", Warrior, FarCamp);
        await using WorldTestClient anchor = await world.World.EnterWorldAsync("SUMMONANCHOR", "Summonanchor", AccountSecurity.Player);
        await GroupAsync(world, "Summonanchor", userOne, userTwo, far);
        Assert.True(await world.RunUntilAsync(180_000, () => world.Bots.Summons.Active >= 1), await world.TraceAsync());

        (bool keptAtTheBound, bool droppedPastIt) = await world.OnWorldAsync(() =>
        {
            SummonJob job = world.Bots.Summons.JobOf(world.Player(userOne.Id).Guid) ?? world.Bots.Summons.JobOf(world.Player(userTwo.Id).Guid)!;
            WorldSession session = world.Session(userOne.Id);
            uint bound = job.StartMs + PlayerbotSummons.StoneMaxMs + PlayerbotSummons.StaleGraceMs;
            world.Bots.Summons.DropStale(session, bound);
            bool kept = world.Bots.Summons.Active == 1;
            world.Bots.Summons.DropStale(session, bound + 1);
            return (kept, world.Bots.Summons.Active == 0);
        });

        Assert.True(keptAtTheBound, "a job exactly at its limit plus the grace was dropped");
        Assert.True(droppedPastIt, "a job past its limit plus the grace was kept");
    }

    /// <summary>
    /// The meeting stone's summon is not vanilla: with <c>MeetingStones</c> off a real player's CMSG_GAMEOBJ_USE on the stone, with a group member selected and
    /// another beside it, summons nobody. The same packets summon the member with the option on, so the refusal is the option's and not the setup's.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APlayersUseOfTheStone_SummonsTheSelectedMember_OnlyWithTheMeetingStonesOptionOn(bool stones)
    {
        await using GroupTestWorld world = await StartAsync(options =>
        {
            options.Summon.MeetingStones = stones;
            options.Summon.RetrySeconds = 600; // the bots' own summon logic stays out of the way of the packet under test
        }, stone: true);
        var partner = await BotAsync(world, stones ? "Stonepartya" : "Stonepartyb", Warrior, Camp + new Vector3(6f, -3f, 0f));
        var far = await BotAsync(world, stones ? "Stoneselecta" : "Stoneselectb", Warrior, FarCamp);
        string real = stones ? "Stoneclkon" : "Stoneclkoff";
        await using WorldTestClient player = await world.World.EnterWorldAsync(real.ToUpperInvariant(), real, AccountSecurity.Player);
        await GroupAsync(world, real, partner, far);
        await PlaceRealAsync(world, real, Camp + new Vector3(6f, 1f, 0f));
        ulong farGuid = await world.OnWorldAsync(() => world.Player(far.Id).Guid.Value);
        ulong stoneGuid = await world.OnWorldAsync(() => world.World.Services.GetRequiredService<GameObjectLootFeature>().FindSystem(0)!.GameObjects
            .Single(g => g.Type == GameObjectType.MeetingStone).Guid.Value);

        await player.SendAsync(WorldOpcode.CmsgSetSelection, BitConverter.GetBytes(farGuid));
        await world.RunAsync(1_000);
        int acceptedBefore = world.Bots.Summons.Accepted;
        await player.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(stoneGuid));

        if (stones)
        {
            Assert.True(await world.RunUntilAsync(30_000, () => world.Bots.Summons.Accepted > acceptedBefore), "the stone use did not summon the selected member" + Environment.NewLine + await world.TraceAsync());
            return;
        }

        await world.RunAsync(30_000);
        Assert.Equal(acceptedBefore, world.Bots.Summons.Accepted);
        Assert.Null(await world.OnWorldAsync(() => world.Player(far.Id).PendingSummon));
        Assert.True(await world.OnWorldAsync(() => Distance(world.Player(far.Id), Real(world, real))) > 100f, "the member was brought to the stone with the option off");
    }
}
