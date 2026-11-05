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
    private const uint RitualSpell = 990698, RitualEffectSpell = 990720, SoulShard = 6265, StoneEntry = 178828, StoneSpawn = 990030;
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
                    Id = RitualEffectSpell, SpellName = "Ritual of Summoning Effect", RangeIndex = 1,
                    Effect1 = 85, EffectImplicitTargetA1 = 25, EffectBaseDice1 = 1, EffectDieSides1 = 1,
                },
            ],
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
        Assert.True(helpers >= 2, $"{helpers} bots helped at the portal"); // the request itself proves the server counted all three participants (the portal template asks for three)
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
        SpellCastState? after = await world.OnWorldAsync(() =>
        {
            WorldSession session = world.Session(warlock.Id);
            ManagedActionBudget? saved = session.ManagedBudget;
            session.ManagedBudget = new ManagedActionBudget(0);
            try { PlayerbotSummons.StopRitual(session, world.Player(warlock.Id), RitualSpell); }
            finally { session.ManagedBudget = saved; }
            return state();
        });
        Assert.NotEqual(SpellCastState.Casting, after);
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

        await world.OnWorldAsync(() =>
        {
            world.World.Host.World.LogoutPlayer(world.World.Host.World.FindOnlinePlayer(summonerName)!);
            return true;
        });
        Assert.Null(await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer(summonerName)));

        // The other bots keep thinking: any of them drops the job (its summoner is gone) instead of holding still for it until the summoner thinks again.
        Assert.True(await world.RunUntilAsync(20_000, () => world.Bots.Summons.Active == 0), await world.TraceAsync());
    }
}
