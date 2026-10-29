using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>
/// The GM toggle spells of the vmangos GM spell list (CharacterCommands.cpp:2627-2667; scripts in spell_special.cpp). vmangos checks no account
/// level inside them: it relies on the spells being learnable only through the GM commands. Here the caster must also hold the account
/// security of the matching chat command, so a stray learned copy gives a normal player nothing (a deliberate tightening, the toggles
/// themselves are unchanged).
/// </summary>
internal static class GmToggleSpells
{
    public const string GmChatOn = "GM Chat Badge is ON", GmChatOff = "GM Chat Badge is OFF";
    public const string InvisOn = "INVIS mode is ON", InvisOff = "INVIS mode is OFF";
    public const string Visible = "You are now visible.", Invisible = "You are now invisible.";

    public static Player? Allowed(SpellEffectContext context, AccountSecurity needed)
        => context.EffectIndex == 0 && context.Caster is Player player && player.Security >= needed ? player : null;

    public static void Notify(Player player, string text)
        => player.Session.Send(WorldOpcode.SmsgNotification, Text(text));

    private static byte[] Text(string text)
    {
        var writer = new PacketWriter(text.Length + 1);
        writer.WriteCString(text);
        return writer.ToArray();
    }
}

/// <summary>
/// SHOWLABEL Only OFF 456 and ON 2765 (vmangos spell_special.cpp:79-116): Player::SetGMChat(false / true, notify), the GM badge on chat
/// (the same flag as <c>.gm chat</c>, which needs a Moderator).
/// </summary>
[SpellScript(456, 2765)]
public sealed class ShowLabelSpellScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (GmToggleSpells.Allowed(context, AccountSecurity.Moderator) is not { } player)
        {
            return;
        }

        bool on = context.Spell.Id == 2765;
        player.GmChat = on;
        GmToggleSpells.Notify(player, on ? GmToggleSpells.GmChatOn : GmToggleSpells.GmChatOff);
    }
}

/// <summary>
/// INVIS Only ON 2763 and OFF 6147 (vmangos spell_special.cpp:155-191): Player::SetGMVisible(false / true, notify), the same state as
/// <c>.gm visible off|on</c> (a GameMaster): players of a lower account security stop seeing the caster (<see cref="Maps.GmInvisibilityRule"/>).
/// vmangos also lays the permanent visual aura 16380 and switches GM mode on when going invisible; this code base keeps its existing
/// <c>.gm visible</c> behaviour, which does neither.
/// </summary>
[SpellScript(2763, 6147)]
public sealed class InvisSpellScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (GmToggleSpells.Allowed(context, AccountSecurity.GameMaster) is not { } player)
        {
            return;
        }

        bool invisible = context.Spell.Id == 2763;
        player.GmInvisible = invisible;
        GmToggleSpells.Notify(player, invisible ? GmToggleSpells.InvisOn : GmToggleSpells.InvisOff);
        player.Map?.RefreshVisibility(player);
    }
}

/// <summary>
/// CooldownAll 29313 (vmangos spell_special.cpp:233-247): the caster's spell cooldowns are cleared (Unit::RemoveAllCooldowns; the global
/// cooldown is left alone, as there). Needs a GameMaster. Spell school lockouts, which vmangos clears as well, have no counterpart to clear here.
/// </summary>
[SpellScript(29313)]
public sealed class ClearAllCooldownsScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (GmToggleSpells.Allowed(context, AccountSecurity.GameMaster) is not { } player)
        {
            return;
        }

        foreach (InitialSpellCooldown cooldown in context.System.GetActiveCooldowns(player))
        {
            context.System.ClearCooldown(player, cooldown.SpellId);
        }
    }
}
