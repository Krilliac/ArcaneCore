using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>vmangos SPELL_ID_WEAPON_SWITCH_COOLDOWN_1_5s for builds after 1.8.4 (SharedDefines.h:1173-1176).</summary>
    public const uint WeaponSwitchCooldownSpell = 6119;

    /// <summary>vmangos SPELL_ID_WEAPON_SWITCH_COOLDOWN_1_0s, the rogue's spell from patch 1.9 (SharedDefines.h:1176).</summary>
    public const uint RogueWeaponSwitchCooldownSpell = 6123;

    /// <summary>Per player: the absolute end (world ms) of vmangos Player::m_weaponChangeTimer. A new Player object (relog) starts at 0, as in vmangos.</summary>
    private readonly ConditionalWeakTable<Player, StrongBox<uint>> _weaponChangeUntil = new();

    /// <summary>
    /// vmangos Player::EquipItem (Player.cpp:10340-10369; patch 1.7.0: "Switching weapons in combat triggers a 1 second global cooldown for all
    /// abilities for rogues and a 1.5 second global cooldown for everyone else"): a weapon put on while the player is alive and in combat, with no
    /// weapon change timer running, starts the timer at the switch spell's StartRecoveryTime (6119, a rogue's 6123) and that spell's global
    /// cooldown (Player::AddGCD), and tells the client with SMSG_SPELL_COOLDOWN (spell, 0 ms; Player::AddGCD with updateClient). A missing
    /// switch spell starts nothing (vmangos logs it). Called by the equip binding (<see cref="Items.ItemUse.ItemEquipSpells"/>) when a piece is
    /// worn; the world thread.
    /// </summary>
    public void OnWeaponWorn(Player player, Item item)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(item);
        if (item.Template.Class != (uint)ItemClass.Weapon || !player.IsAlive || (player.UnitFlags & UnitFlags.InCombat) == 0
            || IsWeaponChangeLocked(player))
        {
            return;
        }

        uint spellId = player.Class == Class.Rogue ? RogueWeaponSwitchCooldownSpell : WeaponSwitchCooldownSpell;
        if (Store.Get(spellId) is not { } spell)
        {
            return;
        }

        _weaponChangeUntil.AddOrUpdate(player, new StrongBox<uint>(NowMs + spell.StartRecoveryTime));
        AddGlobalCooldown(GetOrCreateState(player), spell);
        player.Session.Send(WorldOpcode.SmsgSpellCooldown, SpellPackets.BuildSpellCooldown(player.Guid, [(spellId, 0u)]));
    }

    /// <summary>
    /// Whether the weapon change timer still runs (vmangos <c>m_weaponChangeTimer != 0</c>): Player::CanEquipItem then refuses a weapon in combat
    /// with EQUIP_ERR_CANT_DO_RIGHT_NOW (Player.cpp:9710-9711; <see cref="PlayerInventory.WeaponChangeLocked"/>).
    /// </summary>
    public bool IsWeaponChangeLocked(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return _weaponChangeUntil.TryGetValue(player, out StrongBox<uint>? until) && until.Value > NowMs;
    }

    /// <summary>Whether the global cooldown of <paramref name="category"/> (StartRecoveryCategory) has run out for <paramref name="unit"/>.</summary>
    public bool IsGlobalCooldownReady(Unit unit, uint category)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return !(_states.TryGetValue(unit.Guid, out UnitSpellState? state) && ReferenceEquals(state.Unit, unit)
            && state.GlobalCooldowns.TryGetValue(category, out uint until) && until > NowMs);
    }
}
