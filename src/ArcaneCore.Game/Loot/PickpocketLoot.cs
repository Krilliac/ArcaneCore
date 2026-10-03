using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Loot;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// Pick Pocket on one map: the loot window of a living creature (vmangos Spell::EffectPickPocket → Player::SendLoot(LOOT_PICKPOCKETING),
/// Player.cpp:7833-7872; Spell::CheckCast :6062-6071; LootHandler.cpp:283-290, 567-600). One attempt per creature: the first pick rolls its
/// pickpocketing_loot_template and the money <c>10 * (urand(0, mobLevel / 2) + urand(0, playerLevel / 2)) * MoneyRate</c>; a later pick by
/// someone who was not an original looter, or after the loot was taken completely, shows only the quest items that player needs and no money;
/// the original looters reopen what is left. The state is forgotten when the creature dies (its corpse loot replaces it, Creature.cpp:1637).
/// The window is the picker's alone (owner permission), its money is never split (LootHandler.cpp:274-290) and the picker may take from any
/// distance the loot distance allows while the creature lives. Combat does not stop a pick: the in-combat refusal vmangos has is compiled out for
/// build 5875 (Spell.cpp:5466-5470).
/// </summary>
/// <remarks>
/// Not modelled: a missed Pick Pocket breaking stealth and starting combat (vmangos Spell.cpp:1225-1243) needs the stealth and threat primitives of
/// the rogue and combat lanes; <c>LOOT_ERROR_ALREADY_PICKPOCKETED</c> is never sent (vmangos does not send it either).
/// </remarks>
public sealed class PickpocketLoot(LootService loot, Random? random = null) : ILootReleaseHandler
{
    private sealed class State(Creature creature)
    {
        public Creature Creature { get; } = creature;

        public bool Picked { get; set; }

        /// <summary>Null once the loot was cleared (taken completely and released).</summary>
        public LootBag? Bag { get; set; }

        /// <summary>vmangos m_allowedLooters: the picker and their group at the time of the roll.</summary>
        public HashSet<ObjectGuid> Allowed { get; } = [];
    }

    private readonly Dictionary<ObjectGuid, State> _states = [];
    private readonly Random _random = random ?? new Random();

    /// <summary>vmangos <c>target-&gt;GetOwnerGuid().IsPlayer()</c>: pets and totems of players cannot be pickpocketed (the pets lane supplies it).</summary>
    public Func<Creature, bool>? IsPlayerOwned { get; set; }

    /// <summary>Creatures with a pickpocket state (alive and picked at least once).</summary>
    public int TrackedCreatures => _states.Count;

    /// <summary>Spell::CheckCast for SPELL_EFFECT_PICKPOCKET: BAD_TARGETS for a non-creature or a player's creature, TARGET_NO_POCKETS without a pocket loot id.</summary>
    public SpellCastResult CheckTarget(Unit? target)
    {
        if (target is not Creature creature || IsPlayerOwned?.Invoke(creature) == true)
        {
            return SpellCastResult.BadTargets;
        }

        return loot.Content.FindPickpocketLootId(creature.Entry) == 0 ? SpellCastResult.TargetNoPockets : SpellCastResult.CastOk;
    }

    /// <summary>
    /// Player::SendLoot(LOOT_PICKPOCKETING): roll (or reopen) the loot of <paramref name="creature"/> and open the window for
    /// <paramref name="player"/>. The caller (the spell effect) has checked that the creature is alive and not friendly.
    /// </summary>
    public LootResult Pick(Player player, Creature creature)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(creature);
        if (!creature.IsInWorld || !ReferenceEquals(creature.Map, player.Map))
        {
            return LootResult.NotFound;
        }

        if (!_states.TryGetValue(creature.Guid, out State? state) || !ReferenceEquals(state.Creature, creature))
        {
            _states[creature.Guid] = state = new State(creature);
        }

        uint lootId = loot.Content.FindPickpocketLootId(creature.Entry);
        List<Player> recipients = loot.RecipientsFor(player, creature, out _);
        if (!state.Picked)
        {
            LootBag fresh = loot.Generate(creature.Guid, LootSourceKind.Creature, LootType.Pickpocketing, LootTableKind.Pickpocketing, lootId, recipients);
            uint a = (uint)_random.Next(0, (creature.Level / 2) + 1);
            uint b = (uint)_random.Next(0, (player.Level / 2) + 1);
            fresh.Gold += (uint)Math.Min(10.0 * (a + b) * loot.Options.MoneyRate, LootService.MaxMoneyAmount);
            state.Picked = true;
            state.Bag = fresh;
            state.Allowed.UnionWith(fresh.Recipients);
        }
        else if (state.Bag is null || !state.Allowed.Contains(player.Guid))
        {
            // Not an original looter, or nothing left: only the quest items this player needs, no money (LootMgr.h:329-332 leaveOnlyQuestItems).
            LootBag questOnly = loot.Generate(creature.Guid, LootSourceKind.Creature, LootType.Pickpocketing, LootTableKind.Pickpocketing, lootId, recipients);
            questOnly.KeepOnlyQuestItems();
            state.Bag = questOnly;
        }

        LootBag bag = state.Bag!;
        bag.Owner = player.Guid; // OWNER_PERMISSION: only the picker may take
        bag.ShareMoney = false;
        bag.ReleaseHandler = this;
        bag.SourceCheck = viewer => creature.IsInWorld && creature.IsAlive && viewer.IsAlive && ReferenceEquals(viewer.Map, creature.Map)
            && LootService.Distance3D(viewer, creature) <= loot.Options.LootDistance;
        return loot.ShowSpecial(player, creature, bag);
    }

    /// <summary>The creature died (subscribe to <c>MapCombat.UnitKilled</c>): its pocket state goes, the corpse loot replaces the window.</summary>
    public void OnCreatureKilled(Unit? killer, Unit victim)
    {
        if (victim is Creature creature && _states.Remove(creature.Guid, out State? state) && state.Bag is { } bag
            && ReferenceEquals(loot.FindLoot(creature.Guid), bag))
        {
            loot.RemoveSpecial(creature);
        }
    }

    /// <summary>DoLootRelease for a creature (LootHandler.cpp:567-600): a completely taken loot is cleared, leftovers stay for a reopen.</summary>
    public void OnReleased(Player player, LootBag bag)
    {
        ArgumentNullException.ThrowIfNull(bag);
        if (_states.TryGetValue(bag.Source, out State? state) && ReferenceEquals(state.Bag, bag) && bag.IsEmpty)
        {
            state.Bag = null;
            loot.RemoveSpecial(state.Creature);
        }
    }
}