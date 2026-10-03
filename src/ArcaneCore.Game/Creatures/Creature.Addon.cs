using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>The per-spawn <c>creature_addon</c> fields.</summary>
public sealed partial class Creature : Unit, ICombatCreature
{
    /// <summary>creature_addon: mount, stand state, sheath state, emote state (vmangos Creature::LoadCreatureAddon).</summary>
    private void ApplyAddon()
    {
        CreatureAddon? addon = Spawn is null ? null : Content.FindAddon(Spawn.Guid);
        SetUInt32(UpdateFields.UnitFieldMountdisplayid, addon?.MountDisplayId ?? 0);
        StandState = (StandState)(addon?.StandState ?? 0);
        if (addon is not null)
        {
            SetByte(UpdateFields.UnitFieldBytes2, 0, addon.SheathState);
        }

        SetUInt32(UpdateFields.UnitNpcEmotestate, addon?.EmoteState ?? 0);
    }
}
