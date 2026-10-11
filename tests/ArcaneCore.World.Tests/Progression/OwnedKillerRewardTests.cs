using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Spells.Totems;
using ArcaneCore.World.Tests.ClassSpells.Totems;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.GameObjects;
using ArcaneCore.World.Tests.Reputation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Progression;

/// <summary>
/// Who a creature kill rewards (experience, quest kill credit, kill reputation and corpse loot), as vmangos decides it:
/// Unit::DealDamage taps on the first hit and counts the damage origin (Unit.cpp:789, 800-819; Creature::SetLootRecipient,
/// Creature.cpp:1590-1612, where an owner's active pet holds the tap until the owner or the tap's group hits), and Unit::Kill
/// (Unit.cpp:981-1008, 1094-1097) starts from the killing blow's player (GetCharmerOrOwnerPlayerOrPlayerItself, Unit.cpp:4806-4812),
/// takes the creature's loot recipient and group instead, credits no player while a pet still holds the tap, and drops the tap of a
/// creature players damaged 35% or less (Creature::IsLootAllowedDueToDamageOrigin, Creature.h:548-554).
/// </summary>
public sealed class OwnedKillerRewardTests
{
    private const uint VictimEntry = 960201;
    private const uint HelperEntry = 960202;
    private const uint SummonSpell = 960203;
    private const uint BootyBay = 21;

    private static readonly CreatureTemplate Victim = new()
    {
        Entry = VictimEntry, Name = "Owned Kill Victim", MinLevel = 1, MaxLevel = 1, DisplayIds = [903], Faction = 14,
        MinLevelHealth = 100, MaxLevelHealth = 100, AIName = "NullAI",
    };

    private static readonly CreatureTemplate Helper = new()
    {
        Entry = HelperEntry, Name = "Owned Kill Helper", MinLevel = 1, MaxLevel = 1, DisplayIds = [903], Faction = 35,
        MinLevelHealth = 50, MaxLevelHealth = 50, AIName = "NullAI",
    };

    private static readonly CreatureTemplate Totem = new()
    {
        Entry = TotemTestServices.TotemEntry, Name = "Owned Kill Totem", MinLevel = 1, MaxLevel = 1, DisplayIds = [903],
        Faction = 35, MinLevelHealth = 5, MaxLevelHealth = 5, AIName = "TotemAI",
    };

