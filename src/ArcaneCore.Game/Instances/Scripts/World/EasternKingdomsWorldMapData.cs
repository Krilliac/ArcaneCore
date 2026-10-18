using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.World;

/// <summary>
/// cmangos <c>world_map_eastern_kingdoms</c> (AI/ScriptDevAI/scripts/eastern_kingdoms/world_eastern_kingdoms.cpp, world_eastern_kingdoms.h),
/// the parts a ported script needs: the single-creature store of OnCreatureCreate (Jonathan, Wrynn, Bolvar, Prestor, Windsor, Falstad
/// Wildhammer, Short John Mithril) and <c>event_wildhammer_message</c> (hinterlands.cpp:357-374, event 8608, The Final Message to the
/// Wildhammer, q7843). OnEventHappened starts Short John Mithril's Gurubashi Arena run (<see cref="Creatures.Scripts.ShortJohnMithrilAI"/>). The Stranglethorn Fever waves are the vmangos creature script (<see cref="Creatures.Scripts.WitchDoctorUnbagwaAI"/>) because the cmangos spawn groups are not in this DB; the Scourge Invasion circles are not ported here.
/// SetData is empty in cmangos, so the single encounter slot that ScriptedInstance requires is never used.
/// </summary>
public sealed class EasternKingdomsWorldMapData : WorldMapData
{
    public const uint MapEasternKingdoms = 0;

    /// <summary>world_eastern_kingdoms.h.</summary>
    public const uint NpcJonathan = 466, NpcWrynn = 1747, NpcBolvar = 1748, NpcPrestor = 1749, NpcWindsor = 12580,
        NpcFalstadWildhammer = 5635, NpcShortJohnMithril = 14508;

    /// <summary>The event the Final Message to the Wildhammer sends when it is planted by the Aerie Peak well (scripted_event_id 8608).</summary>
    public const uint EventWildhammerMessage = 8608;

    /// <summary>
    /// YELL_FALSTAD_INVADERS: cmangos says creature_ai_texts -27, which ClassicDB z2815 does not have. Its line, "Invaders! Invaders! We're
    /// under attack, boys! Get 'em!", is broadcast_text 10009 (a yell), so that is the id used here.
    /// </summary>
    public const int YellFalstadInvaders = 10009;

    public EasternKingdomsWorldMapData(Map map)
        : base(map, 1) // ScriptedInstance needs at least one slot; nothing uses it
    {
    }

    public override void OnCreatureCreate(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        switch (creature.Template.Entry)
        {
            case NpcJonathan:
            case NpcWrynn:
            case NpcBolvar:
            case NpcPrestor:
            case NpcWindsor:
            case NpcFalstadWildhammer:
            case NpcShortJohnMithril:
                StoreCreature(creature);
                break;
        }
    }

    /// <summary>
    /// OnEventHappened (world_eastern_kingdoms.cpp): when the Gurubashi Arena event (16) starts, Short John Mithril sets off on his run.
    /// </summary>
    protected override void OnEventHappened(ushort eventId, bool active, bool resume)
    {
        if (eventId == Creatures.Scripts.ShortJohnMithrilAI.GameEventGurubashiArena && active
            && GetSingleCreatureFromStorage(NpcShortJohnMithril)?.AI is Creatures.Scripts.ShortJohnMithrilAI shortJohn)
        {
            shortJohn.StartBootyRun();
        }
    }

    public override uint GetData(uint type) => 0;

    public override void SetData(uint type, uint data)
    {
    }

    /// <summary>
    /// ProcessEventId_WildhammerMessage (hinterlands.cpp:363-374). The event is always handled, so its DB script never runs (it has none).
    /// A player who plants the message is flagged for PvP, and Falstad Wildhammer yells the alarm at them if he is in storage. Other sources
    /// do nothing.
    /// cmangos also calls RemoveAurasWithInterruptFlags(AURA_INTERRUPT_FLAG_PVP_ACTIVE_CANCELS). That is skipped here: ArcaneCore's 1.12
    /// interrupt bits (<see cref="Spells.AuraInterruptMask"/>) have no such bit, so no aura would be removed.
    /// </summary>
    public override bool OnSpellEvent(Unit caster, uint eventId)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (eventId != EventWildhammerMessage)
        {
            return false;
        }

        if (caster is not Player player)
        {
            return true;
        }

        MapCombat.UpdatePvp(player, true);
        if (GetSingleCreatureFromStorage(NpcFalstadWildhammer) is { } falstad && Instance.FindUpdater<CreatureMapSystem>() is { } creatures)
        {
            creatures.SayText(falstad, YellFalstadInvaders, player);
        }

        return true;
    }
}
