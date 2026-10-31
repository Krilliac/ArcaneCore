using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Bots group up for content one bot cannot do (<see cref="PlayerbotGroupCoordinator"/>), end to end on the real world handlers and the
/// manual clock: the bots accept their quests from the giver like players, the coordinator matches them, the leader's invitations and
/// the members' acceptances are ordinary client packets, and the group does the content and disbands.
/// </summary>
public abstract class PlayerbotGroupWorldTestBase
{
    private protected const byte Human = 1, Dwarf = 3;
    private protected const byte Warrior = 1, Hunter = 3, Rogue = 4, Priest = 5, Mage = 8, Warlock = 9;
    private protected const uint LesserHeal = 2050, Fireball = 133, Smite = 585;
}