    private static WorldTestHost Start()
    {
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([Victim, Helper, Totem], [], [], [], []));
        GameObjectTestStore.Current.Value = new GameObjectTestContext(GameObjectContent.Empty,
            new LootContent([], [new CreatureLootInfo(VictimEntry, 0, 0, 10, 20)]));
        var reputation = new MutableReputationContent();
        reputation.OnKill.Add(new ReputationOnKillEntry(VictimEntry, BootyBay, 0, 7, false, 10, 0, false, 0, TeamDependent: false));
        ReputationTestServices.Current.Value = new MemoryReputationStore();
        ReputationTestServices.Mutable.Value = reputation;
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
            GameObjectTestStore.Current.Value = null;
            ReputationTestServices.Current.Value = null;
            ReputationTestServices.Mutable.Value = null;
        }
    }

    /// <summary>One kill observed on every reward path.</summary>
    private sealed class Watch : IDisposable
    {
        private readonly WorldTestHost _host;
        private readonly QuestObjectiveAdapter _adapter;
        private readonly Dictionary<Player, (uint Xp, int Rep)> _before = [];

        public Watch(WorldTestHost host, Map map, params Player[] players)
        {
            _host = host;
            _adapter = new QuestObjectiveAdapter(new CreditRecorder(Credits), RewardGroups.Resolver(host.WorldServices));
            _adapter.Attach(map);
            foreach (Player player in players)
            {
                _before[player] = (PlayerProgression.CurrentXp(player), Rep(player));
            }
        }

        public List<(ObjectGuid Player, uint Entry)> Credits { get; } = [];

        public uint XpGained(Player player) => PlayerProgression.CurrentXp(player) - _before[player].Xp;

        public int RepGained(Player player) => Rep(player) - _before[player].Rep;

        private int Rep(Player player) => _host.WorldServices.GetRequiredService<ReputationFeature>().Service.GetReputation(player, BootyBay);

        public LootBag? Loot(Creature victim)
            => _host.WorldServices.GetRequiredService<GameObjectLootFeature>().FindSystem(victim.Map!)!.Loot!.FindLoot(victim.Guid);

        /// <summary>Experience, KilledMonster, reputation and the corpse loot all went to <paramref name="rewarded"/>, and to nobody else watched.</summary>
        public void AssertRewarded(Creature victim, params Player[] rewarded)
        {
            Assert.Equal(CreatureDeathState.Corpse, victim.DeathState);
            foreach ((Player player, _) in _before)
            {
                bool expected = rewarded.Contains(player);
                Assert.True(expected == XpGained(player) > 0, $"{player.Name}: experience {XpGained(player)}, expected {(expected ? "some" : "none")}");
                Assert.True(expected == RepGained(player) > 0, $"{player.Name}: reputation {RepGained(player)}, expected {(expected ? "some" : "none")}");
                Assert.True(expected == Credits.Contains((player.Guid, VictimEntry)), $"{player.Name}: quest kill credit, expected {expected}");
            }

            Assert.Equal(rewarded.Length, Credits.Count);
            LootBag? bag = Loot(victim);
            if (rewarded.Length == 0)
            {
                Assert.Null(bag);
            }
            else
            {
                Assert.NotNull(bag);
                Assert.All(rewarded, p => Assert.Contains(p.Guid, bag.Recipients));
            }
        }

        public void Dispose() => _adapter.Dispose();
    }

    private static Creature Spawn(WorldTestHost host, Player near, CreatureTemplate template, float dx)
        => host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(near.Map!)
            .SpawnTemporary(template, near.X + dx, near.Y, near.Z, 0);

    /// <summary>A hunter's pet or a warlock's demon: the owner links SummonService gives it, and the owner's UNIT_FIELD_SUMMON.</summary>
    private static Creature SpawnActivePet(WorldTestHost host, Player owner)
    {
        Creature pet = SpawnGuardian(host, owner);
        owner.SetPetGuid(pet.Guid);
        return pet;
    }

    /// <summary>A guardian: owned (UNIT_FIELD_SUMMONEDBY) but not the owner's active pet.</summary>
    private static Creature SpawnGuardian(WorldTestHost host, Player owner)
    {
        Creature guardian = Spawn(host, owner, Helper, 2);
        SummonService.ApplyOwner(guardian, owner, 0);
        return guardian;
    }

    private static void Hit(Unit attacker, Creature victim, uint damage) => victim.Map!.Combat.DealDamage(attacker, victim, damage, direct: false);

    private static void Finish(Unit attacker, Creature victim) => Hit(attacker, victim, victim.Health);

    private static async Task<(WorldTestClient Leader, WorldTestClient Member)> PartyAsync(WorldTestHost host, string leaderName, string memberName)
    {
        WorldTestClient leader = await host.EnterWorldAsync(leaderName.ToUpperInvariant(), leaderName);
        WorldTestClient member = await host.EnterWorldAsync(memberName.ToUpperInvariant(), memberName);
        await leader.SendAsync(WorldOpcode.CmsgGroupInvite, [.. System.Text.Encoding.ASCII.GetBytes(memberName.ToLowerInvariant()), 0]);
        await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(memberName) is { } p
            && ((WorldSession)p.Session).Services.GetRequiredService<SocialFeature>().Context.Groups.GetGroup(p.Guid) is not null,
            "the party to form");
        return (leader, member);
    }

    [Fact]
    public async Task ActivePetTap_PassesToTheOwnersFirstHit_ThenThePetsKillingBlowRewardsTheOwner()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("PETXP", "Petxp");
        await host.OnWorldAsync(() =>
        {
            Player hunter = host.World.FindOnlinePlayer("Petxp")!;
            using var watch = new Watch(host, hunter.Map!, hunter);
            Creature pet = SpawnActivePet(host, hunter);
            Creature victim = Spawn(host, hunter, Victim, 4);

            Hit(pet, victim, 1);
            Assert.Equal(pet.Guid, victim.LootTapPlayerGuid); // Creature.cpp:1602-1603: the active pet holds the tap
            Hit(hunter, victim, 1);
            Assert.Equal(hunter.Guid, victim.LootTapPlayerGuid); // Unit.cpp:814-815: the owner's hit takes it over
            Finish(pet, victim);

            watch.AssertRewarded(victim, hunter);
        });
    }

    [Fact]
    public async Task ACreatureOnlyAnActivePetDamaged_RewardsNobody()
    {
        // Unit.cpp:993-997: the original loot recipient is still the pet, so pPlayerTap is null; the owner has no group.
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("PETSOLO", "Petsolo");
        await host.OnWorldAsync(() =>
        {
            Player hunter = host.World.FindOnlinePlayer("Petsolo")!;
            using var watch = new Watch(host, hunter.Map!, hunter);
            Creature pet = SpawnActivePet(host, hunter);
            Creature victim = Spawn(host, hunter, Victim, 4);

            Hit(pet, victim, 10);
            Finish(pet, victim);

            watch.AssertRewarded(victim);
        });
    }

    [Fact]
    public async Task AGroupMembersActivePet_KillingAlone_RewardsTheGroupOfTheTap()
    {
        // Unit.cpp:991 and 1094-1095: pGroupTap is the group recorded with the pet's tap; RewardGroupAtKill runs without a pPlayerTap.
        await using WorldTestHost host = Start();
        (WorldTestClient leader, WorldTestClient member) = await PartyAsync(host, "Petgrpleader", "Petgrpmember");
        await using (leader)
        await using (member)
        {
            await host.OnWorldAsync(() =>
            {
                Player lead = host.World.FindOnlinePlayer("Petgrpleader")!;
                Player hunter = host.World.FindOnlinePlayer("Petgrpmember")!;
                Assert.Same(lead.Map, hunter.Map);
                using var watch = new Watch(host, hunter.Map!, lead, hunter);
                Creature pet = SpawnActivePet(host, hunter);
                Creature victim = Spawn(host, hunter, Victim, 4);

                Hit(pet, victim, 10);
                Finish(pet, victim);

                watch.AssertRewarded(victim, lead, hunter);
            });
        }
    }

    [Fact]
    public async Task GuardianKillingBlow_RewardsItsOwner()
    {
        // Creature.cpp:1602-1605: a guardian is not the owner's active pet, so it taps for the owner at once.
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GUARDXP", "Guardxp");
        await host.OnWorldAsync(() =>
        {
            Player warlock = host.World.FindOnlinePlayer("Guardxp")!;
            using var watch = new Watch(host, warlock.Map!, warlock);
            Creature guardian = SpawnGuardian(host, warlock);
            Creature victim = Spawn(host, warlock, Victim, 4);

            Finish(guardian, victim);

            watch.AssertRewarded(victim, warlock);
        });
    }

    [Fact]
    public async Task CharmedCreatureKillingBlow_RewardsTheCharmer()
    {
        // GetCharmerOrOwnerGuid prefers the charmer: a mind-controlled creature's kill belongs to the priest.
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("CHARMXP", "Charmxp");
        await host.OnWorldAsync(() =>
        {
            Player priest = host.World.FindOnlinePlayer("Charmxp")!;
            using var watch = new Watch(host, priest.Map!, priest);
            Creature charmed = Spawn(host, priest, Helper, 2);
            CharmService.SetCharmerGuid(charmed, priest.Guid);
            Creature victim = Spawn(host, priest, Victim, 4);

            Finish(charmed, victim);

            watch.AssertRewarded(victim, priest);
        });
    }

    [Fact]
    public async Task TotemKillingBlow_RewardsTheShaman()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("TOTEMXP", "Totemxp");
        await host.OnWorldAsync(() =>
        {
            Player shaman = host.World.FindOnlinePlayer("Totemxp")!;
            using var watch = new Watch(host, shaman.Map!, shaman);
            SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
            TotemFeature totems = host.WorldServices.GetRequiredService<TotemFeature>();
            spells.System.Store = new SpellStore(
                [
                    .. spells.System.Store.All,
                    new SpellInfo
                    {
                        Id = SummonSpell,
                        Name = "Owned Kill Totem Summon",
                        RangeIndex = SpellConstants.RangeIndexSelfOnly,
                        Duration = new SpellDuration(60_000, 0, 60_000),
                        Effects =
                        [
                            new SpellEffectInfo
                            {
                                Effect = SpellEffectName.SummonTotemSlot2, BasePoints = 4, BaseDice = 1, DieSides = 1,
                                TargetA = (SpellImplicitTarget)41, MiscValue = (int)TotemTestServices.TotemEntry, Radius = 2,
                            },
                        ],
                    },
                ],
                [],
                []);
            Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(shaman, SummonSpell, SpellCastTargets.ForSelf(), triggered: true));
            Creature totem = Assert.IsType<Creature>(totems.System!.GetTotem(shaman, TotemSlot.Earth));
            Creature victim = Spawn(host, shaman, Victim, 4);

            Finish(totem, victim);

            watch.AssertRewarded(victim, shaman);
        });
    }

    [Fact]
    public async Task AnUngroupedPlayerFinishingAnothersTap_TheTapperIsRewarded_TheFinisherNot()
    {
        // Unit.cpp:993-994: pPlayerTap becomes the original loot recipient, whoever lands the killing blow.
        await using WorldTestHost host = Start();
        await using WorldTestClient a = await host.EnterWorldAsync("TAPPERA", "Tappera");
        await using WorldTestClient b = await host.EnterWorldAsync("FINISHB", "Finishb");
        await host.OnWorldAsync(() =>
        {
            Player tapper = host.World.FindOnlinePlayer("Tappera")!;
            Player finisher = host.World.FindOnlinePlayer("Finishb")!;
            using var watch = new Watch(host, tapper.Map!, tapper, finisher);
            Creature victim = Spawn(host, tapper, Victim, 4);

            Hit(tapper, victim, 10);
            Finish(finisher, victim);

            watch.AssertRewarded(victim, tapper);
        });
    }

    [Fact]
    public async Task AGuardFinishingACreaturePlayersDamagedBelowTheShare_RewardsNobody()
    {
        // Creature.h:553 (65 * player > 35 * non-player fails) and Unit.cpp:1007-1008: the tap is dropped, the killer is no player.
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GUARDLOW", "Guardlow");
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Guardlow")!;
            using var watch = new Watch(host, player.Map!, player);
            Creature guard = Spawn(host, player, Helper, 2);
            Creature victim = Spawn(host, player, Victim, 4);

            Hit(player, victim, victim.MaxHealth * 30 / 100); // 30%: 65 * 30 = 1950 <= 35 * 70 = 2450
            Assert.Equal(player.Guid, victim.LootTapPlayerGuid);
            Finish(guard, victim);

            watch.AssertRewarded(victim);
            Assert.True(victim.LootTapPlayerGuid.IsEmpty);
        });
    }

    [Fact]
    public async Task AGuardFinishingACreaturePlayersDamagedAboveTheShare_RewardsTheTapper()
    {
        // Creature.h:553 (65 * 40 = 2600 > 35 * 60 = 2100): the tap stands and pPlayerTap is the loot recipient (Unit.cpp:993-994).
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GUARDHIGH", "Guardhigh");
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Guardhigh")!;
            using var watch = new Watch(host, player.Map!, player);
            Creature guard = Spawn(host, player, Helper, 2);
            Creature victim = Spawn(host, player, Victim, 4);

            Hit(player, victim, victim.MaxHealth * 40 / 100);
            Finish(guard, victim);

            watch.AssertRewarded(victim, player);
        });
    }

    [Fact]
    public async Task KillingACreatureAPlayerCharms_IsPvp_NoExperienceReputationOrQuestCredit()
    {
        // Player.cpp:19961-19982 and Group.cpp:2364: IsCharmerOrOwnerPlayerOrPlayerItself makes it a PvP kill.
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("CHARMVIC", "Charmvic");
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Charmvic")!;
            using var watch = new Watch(host, player.Map!, player);
            Creature victim = Spawn(host, player, Victim, 4);
            CharmService.SetCharmerGuid(victim, player.Guid);

            Finish(player, victim);

            Assert.Equal(CreatureDeathState.Corpse, victim.DeathState);
            Assert.Equal(0u, watch.XpGained(player));
            Assert.Equal(0, watch.RepGained(player));
            Assert.Empty(watch.Credits);
        });
    }

    [Fact]
    public async Task UnownedCreatureKill_RewardsNobody()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("NPCKILL", "Npckill");
        await host.OnWorldAsync(() =>
        {
            Player bystander = host.World.FindOnlinePlayer("Npckill")!;
            using var watch = new Watch(host, bystander.Map!, bystander);
            Creature guard = Spawn(host, bystander, Helper, 2);
            Creature victim = Spawn(host, bystander, Victim, 4);

            Finish(guard, victim);

            watch.AssertRewarded(victim);
        });
    }

    private sealed class CreditRecorder(List<(ObjectGuid Player, uint Entry)> credits) : ArcaneCore.Game.Quests.IQuestObjectiveEvents
    {
        public void KilledMonsterCredit(Player player, uint entry, ObjectGuid guid) => credits.Add((player.Guid, entry));

        public void CastedCreatureOrGo(Player player, uint entry, ObjectGuid guid, bool isCreature, uint spellId) { }

        public void TalkedToCreature(Player player, uint entry, ObjectGuid guid) { }

        public void ItemAdded(Player player, uint entry, uint count) { }

        public void ItemRemoved(Player player, uint entry, uint count) { }

        public void AreaExploredOrEventHappens(Player player, uint questId) { }

        public void FailQuest(Player player, uint questId) { }

        public void MoneyChanged(Player player) { }
    }
}
