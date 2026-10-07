using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// CMSG_SELF_RES (build 5875: empty): cast the spell in PLAYER_SELF_RES_SPELL and clear the field, see
    /// <see cref="Death.Resurrection.SelfResurrection.Use"/>. Returns whether the spell was cast.
    /// </summary>
    public bool TrySelfResurrect(Player player) => Death.Resurrection.SelfResurrection.Use(this, player);
}
