using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Death.Resurrection;

/// <summary>
/// Self-resurrection: the Warlock's Soulstone, the Twisting Nether item effect and the Shaman's Reincarnation (vmangos
/// <c>Player::SelectResurrectionSpellId</c>, Player.cpp:19868-19945; <c>Player::SetDeathState</c>, Player.cpp:1507-1570;
/// <c>WorldSession::HandleSelfResOpcode</c>, SpellHandler.cpp:461-485). When a player dies, the spell it can resurrect itself with is
/// chosen and written to PLAYER_SELF_RES_SPELL, which makes the client's release-spirit dialog offer a button with that spell's name;
/// CMSG_SELF_RES then casts it (<see cref="Use"/>). The field is chosen before the death strips the player's auras (the soulstone is
/// such an aura) and is emptied by any resurrection. The pre-1.6.1 client's PLAYER_FLAGS_CAN_SELF_RESURRECT path of the same
/// functions is not implemented, because this core's client is 1.12.1.
/// </summary>
public static class SelfResurrection
{
    /// <summary>SPELL_SOULSTONE_RES_R1_PASSIVE to R5 (the buff the Soulstone gives), with the effect spell each one stands for.</summary>
    private static readonly Dictionary<uint, uint> s_soulstoneEffects = new()
    {
        [20707] = 3026,
        [20762] = 20758,
        [20763] = 20759,
        [20764] = 20760,
        [20765] = 20761,
    };

    /// <summary>The soulstone aura spells are told apart by their visual and icon (vmangos checks SpellVisual 99 and SpellIconID 92).</summary>
    private const uint SoulstoneVisual = 99;

    private const uint SoulstoneIcon = 92;

    /// <summary>SPELL_TWISTING_NETHER_PASSIVE (the item aura).</summary>
    public const uint TwistingNetherPassive = 23701;

    /// <summary>SPELL_TWISTING_NETHER_EFFECT (the resurrection).</summary>
    public const uint TwistingNetherEffect = 23700;

    /// <summary>The chance, in percent, that Twisting Nether works on a death (roll_chance_i(10)).</summary>
    private const int TwistingNetherChance = 10;

    /// <summary>SPELL_REINCARNATION_PASSIVE (learnable).</summary>
    public const uint ReincarnationPassive = 20608;

    /// <summary>SPELL_REINCARNATION_EFFECT (the resurrection).</summary>
    public const uint ReincarnationEffect = 21169;

    /// <summary>ITEM_ANKH, the reagent of Reincarnation.</summary>
    public const uint Ankh = 17030;

    /// <summary>
    /// The spell this player could resurrect itself with now, or 0. The loop is vmangos: over the player's dummy auras in the order they
    /// were applied, a Soulstone aura (while nothing of priority 2 was found) selects its effect spell, and Twisting Nether, which has
    /// no such guard, replaces whatever was selected when its 10% roll succeeds, so a Twisting Nether after a Soulstone wins and the
    /// other order does not; Reincarnation is the last resort, needing the passive, the Ankh and a ready effect spell.
    /// </summary>
    public static uint SelectResurrectionSpellId(SpellSystem system, Player player)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(player);
        uint priority = 0;
        uint spellId = 0;
        foreach (SpellAuraHolder holder in system.GetAuras(player))
        {
            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is not { Type: AuraType.Dummy })
                {
                    continue;
                }

                SpellInfo spell = holder.Spell;
                if (priority < 2 && spell.SpellVisual == SoulstoneVisual && spell.SpellIconId == SoulstoneIcon)
                {
                    if (!s_soulstoneEffects.TryGetValue(spell.Id, out uint effect))
                    {
                        continue; // "Unhandled spell: S.Resurrection"
                    }

                    spellId = effect;
                    priority = 3;
                }
                else if (spell.Id == TwistingNetherPassive && system.Random.Next(100) < TwistingNetherChance)
                {
                    priority = 2;
                    spellId = TwistingNetherEffect;
                }
            }
        }

        if (priority < 1 && system.Spellbook?.HasSpell(player, ReincarnationPassive) == true
            && system.Store.Get(ReincarnationEffect) is { } reincarnation
            && system.IsSpellReady(player, reincarnation) && player.Inventory.GetItemCount(Ankh) >= 1)
        {
            spellId = ReincarnationEffect;
        }

        return spellId;
    }

    /// <summary>
    /// A player has just died (before its auras are stripped): keep a self-resurrection spell it already had, else select one, and
    /// publish it in PLAYER_SELF_RES_SPELL (vmangos writes it after the aura removal; nothing here clears it in between).
    /// </summary>
    public static void OnPlayerDied(SpellSystem system, Player player)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(player);
        uint spellId = player.GetUInt32(UpdateFields.PlayerSelfResSpell);
        if (spellId == 0)
        {
            spellId = SelectResurrectionSpellId(system, player);
        }

        if (spellId != 0)
        {
            player.SetUInt32(UpdateFields.PlayerSelfResSpell, spellId);
        }
    }

    /// <summary>
    /// CMSG_SELF_RES: cast the stored spell on the player and empty the field. The spell is cast the way the client asks, not triggered, so
    /// its cooldown starts (Reincarnation's hour). Reincarnation's Ankh is taken here once the cast went through: the spell system has no
    /// reagent step, which vmangos does in <c>Spell::TakeReagents</c>. Returns whether a spell was cast.
    /// </summary>
    public static bool Use(SpellSystem system, Player player)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(player);
        uint spellId = player.GetUInt32(UpdateFields.PlayerSelfResSpell);
        if (spellId == 0)
        {
            return false;
        }

        bool cast = false;
        if (system.Store.Get(spellId) is not null)
        {
            cast = system.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: false) == SpellCastResult.CastOk;
            if (cast && spellId == ReincarnationEffect)
            {
                player.Inventory.DestroyItemCount(Ankh, 1);
            }
        }

        player.SetUInt32(UpdateFields.PlayerSelfResSpell, 0);
        return cast;
    }
}
