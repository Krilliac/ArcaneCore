using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Death.Visibility;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Death;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Loot;
using Xunit;

namespace ArcaneCore.Game.Tests.Memo;

/// <summary>
/// The memo and lookup tables of the hot paths (docs/integration/memo-tabulation-20261015.md) give exactly what the computation they replace
/// gave: each test runs the original computation (kept verbatim) beside the tabulated one over generated or exhaustive inputs.
/// </summary>
public sealed class MemoEquivalenceTests
{
    // ---- Loot: per-template plans ----

    private static readonly float[] Chances = [0f, -0f, 0.01f, 0.5f, 5f, 25f, 33.3f, 50f, 99.9f, 100f, 150f, -2f, -40f, -100f, float.NaN];

    /// <summary>Random templates with every row shape: groups 0-3, explicit and zero chances, quest chances, references (with and without a
    /// group, nested, cyclic, maxcount 0 to 3), conditions and count ranges.</summary>
    private static LootContent RandomContent(int seed)
    {
        var random = new Random(seed);
        var rows = new List<(LootTableKind, LootStoreRow)>();
        foreach ((LootTableKind kind, int entries) in new[] { (LootTableKind.Creature, 40), (LootTableKind.Reference, 30), (LootTableKind.GameObject, 10) })
        {
            for (uint entry = 1; entry <= entries; entry++)
            {
                int count = random.Next(0, 13);
                for (int i = 0; i < count; i++)
                {
                    bool reference = random.Next(6) == 0;
                    rows.Add((kind, new LootStoreRow(entry, (uint)random.Next(1, 60), Chances[random.Next(Chances.Length)], (byte)random.Next(0, 4),
                        reference ? -random.Next(1, 33) : random.Next(0, 4), (uint)random.Next(0, 4), random.Next(4) == 0 ? (uint)random.Next(1, 9) : 0)));
                }
            }
        }

        return new LootContent(rows, []);
    }

    [Theory]
    [InlineData(1, false, 0)]
    [InlineData(2, true, 0)]
    [InlineData(3, false, 16)]
    [InlineData(4, true, 7)]
    [InlineData(5, false, LootGenerator.DefaultMaxProcessCallsPerRoll)]
    [InlineData(6, true, LootGenerator.DefaultMaxProcessCallsPerRoll)]
    public void LootPlans_RollExactlyAsTheRebuiltGroups_OverRandomTemplates(int seed, bool rated, int budget)
    {
        LootContent content = RandomContent(seed);
        Func<LootStoreRow, float>? rate = rated ? row => (row.Item % 4) * 0.75f : null;
        var oracle = new ReferenceLootGenerator(content, new Random(seed * 31)) { MaxProcessCallsPerRoll = budget, ChanceRate = rate };
        var generator = new LootGenerator(content, new Random(seed * 31)) { MaxProcessCallsPerRoll = budget, ChanceRate = rate };
        int compared = 0;
        int items = 0;
        for (int round = 0; round < 60; round++)
        {
            foreach (LootTableKind kind in new[] { LootTableKind.Creature, LootTableKind.Reference, LootTableKind.GameObject, LootTableKind.Item })
            {
                for (uint entry = 0; entry <= 42; entry++)
                {
                    List<RolledLoot> expected = oracle.Roll(kind, entry);
                    List<RolledLoot> actual = generator.Roll(kind, entry);
                    Assert.True(ReferenceLootGenerator.Same(expected, actual), $"seed {seed} round {round} {kind} {entry}: rolls differ");
                    compared++;
                    items += expected.Count;
                }
            }
        }

        Assert.Equal(60 * 4 * 43, compared);
        Assert.True(items > 1000, $"only {items} items rolled: the random content does not exercise the generator");
    }

