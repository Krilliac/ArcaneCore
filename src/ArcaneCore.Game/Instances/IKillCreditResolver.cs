using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Instances;

/// <summary>
/// Who gets the credit for a creature kill inside an instance (vmangos Unit.cpp:1253-1260:
/// <c>GetCharmerOrOwnerPlayerOrPlayerItself()</c>, else the tapper when the victim killed itself).
/// The default credits the player behind the killer; the loot-tap lane can replace it to add the tapper.
/// </summary>
public interface IKillCreditResolver
{
    /// <summary>The player credited with killing <paramref name="victim"/>, or null when nobody is.</summary>
    Player? Resolve(Unit? killer, Creature victim);
}

/// <summary>
/// Credits the player behind the killer: the killer itself when it is a player, else the player that charms or owns it
/// (a pet, totem, guardian or charmed creature; vmangos <c>GetCharmerOrOwnerPlayerOrPlayerItself</c>), else the player a
/// stand-in unit reports through <see cref="IPlayerControlledUnit"/>. A creature that killed itself would credit its tapper
/// in vmangos; creatures keep no tap list yet, so such a kill credits nobody.
/// </summary>
public sealed class PlayerKillCreditResolver : IKillCreditResolver
{
    public static PlayerKillCreditResolver Instance { get; } = new();

    public Player? Resolve(Unit? killer, Creature victim)
        => killer is null ? null : killer.GetCharmerOrOwnerPlayerOrSelf() ?? DuelRules.ControllingPlayer(killer);
}
