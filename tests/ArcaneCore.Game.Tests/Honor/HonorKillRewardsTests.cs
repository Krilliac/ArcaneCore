using System.Buffers.Binary;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Progression;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Honor;

/// <summary>
/// PvP and creature honor (vmangos Player::RewardHonorOnDeath / RewardHonor, Player.cpp:21810-21919). The kills are real
/// lethal hits through <see cref="MapCombat.DealDamage"/>; the clock is virtual so no test waits on time.
/// </summary>
public sealed class HonorKillRewardsTests : IDisposable
{
    private const uint Today = 20_000;

    private readonly WorldRuntime _world;
    private readonly Map _map;
    private readonly FixedHonorClock _clock = new(Today);
    private readonly HonorService _honor;
    private readonly HonorKillRewards _rewards;
    private readonly Dictionary<ulong, RewardGroup> _groups = [];
    private readonly HashSet<Unit> _honorless = [];
    private readonly Player _a;
    private readonly Player _victim;

    public HonorKillRewardsTests()
    {
        (_world, _map, _, _) = CombatTestKit.CreateWorld();
        _honor = new HonorService(new HonorOptions(), _clock, () => 19_997);
        _rewards = new HonorKillRewards(_honor, p => _groups.GetValueOrDefault(p.Guid.Value), noPvpCredit: u => _honorless.Contains(u));
        _rewards.Attach(_map.Combat);
        _a = AddHuman(1, 0);
        _victim = AddPlayer(2, 2, Race.Orc);
        _world.RunTick(1);
    }

    public void Dispose() => _world.Dispose();

    private Player AddPlayer(uint guid, float x, Race race, byte level = 60)
    {
        Player player = CombatTestKit.AddPlayer(_world, guid, x, 0, new FakeSession((int)guid), race, level);
        _honor.Track(player, _honor.Create(player, CharacterHonorData.Empty));
        return player;
    }

    private Player AddHuman(uint guid, float x, byte level = 60) => AddPlayer(guid, x, Race.Human, level);

    private IReadOnlyList<HonorCpRecord> RowsOf(Player p) => _honor.For(p)!.Rows;

    private void Group(params Player[] members)
    {
        var group = new RewardGroup([.. members.Select(m => m.Guid)], IsRaid: false);
        foreach (Player member in members)
        {
            _groups[member.Guid.Value] = group;
        }
    }

    private void Hit(Unit attacker, Player victim, uint damage) => _map.Combat.DealDamage(attacker, victim, damage);

    [Fact]
    public void A_lone_attacker_killing_with_one_blow_gets_the_whole_kill_honor()
    {
        Hit(_a, _victim, 5000); // the killing blow is in the history: a one-shot still counts

        Assert.False(_victim.IsAlive);
        HonorCpRecord row = Assert.Single(RowsOf(_a));
        Assert.Equal(new HonorCpRecord(4, 2, 188f, Today, (byte)HonorKind.Honorable), row);
        Assert.Equal(188, BinaryPrimitives.ReadInt32LittleEndian(LastCredit(_a)));
        Assert.Empty(RowsOf(_victim));
    }

    [Fact]
    public void Two_lone_attackers_split_by_damage_with_each_share_truncated()
    {
        Player other = AddHuman(3, -2);
        _world.RunTick(1);
        Hit(other, _victim, 300);
        Hit(_a, _victim, 700); // exactly lethal

        Assert.Equal(131f, Assert.Single(RowsOf(_a)).Cp);   // uint(188.3 * 700 / 1000)
        Assert.Equal(56f, Assert.Single(RowsOf(other)).Cp); // uint(188.3 * 300 / 1000)
    }

    [Fact]
    public void A_group_pools_its_damage_and_shares_it_evenly_with_the_group_rate()
    {
        Player b = AddHuman(3, -2);
        Player c = AddHuman(4, -3);
        Group(_a, b, c);
        _world.RunTick(1);

        Hit(_a, _victim, 5000);

        // honorRate = 1000/1000 * GroupRate(3) / 3: uint(188.3 * 0.38866...) = 73 each, including the two who did no damage.
        foreach (Player member in new[] { _a, b, c })
        {
            Assert.Equal(73f, Assert.Single(RowsOf(member)).Cp);
        }
    }

    [Fact]
    public void Dead_distant_and_same_team_members_are_left_out_of_the_split()
    {
        Player dead = AddHuman(3, -2);
        Player far = AddHuman(4, 900);
        Player orcFriend = AddPlayer(5, -3, Race.Orc);
        dead.Health = 0;
        dead.Combat.DeathState = DeathState.Corpse;
        Group(_a, dead, far, orcFriend);
        _world.RunTick(1);

        Hit(_a, _victim, 5000);

        // Only _a qualifies: the dead one, the one 900 yards away and the victim's own team get nothing; rate = 1.0 / 1.
        Assert.Equal(188f, Assert.Single(RowsOf(_a)).Cp);
        Assert.Empty(RowsOf(dead));
        Assert.Empty(RowsOf(far));
        Assert.Empty(RowsOf(orcFriend));
    }

