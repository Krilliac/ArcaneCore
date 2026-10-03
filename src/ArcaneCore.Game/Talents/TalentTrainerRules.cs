using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Talents;

/// <summary>Who may reset talents at which trainer.</summary>
public static class TalentTrainerRules
{
    /// <summary>Lowest level that may reset talents (vmangos Creature::CanTrainAndResetTalentsOf).</summary>
    public const int MinimumLevel = 10;

    /// <summary>
    /// vmangos Creature::CanTrainAndResetTalentsOf (Creature.cpp:1523-1527): level 10 or more, a class trainer
    /// (trainer_type 0) of the player's own class. Both the gossip offer (Player.cpp:12036-12039) and, in
    /// mangos-classic, the wipe-confirm handler (SkillHandler.cpp:51) use it.
    /// </summary>
    public static bool CanTrainAndResetTalentsOf(Player player, NpcInfo npc)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(npc);
        return player.Level >= MinimumLevel
            && npc.TrainerType == TrainerType.Class
            && (byte)player.Class == npc.TrainerClass;
    }
}
