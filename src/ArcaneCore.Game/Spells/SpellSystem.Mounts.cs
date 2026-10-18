using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// Raised on the world thread when a player mounted through <see cref="Locomotion.MountService.Mount"/>, after the mount display is set
    /// and before SMSG_MOUNTRESULT; the pets area greys the player's pet bar out (vmangos Player::Mount, Player.cpp:18204-18216).
    /// </summary>
    public event Action<Player>? PlayerMounted;

    /// <summary>
    /// Raised on the world thread when a player dismounted through <see cref="Locomotion.MountService.Unmount"/>, after the mount display is
    /// cleared and before SMSG_DISMOUNTRESULT; the pets area gives the pet its bar back, or brings back the pet that was put away
    /// (vmangos Player::Unmount, Player.cpp:18248-18251).
    /// </summary>
    public event Action<Player>? PlayerDismounted;

    internal void NotifyMounted(Player player) => PlayerMounted?.Invoke(player);

    internal void NotifyDismounted(Player player) => PlayerDismounted?.Invoke(player);
}