    [Fact]
    public void An_attacker_of_the_victims_own_team_or_out_of_reach_gets_nothing()
    {
        Player orc = AddPlayer(3, -2, Race.Orc);
        Player far = AddHuman(4, 900);
        _world.RunTick(1);
        Hit(orc, _victim, 100);
        Hit(far, _victim, 100);
        Hit(_a, _victim, 800);

        Assert.Equal(150f, Assert.Single(RowsOf(_a)).Cp); // uint(188.3 * 800 / 1000): the others' damage still counts in the total
        Assert.Empty(RowsOf(orc));
        Assert.Empty(RowsOf(far));
    }

    [Fact]
    public void A_victim_at_or_below_the_gray_level_gives_no_honor()
    {
        _victim.Level = 47; // GrayLevel(60)
        Hit(_a, _victim, 5000);
        Assert.Empty(RowsOf(_a));
        _victim.Level = 48;
        Assert.True(HonorKillRewards.IsHonorOrXpTarget(_a, _victim));
        _victim.Level = 47;
        Assert.False(HonorKillRewards.IsHonorOrXpTarget(_a, _victim));
    }

    [Fact]
    public void Killing_the_same_victim_again_today_loses_ten_percent_per_earlier_kill()
    {
        _honor.Add(_a, 10f, HonorKind.Honorable, _victim); // an earlier kill of the same victim today
        Hit(_a, _victim, 5000);
        Assert.Equal(169f, RowsOf(_a)[1].Cp); // uint(188.3 * 0.9)
    }

    [Fact]
    public void The_honorless_target_aura_on_the_victim_yields_nothing_at_all()
    {
        _honorless.Add(_victim);
        Hit(_a, _victim, 5000);
        Assert.Empty(RowsOf(_a));
    }

    [Fact]
    public void A_pet_or_totem_hit_is_credited_to_its_owner()
    {
        var pet = new OwnedUnit(_a);
        pet.Spawn(_map, 1, 0);
        Hit(pet, _victim, 5000);
        Assert.Single(RowsOf(_a));
    }

    [Fact]
    public void Creature_damage_counts_in_the_total_but_credits_nobody()
    {
        var beast = new CombatTestUnit(level: 60, health: 5000);
        beast.Spawn(_map, 1, 0);
        Hit(beast, _victim, 750);
        Hit(_a, _victim, 250);
        Assert.Equal(47f, Assert.Single(RowsOf(_a)).Cp); // uint(188.3 * 250 / 1000)
    }

    [Fact]
    public void The_history_expires_after_an_idle_minute_but_not_before()
    {
        Player other = AddHuman(3, -2);
        _world.RunTick(1);
        Hit(_a, _victim, 100);
        _clock.ExtraMs += 59_000; // not yet idle for a minute
        Hit(other, _victim, 900);
        Assert.Equal(18f, Assert.Single(RowsOf(_a)).Cp);   // uint(188.3 * 100 / 1000)
        Assert.Equal(169f, Assert.Single(RowsOf(other)).Cp);

        Player third = AddHuman(4, -3);
        Player fourth = AddPlayer(5, 3, Race.Orc);
        _world.RunTick(1);
        Hit(third, fourth, 100);
        _clock.ExtraMs += 61_000; // over a minute idle: the first hit is forgotten
        Hit(_a, fourth, 900);
        Assert.Equal(188f, Assert.Single(RowsOf(_a), r => r.VictimId == 5).Cp);
        Assert.Empty(RowsOf(third));
    }

    [Fact]
    public void A_settled_death_clears_the_history()
    {
        Hit(_a, _victim, 5000);
        Assert.Empty(_honor.For(_victim)!.Ledger.Snapshot(_clock.UnixMilliseconds));
    }

    [Fact]
    public void A_death_with_no_recorded_damage_pays_nothing()
    {
        _rewards.RewardHonorOnDeath(_victim);
        Assert.Empty(RowsOf(_a));
    }

    // --- creatures -----------------------------------------------------------------------------------

    private (Creature Creature, WorldRuntime World, Map Map) CreatureAt(CreatureTemplate template, float x)
    {
        CreatureContent content = CreatureTestSupport.Content([template], [CreatureTestSupport.Spawn(1, template.Entry, x, 0)]);
        var system = new CreatureMapSystem(_map, content, random: new Random(1));
        _map.AddUpdater(system);
        _world.RunTick(50);
        return (system.Creatures.Single(), _world, _map);
    }

