using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// Pet untraining at a hunter trainer (vmangos GOSSIP_OPTION_UNLEARNPETSKILLS, Player::SendPetSkillWipeConfirm, WorldSession::HandlePetUnlearnOpcode
/// and Pet::resetTalentsCost). The trainer option sends SMSG_PET_UNLEARN_CONFIRM with the price; the client answers CMSG_PET_UNLEARN.
/// The last price and time live on the <see cref="CharmInfo"/> only (character_pet resettalents_cost/time are not persisted: a relog
/// restarts the price at 10 silver).
/// </summary>
public sealed partial class PetController
{
    public const uint Silver = 100;
    public const uint Gold = 100 * Silver;
    private const long Day = 86_400;

    /// <summary>The unix-seconds clock of the untraining price (tests set it).</summary>
    public Func<long> UnixNow { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Raised on the world thread after a paid untraining (the world saves the owner's money).</summary>
    public event Action<Player>? PetSkillsUnlearned;

    /// <summary>
    /// vmangos Pet::resetTalentsCost: 10 silver the first time or once a day has passed since the last, then 50 silver, then 1 gold, then
    /// one more gold per untraining up to 10 gold.
    /// </summary>
    public static uint ResetCost(uint lastCost, long lastTime, long now)
    {
        long days = (now - lastTime) / Day;
        if (lastCost < 10 * Silver || days > 0)
        {
            return 10 * Silver;
        }

        if (lastCost < 50 * Silver)
        {
            return 50 * Silver;
        }

        if (lastCost < Gold)
        {
            return Gold;
        }

        return Math.Min(lastCost + Gold, 10 * Gold);
    }

    /// <summary>The price of untraining <paramref name="pet"/> now.</summary>
    public uint ResetCost(CharmInfo charm) => ResetCost(charm.ResetTalentsCost, charm.ResetTalentsTime, UnixNow());

    /// <summary>
    /// The player's hunter pet when it can be untrained (the gossip condition of GOSSIP_OPTION_UNLEARNPETSKILLS, Player.cpp): a live beast
    /// <see cref="SummonKind.Pet"/> of a hunter that knows at least one trained spell.
    /// </summary>
    public (Creature Pet, CharmInfo Charm)? UntrainablePet(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Class != Class.Hunter || player.Map?.FindObject(player.PetGuid) is not Creature { Summon: { Kind: SummonKind.Pet, Charm: { } charm } } pet
            || pet.OwnerGuid != player.Guid || pet.Template.CreatureType != SummonService.CreatureTypeBeast || !TrainedSpells(charm).Any())
        {
            return null;
        }

        return (pet, charm);
    }

    /// <summary>vmangos Player::SendPetSkillWipeConfirm: SMSG_PET_UNLEARN_CONFIRM (pet guid, price). False when there is nothing to untrain.</summary>
    public bool SendUnlearnConfirm(Player player)
    {
        if (UntrainablePet(player) is not { } owned)
        {
            return false;
        }

        player.Session.Send(WorldOpcode.SmsgPetUnlearnConfirm, BuildUnlearnConfirm(owned.Pet.Guid, ResetCost(owned.Charm)));
        return true;
    }

    public static byte[] BuildUnlearnConfirm(ObjectGuid pet, uint cost)
    {
        var w = new PacketWriter(12);
        w.WriteUInt64(pet.Value);
        w.WriteUInt32(cost);
        return w.ToArray();
    }

    /// <summary>
    /// vmangos WorldSession::HandlePetUnlearnOpcode: the current hunter pet named by <paramref name="petGuid"/> forgets its trained spells
    /// (their auras go too), its training points become level × (loyalty level − 1), its spell slots empty, the owner pays the price
    /// (SMSG_BUY_FAILED, not enough money, when he cannot) and gets SMSG_PET_SPELLS again. Free passives (no training-point cost; vmangos
    /// relearns them through LearnPetPassives) are kept. Returns true when the pet was untrained.
    /// </summary>
    public bool HandleUnlearn(Player player, ObjectGuid petGuid)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!player.IsInWorld || player.IsQuestSettlementPending || petGuid != player.PetGuid || UntrainablePet(player) is not { } owned)
        {
            return false;
        }

        (Creature pet, CharmInfo charm) = owned;
        uint cost = ResetCost(charm);
        if (player.Money < cost)
        {
            player.Session.Send(WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(ObjectGuid.Empty, 0, BuyResult.NotEnoughMoney).AsSpan());
            return false;
        }

        foreach (uint spellId in TrainedSpells(charm).ToArray())
        {
            charm.UnlearnSpell(spellId);
            _spells()?.RemoveAuras(pet, spellId);
        }

        PetLoyalty.SetTrainingPoints(pet, (int)pet.Level * (PetLoyalty.Level(pet) - 1));
        for (int slot = 0; slot < CharmInfo.ActionBarSize; slot++)
        {
            ActionButton button = charm.GetButton(slot);
            if (button.Action != 0 && button.IsForSpell && !charm.HasSpell(button.Action))
            {
                charm.SetActionBar(slot, 0, (byte)ActionType.Disabled);
            }
        }

        charm.ResetTalentsTime = UnixNow();
        charm.ResetTalentsCost = cost;
        player.Money -= cost;
        _summons.QueueCurrentPetSave(player);
        SendPetSpells(player, pet);
        PetSkillsUnlearned?.Invoke(player);
        return true;
    }

    /// <summary>The spells untraining removes: every castable spell, and the passives that cost training points.</summary>
    private IEnumerable<uint> TrainedSpells(CharmInfo charm)
    {
        foreach ((uint spellId, ActionType state) in charm.SpellStates)
        {
            if (state != ActionType.Passive || (_summons.Training is { } training && training.Abilities.TrainingPoints(spellId) > 0))
            {
                yield return spellId;
            }
        }
    }
}
