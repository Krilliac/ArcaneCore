using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// Pets and the battleground spirit guide. vmangos Spell::EffectSpiritHeal (SpellEffects.cpp:5821-5846): a dead player carrying Waiting
/// to Resurrect (2584) loses it, comes back at full health without its corpse, and Player::AutoReSummonPet (Player.cpp:1580-1628) brings
/// back the pet it last had (m_petEntry / m_petSpell): the summoning spell's reagents must be in the bags and are taken, and the pet is
/// alive at full health. Its other half, Pet::Unsummon(PET_SAVE_REAGENTS) (Pet.cpp:1052-1075): a demon whose owner dies gives its soul
/// shard back.
/// </summary>
public sealed class PetSpiritHealTests : IDisposable
{
    private const uint Voidwalker = 1860;
    private const uint HunterPetEntry = 5002;
    private const uint SummonVoidwalker = 697;
    private const uint SoulShard = 6265;
    private const uint SpiritHeal = 22012;

    private readonly SpellTestKit _spells;
    private readonly Map _map;
    private readonly CreatureMapSystem _creatures;
    private readonly SummonService _service;
    private readonly ItemTemplateStore _items;

    public PetSpiritHealTests()
    {
        _spells = new SpellTestKit(
            Spell(SummonVoidwalker, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)Voidwalker)) with
            {
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
                Reagents = [new SpellReagent(SoulShard, 1)],
            },
            Spell(SpiritHealEffect.WaitingToResurrect, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            {
                Attributes = SpellAttributes.AllowCastWhileDead,
                Duration = new SpellDuration(-1, 0, -1),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(SpiritHeal, Effect(SpellEffectName.SpiritHeal, 0, SpellImplicitTarget.UnitFriend)) with
            {
                AttributesEx2 = SpellAttributesEx2.AllowDeadTarget,
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        _map = _spells.World.GetMap(0);
        CreatureContent content = CreatureTestSupport.Content(
            [.. new[] { Voidwalker, HunterPetEntry }.Select(entry => CreatureTestSupport.Template(entry, b =>
            {
                b.Name = $"Pet {entry}";
                b.Faction = 14;
                b.MinLevel = 5;
                b.MaxLevel = 5;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
            }))],
            []);
        _creatures = new CreatureMapSystem(_map, content, random: new Random(1));
        _map.AddUpdater(_creatures);
        _service = new SummonService(systems: map => ReferenceEquals(map, _map) ? _creatures : null, random: new Random(3));
        _spells.System.Units = new MapObjectResolver();
        _service.Install(_spells.System);
        _service.InstallDemons(_spells.System);
        _spells.System.Summons = _service;
        _items = new ItemTemplateStore(
            [.. ItemTestData.Templates, new ItemTemplate { Entry = SoulShard, Class = 15, Name = "Soul Shard", DisplayId = 6009, Quality = 1, MaxCount = 0 }],
            []);
    }

    public void Dispose() => _spells.Dispose();

    private Creature? Pet() => _creatures.Creatures.SingleOrDefault(c => c.IsPet);

    private (Player Player, FakeSession Session) Owner(Class cls, uint guid = 1)
    {
        (Player player, FakeSession session) = _spells.AddPlayer(guid);
        player.Level = 10;
        player.MaxHealth = 400;
        player.Health = 400;
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)cls);
        player.Inventory.Templates = _items;
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        return (player, session);
    }

    private uint Shards(Player player) => player.Inventory.GetItemCount(SoulShard);

    private void Give(Player player, uint count) => ItemTestData.Give(player.Inventory, SoulShard, count);

