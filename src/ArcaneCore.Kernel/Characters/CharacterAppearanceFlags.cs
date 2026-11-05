namespace ArcaneCore.Kernel.Characters;

/// <summary>
/// The CHARACTER_FLAG_* bits of <see cref="CharacterRecord.CharacterFlags"/> that this server stores: vmangos Player.h:298-299
/// (0e3ff01) CHARACTER_FLAG_HIDE_HELM = 0x400 and CHARACTER_FLAG_HIDE_CLOAK = 0x800, the same values as PLAYER_FLAGS_HIDE_HELM and
/// PLAYER_FLAGS_HIDE_CLOAK (Player.h:325-326; cmangos mangos-classic ff53dfc Player.cpp:111-112).
/// </summary>
public static class CharacterAppearanceFlags
{
    public const uint HideHelm = 0x00000400;
    public const uint HideCloak = 0x00000800;

    /// <summary>The bits a character save owns; other bits of the stored word are left as they are.</summary>
    public const uint Mask = HideHelm | HideCloak;
}
