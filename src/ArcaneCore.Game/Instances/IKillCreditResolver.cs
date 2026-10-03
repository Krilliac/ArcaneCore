using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances;

/// <summary>
/// Who gets the credit for a creature kill inside an instance (vmangos Unit.cpp:1253-1260:
/// <c>GetCharmerOrOwnerPlayerOrPlayerItself()</c>, else the tapper when the victim killed itself).
/// The default credits the killer when it is a player; the pets and loot-tap lanes replace it.
/// </summary>
public interface IKillCreditResolver
{
    /// <summary>The player credited with killing <paramref name="victim"/>, or null when nobody is.</summary>
    Player? Resolve(Unit? killer, Creature victim);
}

/// <summary>Credits the killer if it is a player (the behaviour before owners and tappers exist).</summary>
public sealed class PlayerKillCreditResolver : IKillCreditResolver
{
    public static PlayerKillCreditResolver Instance { get; } = new();

    public Player? Resolve(Unit? killer, Creature victim) => killer as Player;
}