    /// <summary>The owner dies, the world ticks (the pet system takes the pet), the spirit is released and waits to be resurrected.</summary>
    private void DieAndWait(Player owner)
    {
        _map.Combat.KillPlayer(owner);
        _spells.World.RunTick(100);
        Assert.True(_map.Combat.RepopPlayer(owner));
        Assert.Equal(SpellCastResult.CastOk, _spells.System.CastSpell(owner, SpiritHealEffect.WaitingToResurrect, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(_spells.System.HasAura(owner, SpiritHealEffect.WaitingToResurrect));
    }

    private SpellCastResult SpiritHealOf(Player target)
    {
        (Player guide, _) = _spells.AddPlayer(9, 3, 0);
        return _spells.System.CastSpell(guide, SpiritHeal, SpellCastTargets.ForUnit(target.Guid), triggered: true);
    }

    [Fact]
    public void AnOwnersDeath_UnsummonsTheDemon_AndGivesItsSoulShardBack()
    {
        (Player warlock, _) = Owner(Class.Warlock);
        _spells.System.CastSpell(warlock, SummonVoidwalker, SpellCastTargets.ForSelf(), triggered: true);
        Assert.NotNull(Pet());
        Assert.Equal(0u, Shards(warlock));

        _map.Combat.KillPlayer(warlock);
        _spells.World.RunTick(100);

        Assert.Null(Pet());
        Assert.Equal(1u, Shards(warlock));
    }

    [Fact]
    public void SpiritHeal_ResurrectsTheWaitingPlayer_AndBringsTheDemonBack_ForASoulShard()
    {
        (Player warlock, _) = Owner(Class.Warlock);
        _spells.System.CastSpell(warlock, SummonVoidwalker, SpellCastTargets.ForSelf(), triggered: true);
        DieAndWait(warlock);
        Assert.Equal(1u, Shards(warlock)); // given back at the death
        Assert.NotNull(warlock.Combat.Corpse);

        Assert.Equal(SpellCastResult.CastOk, SpiritHealOf(warlock));

        Assert.True(warlock.IsAlive);
        Assert.Equal(400u, warlock.Health);
        Assert.Null(warlock.Combat.Corpse);
        Assert.False(_spells.System.HasAura(warlock, SpiritHealEffect.WaitingToResurrect));
        Creature pet = Assert.IsType<Creature>(Pet());
        Assert.Equal((Voidwalker, SummonVoidwalker), (pet.Entry, pet.GetUInt32(UpdateFields.UnitCreatedBySpell)));
        Assert.True(pet.IsAlive);
        Assert.Equal(pet.MaxHealth, pet.Health);
        Assert.Equal(pet.Guid, warlock.PetGuid);
        Assert.Equal(0u, Shards(warlock)); // the shard was taken again
        Assert.Equal((Voidwalker, SummonVoidwalker), _service.PetForSpiritHealer(warlock)); // the new summon is remembered again (SpellEffects.cpp:3322-3323)
    }

    [Fact]
    public void SpiritHeal_WithoutTheSoulShard_ResurrectsButBringsNoPet()
    {
        (Player warlock, _) = Owner(Class.Warlock);
        _spells.System.CastSpell(warlock, SummonVoidwalker, SpellCastTargets.ForSelf(), triggered: true);
        DieAndWait(warlock);
        warlock.Inventory.DestroyItemCount(SoulShard, 1);

        Assert.Equal(SpellCastResult.CastOk, SpiritHealOf(warlock));

        Assert.True(warlock.IsAlive);
        Assert.Null(Pet());
        Assert.Null(_service.PetForSpiritHealer(warlock)); // forgotten either way (m_petSpell = m_petEntry = 0 first)
    }

    [Fact]
    public void SpiritHeal_OfAPlayerNotWaitingToResurrect_DoesNothing()
    {
        (Player warlock, _) = Owner(Class.Warlock);
        _map.Combat.KillPlayer(warlock);
        Assert.True(_map.Combat.RepopPlayer(warlock));

        SpiritHealOf(warlock);

        Assert.False(warlock.IsAlive);
        Assert.NotNull(warlock.Combat.Corpse);
    }

    [Fact]
    public void SpiritHeal_BringsTheHuntersLivingPetBack_WithTheHealthItHad()
    {
        // vmangos Player::AutoReSummonPet (Player.cpp:1619-1628): "We may want to resurrect the pet": only a dead pet is set to full
        // health; a living one keeps what it was saved with.
        (Player hunter, _) = Owner(Class.Hunter);
        Assert.NotNull(_service.RestoreCurrentPet(hunter, new PersistentPetSnapshot(1, 801, HunterPetEntry, 5, 0, 40, 0, 0, 1, [], [])));
        Assert.NotNull(_service.PetForSpiritHealer(hunter));
        DieAndWait(hunter);
        Assert.Null(Pet());

        Assert.Equal(SpellCastResult.CastOk, SpiritHealOf(hunter));

        Creature pet = Assert.IsType<Creature>(Pet());
        Assert.Equal((HunterPetEntry, 801u), (pet.Entry, pet.Summon!.Charm!.PetNumber));
        Assert.True(pet.IsAlive);
        Assert.Equal(40u, pet.Health);
        Assert.True(pet.Health < pet.MaxHealth);
        Assert.Equal(pet.Guid, hunter.PetGuid);
    }

    [Fact]
    public void SpiritHeal_BringsTheHuntersDeadPetBack_AliveAtFullHealth()
    {
        (Player hunter, _) = Owner(Class.Hunter);
        Creature dying = _service.RestoreCurrentPet(hunter, new PersistentPetSnapshot(1, 803, HunterPetEntry, 5, 0, 40, 0, 0, 1, [], []))!;
        _creatures.KillCreature(dying);
        Assert.False(dying.IsAlive);
        DieAndWait(hunter);
        Assert.DoesNotContain(_creatures.Creatures, c => c.IsPet && c.IsAlive);

        Assert.Equal(SpellCastResult.CastOk, SpiritHealOf(hunter));

        Creature pet = Assert.Single(_creatures.Creatures, c => c.IsPet && c.IsAlive);
        Assert.Equal((HunterPetEntry, 803u), (pet.Entry, pet.Summon!.Charm!.PetNumber));
        Assert.Equal(pet.MaxHealth, pet.Health);
        Assert.Equal(pet.Guid, hunter.PetGuid);
    }

    [Fact]
    public void SpiritHeal_RepopsAtTheGraveyard_WhileTheAuraIsStillOn_AndRemovesItJustBeforeTheResurrection()
    {
        // vmangos Spell::EffectSpiritHeal (SpellEffects.cpp:5838-5842): RepopAtGraveyard, then RemoveAurasDueToSpell(2584), then
        // ResurrectPlayer. The aura goes only on the way to a resurrection that happens.
        (Player warlock, _) = Owner(Class.Warlock);
        var graveyards = new RecordingGraveyards { Probe = player => _spells.System.HasAura(player, SpiritHealEffect.WaitingToResurrect) };
        Assert.True(DeathSeams.Of(_spells.World).TryRegisterGraveyards(graveyards));
        Assert.True(DeathSeams.Of(_spells.World).TryRegisterBattlegrounds(new Presence(BattlegroundStatus.WaitJoin)));
        DieAndWait(warlock);
        graveyards.Repops.Clear();
        graveyards.Probes.Clear();

        Assert.Equal(SpellCastResult.CastOk, SpiritHealOf(warlock));

        Assert.Equal([warlock.Guid], graveyards.Repops);
        Assert.Equal([true], graveyards.Probes); // still waiting to resurrect when sent to the graveyard
        Assert.True(warlock.IsAlive);
        Assert.False(_spells.System.HasAura(warlock, SpiritHealEffect.WaitingToResurrect));
    }

    [Fact]
    public void AnAbandonedHunterPet_IsNotBroughtBack()
    {
        (Player hunter, _) = Owner(Class.Hunter);
        Creature pet = _service.RestoreCurrentPet(hunter, new PersistentPetSnapshot(1, 802, HunterPetEntry, 5, 0, 40, 0, 0, 1, [], []))!;
        new PetController(_service, () => _spells.System, new Random(5)).HandleAbandon(hunter, pet.Guid);
        Assert.Null(Pet());

        Assert.Null(_service.PetForSpiritHealer(hunter));
    }

    [Fact]
    public void SpiritHeal_BeforeTheMatchIsInProgress_SendsThePlayerToItsGraveyardFirst()
    {
        (Player warlock, _) = Owner(Class.Warlock);
        var graveyards = new RecordingGraveyards();
        Assert.True(DeathSeams.Of(_spells.World).TryRegisterGraveyards(graveyards));
        Assert.True(DeathSeams.Of(_spells.World).TryRegisterBattlegrounds(new Presence(BattlegroundStatus.WaitJoin)));
        DieAndWait(warlock);
        graveyards.Repops.Clear();

        Assert.Equal(SpellCastResult.CastOk, SpiritHealOf(warlock));

        Assert.Equal([warlock.Guid], graveyards.Repops);
        Assert.True(warlock.IsAlive);
    }

    private sealed class Presence(BattlegroundStatus status) : IBattlegroundPresence
    {
        public BattlegroundStatus? MatchStatusOf(ObjectGuid player) => status;
    }

    private sealed class RecordingGraveyards : IGraveyardRepop
    {
        public List<ObjectGuid> Repops { get; } = [];

        public Func<Player, bool>? Probe { get; init; }

        public List<bool> Probes { get; } = [];

        public bool RepopAtGraveyard(Player player)
        {
            Repops.Add(player.Guid);
            if (Probe is { } probe)
            {
                Probes.Add(probe(player));
            }

            return true;
        }

        public bool RelocateLeavingPlayer(Player player) => false;

        public bool TeleportToCorpseGraveyard(Player player, CorpsePlace? corpse) => false;
    }
}