    [Fact]
    public void LootPlans_GroupReferences_PickFromTheNamedGroupOnly_AsBefore()
    {
        // A reference row's group id selects that group of the referenced template, explicit chances first, then the equal pool.
        var rows = new List<(LootTableKind, LootStoreRow)>
        {
            (LootTableKind.Creature, new LootStoreRow(1, 0, 100, 2, -7, 3)),
            (LootTableKind.Creature, new LootStoreRow(1, 0, 100, 5, -7, 2, 4)),
            (LootTableKind.Reference, new LootStoreRow(7, 70, 100, 0, 1, 1)),
            (LootTableKind.Reference, new LootStoreRow(7, 71, 30, 2, 1, 1)),
            (LootTableKind.Reference, new LootStoreRow(7, 72, 0, 2, 1, 1)),
            (LootTableKind.Reference, new LootStoreRow(7, 73, 0, 2, 2, 5)),
            (LootTableKind.Reference, new LootStoreRow(7, 74, 20, 3, 1, 1)),
        };
        var content = new LootContent(rows, []);
        var oracle = new ReferenceLootGenerator(content, new Random(99));
        var generator = new LootGenerator(content, new Random(99));
        for (int i = 0; i < 2000; i++)
        {
            List<RolledLoot> expected = oracle.Roll(LootTableKind.Creature, 1);
            Assert.True(ReferenceLootGenerator.Same(expected, generator.Roll(LootTableKind.Creature, 1)), $"roll {i} differs");
            Assert.All(expected, r => Assert.Contains(r.ItemId, new uint[] { 71, 72, 73 }));
        }
    }

    // ---- Ghost visibility: the living pair answered before the raid lookup ----

    /// <summary>The rule before the change (origin/main 89397354): the raid lookup first, for every pair.</summary>
    private sealed class ReferenceGhostRule(Func<ISpellGroupResolver?> groups)
    {
        public bool CanSeePlayer(Player viewer, Player target)
        {
            if (viewer.IsGameMaster && target.Session.Security <= viewer.Session.Security)
            {
                return true;
            }

            if (groups.Invoke() is { } resolver && resolver.GetGroupMembers(target, raid: true).Contains(viewer.Guid))
            {
                return true;
            }

            bool targetGhost = target.Combat.DeathState == DeathState.Dead;
            if (viewer.Combat.DeathState != DeathState.Dead)
            {
                return !targetGhost;
            }

            if (targetGhost && viewer.Map?.Combat.Hooks.IsFriendly(target, viewer) == true)
            {
                return true;
            }

            return target.IsAlive && WithinOfCorpse(viewer, target, GhostVisibilityRule.PlayerRange);
        }

        private static bool WithinOfCorpse(Player viewer, Unit target, float range)
        {
            if (viewer.Combat.Corpse is not { } corpse || !ReferenceEquals(corpse.Map, target.Map))
            {
                return false;
            }

            float dx = corpse.X - target.X;
            float dy = corpse.Y - target.Y;
            float dz = corpse.Z - target.Z;
            float max = range + corpse.BoundingRadius + target.BoundingRadius;
            return (dx * dx) + (dy * dy) + (dz * dz) < max * max;
        }
    }

    private sealed class CountingGroups(params ObjectGuid[][] groups) : ISpellGroupResolver
    {
        public int Calls { get; set; }

        public IReadOnlyCollection<ObjectGuid> GetGroupMembers(Unit unit, bool raid)
        {
            Calls++;
            return groups.FirstOrDefault(g => g.Contains(unit.Guid)) ?? [unit.Guid];
        }
    }

    [Fact]
    public void GhostVisibility_EveryPairOfLivingDyingGhostAndMasterPlayers_AnswersAsTheRaidFirstRule()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        DeathHooks.Register(world, new DeathHooks(new DeathOptions(), new FixedDeathClock(1_700_000_000)));
        Map map = world.GetMap(0);
        Player Add(uint guid, float x, Race race = Race.Human, AccountSecurity security = AccountSecurity.Player)
        {
            Player player = CombatTestKit.AddPlayer(world, guid, x, 0, new FakeSession((int)guid, security), race);
            if (security != AccountSecurity.Player)
            {
                player.SetGameMaster(true);
            }

            return player;
        }