    private static CreatureTemplate Template(uint entry, byte level, bool civilian = false, bool leader = false)
        => CreatureTestSupport.Template(entry, b => { b.MinLevel = level; b.MaxLevel = level; b.Civilian = civilian; }) with { RacialLeader = leader };

    [Fact]
    public void A_civilian_at_or_below_gray_costs_dishonor_with_the_level_points_and_a_negative_credit()
    {
        (Creature civilian, _, _) = CreatureAt(Template(1000, level: 10, civilian: true), 5);
        ((FakeSession)_a.Session).Clear();

        Hit(_a, civilian);

        HonorCpRecord row = Assert.Single(RowsOf(_a));
        Assert.Equal(new HonorCpRecord(3, 1000, 100f, Today, (byte)HonorKind.Dishonorable), row);
        Assert.Equal(-100, BinaryPrimitives.ReadInt32LittleEndian(LastCredit(_a)));
        Assert.Equal(1u, _a.GetUInt16(UpdateFields.PlayerFieldSessionKills, 1));
    }

    [Fact]
    public void A_civilian_above_gray_or_dishonor_switched_off_gives_nothing()
    {
        (Creature civilian, _, _) = CreatureAt(Template(1000, level: 55, civilian: true), 5);
        Hit(_a, civilian);
        Assert.Empty(RowsOf(_a));

        var off = new HonorService(new HonorOptions { DishonorableKills = false }, _clock, () => 19_997);
        var offRewards = new HonorKillRewards(off, _ => null);
        Creature low = MakeUnattached(Template(1001, level: 10, civilian: true));
        off.Track(_a, off.Create(_a, CharacterHonorData.Empty));
        offRewards.RewardHonor(_a, low);
        Assert.Empty(off.For(_a)!.Rows);
    }

    [Fact]
    public void A_racial_leader_gives_488_honor_with_the_leader_rank_in_the_credit()
    {
        (Creature leader, _, _) = CreatureAt(Template(3057, level: 63, leader: true), 5);
        ((FakeSession)_a.Session).Clear();

        Hit(_a, leader);

        Assert.Equal(new HonorCpRecord(3, 3057, 488f, Today, (byte)HonorKind.Honorable), Assert.Single(RowsOf(_a)));
        byte[] credit = LastCredit(_a);
        Assert.Equal(19u, BinaryPrimitives.ReadUInt32LittleEndian(credit.AsSpan(12)));
    }

    [Fact]
    public void A_racial_leader_on_the_exclusion_list_is_an_ordinary_creature()
    {
        var excluding = new HonorService(new HonorOptions { RacialLeaderExcludedEntries = [15423] }, _clock, () => 19_997);
        var rewards = new HonorKillRewards(excluding, _ => null);
        excluding.Track(_a, excluding.Create(_a, CharacterHonorData.Empty));
        rewards.RewardHonor(_a, MakeUnattached(Template(15423, level: 60, leader: true)));
        Assert.Empty(excluding.For(_a)!.Rows);
    }

    [Fact]
    public void Every_living_group_member_in_reach_is_rewarded_for_a_creature_kill()
    {
        Player b = AddHuman(3, -2);
        Player dead = AddHuman(4, -3);
        dead.Health = 0;
        dead.Combat.DeathState = DeathState.Corpse;
        Group(_a, b, dead);
        (Creature leader, _, _) = CreatureAt(Template(3057, level: 63, leader: true), 5);

        Hit(_a, leader);

        Assert.Single(RowsOf(_a));
        Assert.Single(RowsOf(b));
        Assert.Empty(RowsOf(dead));
    }

    [Fact]
    public void An_ordinary_creature_above_gray_is_an_honor_target()
        => Assert.True(HonorKillRewards.IsHonorOrXpTarget(_a, MakeUnattached(Template(1002, level: 60))));

    // --- helpers -------------------------------------------------------------------------------------

    private void Hit(Player attacker, Creature victim) => _map.Combat.DealDamage(attacker, victim, 100_000);

    private static Creature MakeUnattached(CreatureTemplate template) => new(template.Entry, template, null, CreatureContent.Empty, new Random(1));

    private static byte[] LastCredit(Player player)
    {
        FakeSession session = (FakeSession)player.Session;
        byte[]? last = null;
        while (session.Sent.TryDequeue(out var packet))
        {
            if (packet.Opcode == WorldOpcode.SmsgPvpCredit && packet.Payload.Length == 16)
            {
                last = packet.Payload;
            }
        }

        return last ?? throw new Xunit.Sdk.XunitException("no SMSG_PVP_CREDIT was sent");
    }

    /// <summary>A unit that acts for a player (a pet, totem or charmed unit).</summary>
    private sealed class OwnedUnit(Player owner) : CombatTestUnit, IPlayerControlledUnit
    {
        public Player? ControllingPlayer { get; } = owner;
    }
}
