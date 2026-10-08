using ArcaneCore.Game.Loot;

namespace ArcaneCore.Game.Creatures;

public sealed partial class Creature
{
    /// <summary>
    /// vmangos <c>Creature::SetLootRecipient(nullptr)</c> (Objects/Creature.cpp:1583-1595): forget the tapping player and group and drop the
    /// tapped dynamic flags, as an evade does (<see cref="CreatureMapSystem.EnterEvadeMode"/>). The <c>.die</c> GM command clears it before
    /// the kill so nobody earns the corpse (UnitCommands.cpp HandleDieHelper).
    /// </summary>
    public void ClearLootRecipient()
    {
        LootTapPlayerGuid = default;
        LootTapGroup = null;
        SetUInt32(UpdateFields.UnitDynamicFlags,
            GetUInt32(UpdateFields.UnitDynamicFlags) & ~(LootService.UnitDynFlagTapped | LootService.UnitDynFlagTappedByPlayer));
    }
}
