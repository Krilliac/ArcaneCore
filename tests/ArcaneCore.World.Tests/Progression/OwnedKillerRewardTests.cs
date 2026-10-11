using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Spells.Totems;
using ArcaneCore.World.Tests.ClassSpells.Totems;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.Reputation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Progression;

/// <summary>
/// Kill rewards when the killing blow comes from a unit a player controls (vmangos Unit::Kill, Unit.cpp:977-1000 and 1094-1097:
/// pPlayerTap starts as <c>GetCharmerOrOwnerPlayerOrPlayerItself()</c> of the killer, Unit.cpp:4806-4812, and becomes the creature's
/// original loot recipient; the group of the tap shares through Group::RewardGroupAtKill, else Player::RewardSinglePlayerAtKill).
/// A hunter's pet, a totem, a guardian or a charmed creature landing the killing blow rewards the player behind it with kill
/// experience, quest kill credit and kill reputation; a creature nobody controls rewards nobody.
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
        MinLevelHealth = 50, MaxLevelHealth = 50, AIName = "NullAI",
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

    private static WorldTestHost Start(MutableReputationContent? reputation = null)
    {
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([Victim, Helper, Totem], [], [], [], []));
        if (reputation is not null)
        {
            ReputationTestServices.Current.Value = new MemoryReputationStore();
            ReputationTestServices.Mutable.Value = reputation;
        }

        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
            ReputationTestServices.Current.Value = null;
            ReputationTestServices.Mutable.Value = null;
        }
    }

    private static Creature Spawn(WorldTestHost host, Player near, CreatureTemplate template, float dx)
        => host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(near.Map!)
            .SpawnTemporary(template, near.X + dx, near.Y, near.Z, 0);

    /// <summary>A hunter's pet or a warlock's demon: owner and creator links as SummonService gives every summoned pet.</summary>
    private static Creature SpawnPet(WorldTestHost host, Player owner)
    {
        Creature pet = Spawn(host, owner, Helper, 2);
        SummonService.ApplyOwner(pet, owner, 0);
        return pet;
    }

    private static QuestObjectiveAdapter Credits(WorldTestHost host, Player player, List<(ObjectGuid Player, uint Entry)> credits)
    {
        var adapter = new QuestObjectiveAdapter(new CreditRecorder(credits), RewardGroups.Resolver(host.WorldServices));
        adapter.Attach(player.Map!);
        return adapter;
    }

    [Fact]
    public async Task PetKillingBlow_GivesTheOwnerKillXpAndQuestCredit()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("PETXP", "Petxp");
        await host.OnWorldAsync(() =>
        {
            Player hunter = host.World.FindOnlinePlayer("Petxp")!;
            var credits = new List<(ObjectGuid, uint)>();
            using QuestObjectiveAdapter adapter = Credits(host, hunter, credits);
            Creature pet = SpawnPet(host, hunter);
            Creature victim = Spawn(host, hunter, Victim, 4);

            uint before = PlayerProgression.CurrentXp(hunter);
            hunter.Map!.Combat.Kill(pet, victim);

            Assert.True(PlayerProgression.CurrentXp(hunter) > before, "the pet's owner gets the kill experience");
            Assert.Equal([(hunter.Guid, VictimEntry)], credits);
        });
    }

    [Fact]
    public async Task PetDamage_TapsTheCreatureForItsOwner()
    {
        // Unit::DealDamage (Unit.cpp:800-819): the first damage of a player's pet records the owner as the loot recipient.
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("PETTAP", "Pettap");
        await host.OnWorldAsync(() =>
        {
            Player hunter = host.World.FindOnlinePlayer("Pettap")!;
            Creature pet = SpawnPet(host, hunter);
            Creature victim = Spawn(host, hunter, Victim, 4);

            hunter.Map!.Combat.DealDamage(pet, victim, 1, direct: false);

            Assert.Equal(hunter.Guid, victim.LootTapPlayerGuid);
        });
    }

    [Fact]
    public async Task TappedByThePlayer_FinishedByHisPet_TheOwnerGetsTheKill()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("PETFIN", "Petfin");
        await host.OnWorldAsync(() =>
        {
            Player hunter = host.World.FindOnlinePlayer("Petfin")!;
            var credits = new List<(ObjectGuid, uint)>();
            using QuestObjectiveAdapter adapter = Credits(host, hunter, credits);
            Creature pet = SpawnPet(host, hunter);
            Creature victim = Spawn(host, hunter, Victim, 4);

            uint before = PlayerProgression.CurrentXp(hunter);
            hunter.Map!.Combat.DealDamage(hunter, victim, 1, direct: false);
            hunter.Map!.Combat.DealDamage(pet, victim, victim.Health, direct: false);

            Assert.Equal(CreatureDeathState.Corpse, victim.DeathState);
            Assert.True(PlayerProgression.CurrentXp(hunter) > before);
            Assert.Equal([(hunter.Guid, VictimEntry)], credits);
        });
    }

    [Fact]
    public async Task CharmedCreatureKillingBlow_RewardsTheCharmer()
    {
        // GetCharmerOrOwnerGuid prefers the charmer (Unit.h): a mind-controlled creature's kill belongs to the priest.
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("CHARMXP", "Charmxp");
        await host.OnWorldAsync(() =>
        {
            Player priest = host.World.FindOnlinePlayer("Charmxp")!;
            var credits = new List<(ObjectGuid, uint)>();
            using QuestObjectiveAdapter adapter = Credits(host, priest, credits);
            Creature charmed = Spawn(host, priest, Helper, 2);
            CharmService.SetCharmerGuid(charmed, priest.Guid);
            Creature victim = Spawn(host, priest, Victim, 4);

            uint before = PlayerProgression.CurrentXp(priest);
            priest.Map!.Combat.Kill(charmed, victim);

            Assert.True(PlayerProgression.CurrentXp(priest) > before);
            Assert.Equal([(priest.Guid, VictimEntry)], credits);
        });
    }

    [Fact]
    public async Task UnownedCreatureKillingBlow_RewardsNobody()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("NPCKILL", "Npckill");
        await host.OnWorldAsync(() =>
        {
            Player bystander = host.World.FindOnlinePlayer("Npckill")!;
            var credits = new List<(ObjectGuid, uint)>();
            using QuestObjectiveAdapter adapter = Credits(host, bystander, credits);
            Creature guard = Spawn(host, bystander, Helper, 2);
            Creature victim = Spawn(host, bystander, Victim, 4);

            uint before = PlayerProgression.CurrentXp(bystander);
            bystander.Map!.Combat.Kill(guard, victim);

            Assert.Equal(before, PlayerProgression.CurrentXp(bystander));
            Assert.Empty(credits);
        });
    }

    [Fact]
    public async Task TotemKillingBlow_GivesTheShamanKillXpAndQuestCredit()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("TOTEMXP", "Totemxp");
        await host.OnWorldAsync(() =>
        {
            Player shaman = host.World.FindOnlinePlayer("Totemxp")!;
            var credits = new List<(ObjectGuid, uint)>();
            using QuestObjectiveAdapter adapter = Credits(host, shaman, credits);
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

            uint before = PlayerProgression.CurrentXp(shaman);
            shaman.Map!.Combat.Kill(totem, victim);

            Assert.True(PlayerProgression.CurrentXp(shaman) > before, "the totem's shaman gets the kill experience");
            Assert.Equal([(shaman.Guid, VictimEntry)], credits);
        });
    }

    [Fact]
    public async Task GroupMembersPetKillingBlow_SplitsXpAndCreditsTheWholeGroup()
    {
        await using WorldTestHost host = Start();
        WorldTestClient leader = await host.EnterWorldAsync("PETGRPL", "Petgrpleader");
        WorldTestClient member = await host.EnterWorldAsync("PETGRPM", "Petgrpmember");
        await using (leader)
        await using (member)
        {
            await leader.SendAsync(WorldOpcode.CmsgGroupInvite, [.. "petgrpmember"u8, 0]);
            await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
            await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Petgrpmember") is { } p
                && ((WorldSession)p.Session).Services.GetRequiredService<SocialFeature>().Context.Groups.GetGroup(p.Guid) is not null,
                "the party to form");

            await host.OnWorldAsync(() =>
            {
                Player lead = host.World.FindOnlinePlayer("Petgrpleader")!;
                Player hunter = host.World.FindOnlinePlayer("Petgrpmember")!;
                Assert.Same(lead.Map, hunter.Map);
                var credits = new List<(ObjectGuid, uint)>();
                using QuestObjectiveAdapter adapter = Credits(host, hunter, credits);
                Creature pet = SpawnPet(host, hunter);
                Creature victim = Spawn(host, hunter, Victim, 4);

                uint leadBefore = PlayerProgression.CurrentXp(lead);
                uint hunterBefore = PlayerProgression.CurrentXp(hunter);
                hunter.Map!.Combat.Kill(pet, victim);

                Assert.True(PlayerProgression.CurrentXp(hunter) > hunterBefore, "the pet's owner shares the group kill");
                Assert.True(PlayerProgression.CurrentXp(lead) > leadBefore, "the owner's group member shares the group kill");
                Assert.Equal(2, credits.Count);
                Assert.Contains((hunter.Guid, VictimEntry), credits);
                Assert.Contains((lead.Guid, VictimEntry), credits);
            });
        }
    }

    [Fact]
    public async Task PetKillingBlow_GivesTheOwnerKillReputation()
    {
        var reputation = new MutableReputationContent();
        reputation.OnKill.Add(new ReputationOnKillEntry(VictimEntry, BootyBay, 0, 7, false, 10, 0, false, 0, TeamDependent: false));
        await using WorldTestHost host = Start(reputation);
        await using WorldTestClient client = await host.EnterWorldAsync("PETREP", "Petrep");
        ReputationFeature feature = host.WorldServices.GetRequiredService<ReputationFeature>();
        await host.OnWorldAsync(() =>
        {
            Player hunter = host.World.FindOnlinePlayer("Petrep")!;
            Creature pet = SpawnPet(host, hunter);
            Creature victim = Spawn(host, hunter, Victim, 4);
            int before = feature.Service.GetReputation(hunter, BootyBay);

            hunter.Map!.Combat.Kill(pet, victim);

            Assert.True(feature.Service.GetReputation(hunter, BootyBay) > before, "the pet's owner gets the kill reputation");
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
