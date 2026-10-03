using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Talents;

public sealed partial class TalentService
{
    /// <summary>
    /// vmangos Player::ResetTalents (Player.cpp:4075-4147). Returns false when nothing is spent, a quest settlement
    /// holds the character, or the price cannot be paid (the client then gets SMSG_BUY_FAILED, not enough money, item 0,
    /// like Player::SendBuyError(BUY_ERR_NOT_ENOUGHT_MONEY, 0, 0, 0) at :4094). A paid reset charges the price,
    /// advances the multiplier and stamps the time (:4132-4142). <see cref="TalentsReset"/> is raised on success.
    /// </summary>
    public bool ResetTalents(Player player, bool noCost)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!player.CanMutateQuestSettlementState)
        {
            return false;
        }

        if (UsedPoints(player) == 0)
        {
            UpdateFreeTalentPoints(player, resetIfNeed: false);   // vmangos: "for fix if need counter"
            return false;
        }

        PlayerTalentState state = StateOf(player);
        RespecQuote quote = default;
        if (!noCost)
        {
            quote = QuoteRespec(player);
            if (player.Money < quote.Cost)
            {
                player.Session.Send(WorldOpcode.SmsgBuyFailed, NpcPackets.BuyFailed(ObjectGuid.Empty, 0, BuyResult.NotEnoughMoney).AsSpan());
                return false;
            }
        }

        RemoveClassTalents(player);
        UpdateFreeTalentPoints(player, resetIfNeed: false);

        if (!noCost)
        {
            player.Money -= quote.Cost;
            state.Respec = RespecCost.AfterRespec(quote.Effective, _unixNow(), Options);
            Sink?.RespecChanged(player, state.Respec);
            Sink?.CharacterChanged(player);
        }

        TalentsReset?.Invoke(player);
        return true;
    }

    /// <summary>
    /// The price of a reset now. Reading the price decays the stored multiplier like vmangos does (the decayed value is
    /// kept and persisted unless <see cref="TalentOptions.IdempotentRespecDecay"/> is set).
    /// </summary>
    public uint ResetCost(Player player) => QuoteRespec(player).Cost;

    /// <summary>
    /// Ask the client to confirm a reset at <paramref name="trainer"/>: MSG_TALENT_WIPE_CONFIRM with the trainer guid and
    /// the current price (vmangos Player::SendTalentWipeConfirm, Player.cpp:8259-8265).
    /// </summary>
    public void SendWipeConfirm(Player player, ObjectGuid trainer)
    {
        ArgumentNullException.ThrowIfNull(player);
        uint cost = ResetCost(player);
        player.Session.Send(WorldOpcode.MsgTalentWipeConfirm, TalentPackets.WipeConfirm(trainer, cost).AsSpan());
    }

    /// <summary>
    /// The empty confirmation that tells the client no talent points are spent (vmangos HandleTalentWipeConfirmOpcode,
    /// SkillHandler.cpp:50-53): guid 0 and, per <see cref="TalentOptions.EmptyConfirmCost"/>, the current price (vmangos) or 0
    /// (mangos-classic).
    /// </summary>
    public void SendEmptyWipeConfirm(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        uint cost = Options.EmptyConfirmCost == TalentEmptyConfirmCost.Zero ? 0 : ResetCost(player);
        player.Session.Send(WorldOpcode.MsgTalentWipeConfirm, TalentPackets.WipeConfirm(ObjectGuid.Empty, cost).AsSpan());
    }

    private RespecQuote QuoteRespec(Player player)
    {
        PlayerTalentState state = StateOf(player);
        RespecQuote quote = RespecCost.Quote(state.Respec, _unixNow(), Options);
        if (quote.ToStore != state.Respec)
        {
            state.Respec = quote.ToStore;
            Sink?.RespecChanged(player, state.Respec);
        }

        return quote;
    }

    /// <summary>
    /// The spell-removal loop of vmangos ResetTalents (Player.cpp:4098-4116): for every talent of the player's class trees
    /// (talents of another class are left alone, :4101-4104) the auras of each rank spell's trigger spells go, then the rank
    /// spell itself with its dependents.
    /// </summary>
    private void RemoveClassTalents(Player player)
    {
        uint classMask = ClassMask(player);
        foreach (TalentRecord talent in Catalog.Talents)
        {
            if ((Catalog.Tab(talent.TabId)!.ClassMask & classMask) == 0)
            {
                continue;
            }

            foreach (uint rankSpell in talent.RankSpells)
            {
                if (rankSpell == 0)
                {
                    continue;
                }

                if (Spells.Store.Get(rankSpell) is { } info)
                {
                    foreach (SpellEffectInfo effect in info.Effects)
                    {
                        if (effect.TriggerSpell != 0)
                        {
                            Spells.RemoveAuras(player, effect.TriggerSpell);
                        }
                    }
                }

                RemoveWithDependents(player, rankSpell, disableIfActive: !IsPassive(rankSpell), new HashSet<uint>());
            }
        }

        _logger.LogDebug("{Player} talents reset", player.Name);
    }
}