        void Release(Player player, float at)
        {
            player.Health = 0;
            map.Combat.KillPlayer(player);
            Assert.True(map.Combat.RepopPlayer(player));
            player.Relocate(at, player.Y, player.Z, 0f, 0);
        }

        Player living = Add(1, 0);
        Player livingOrc = Add(2, 5, Race.Orc);
        Player dying = Add(3, 10);
        Player ghostNear = Add(4, 0);
        Player ghostFar = Add(5, 400);
        Player ghostOrc = Add(6, 30, Race.Orc);
        Player master = Add(7, 2, security: AccountSecurity.GameMaster);
        Player farLiving = Add(8, 300);
        world.RunTick(50);
        dying.Health = 0;
        map.Combat.KillPlayer(dying);
        Release(ghostNear, 20);
        Release(ghostFar, 150);
        Release(ghostOrc, 60);
        Assert.Equal(DeathState.Dead, ghostNear.Combat.DeathState);
        Assert.NotEqual(DeathState.Dead, dying.Combat.DeathState);

        Player[] players = [living, livingOrc, dying, ghostNear, ghostFar, ghostOrc, master, farLiving];
        ISpellGroupResolver?[] resolvers =
        [
            null,
            new CountingGroups(),
            new CountingGroups([living.Guid, ghostFar.Guid], [livingOrc.Guid, ghostOrc.Guid, dying.Guid]),
            new CountingGroups([.. players.Select(p => p.Guid)]),
        ];
        int livingPairs = 0;
        foreach (ISpellGroupResolver? groups in resolvers)
        {
            var oracle = new ReferenceGhostRule(() => groups);
            var rule = new GhostVisibilityRule(() => groups);
            foreach (Player viewer in players)
            {
                foreach (Player target in players)
                {
                    if (ReferenceEquals(viewer, target))
                    {
                        continue;
                    }

                    int before = (groups as CountingGroups)?.Calls ?? 0;
                    bool actual = rule.CanSee(viewer, target, alreadyVisible: false, detect: false);
                    int lookups = ((groups as CountingGroups)?.Calls ?? 0) - before;
                    Assert.True(oracle.CanSeePlayer(viewer, target) == actual, $"{viewer.Name} -> {target.Name}: the answer changed");
                    if (!viewer.IsGameMaster && viewer.Combat.DeathState != DeathState.Dead && target.Combat.DeathState != DeathState.Dead)
                    {
                        Assert.Equal(0, lookups);
                        livingPairs++;
                    }
                }
            }
        }

        Assert.True(livingPairs > 20, $"only {livingPairs} living pairs were compared");
    }

    // ---- World tick: handler tables per delegate ----

    private sealed class FirstFeature
    {
        public int Runs { get; private set; }

        public void Tick(uint diff) => Runs++;
    }

    private sealed class SecondFeature
    {
        public int Runs { get; private set; }

        public void Tick(uint diff) => Runs++;
    }

    [Fact]
    public void WorldTickHandlers_RunInSubscriptionOrder_UnderTheirNames_AndFollowEverySubscriptionChange()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var first = new FirstFeature();
        var second = new SecondFeature();
        var order = new List<string>();
        Action<uint> a = diff => { order.Add("first"); first.Tick(diff); };
        Action<uint> b = diff => { order.Add("second"); second.Tick(diff); };

        world.WorldTick += a;
        world.RunTick(50);
        world.WorldTick += b;
        world.RunTick(50);
        world.WorldTick -= a;
        world.RunTick(50);
        world.WorldTick += a;
        world.RunTick(50);

        Assert.Equal(["first", "first", "second", "second", "second", "first"], order);
        Assert.Equal((3, 3), (first.Runs, second.Runs));
        Assert.Equal("WorldTick:" + TickWorkNames.Of(a), WorldRuntime.WorldTickName(a));
        Assert.Equal("WorldTick:MemoEquivalenceTests", WorldRuntime.WorldTickName(a));
        Assert.Equal("WorldTick:FirstFeature", WorldRuntime.WorldTickName(first.Tick));
    }
}
