# Food and drink heartbeat visuals — 2026-10-04

Units retain a 5200 ms heartbeat timer across aura casts: vmangos Object.h uses
400 ms batching × 13. The map advances active unit timers once per update,
reuses a mutation-safe snapshot, and catches up elapsed intervals. Settlement
holds freeze the timer; removed or transferring units are outside active maps.

Generic-family food/drink holders carrying StandingCancels send eat emote 7 on
application. Their unit heartbeat emits food kit 406 and/or drink kit 438 using
SMSG_PLAY_SPELL_VISUAL, full eight-byte target GUID followed by a four-byte kit.
The emote payload is a four-byte emote followed by the full target GUID. Both
paths use the normal observer/self send seam, including creature targets.

Source Unit::TriggerAuraHeartbeat invokes each aura, and the aura helper scans
the whole spell: a two-effect food/drink holder emits both kits twice per unit
heartbeat. Aura removal emits no visual. This unit timer is independent of
resource regeneration and aura periodic intervals. Weak subscriptions belong
to each module instance and holders are requalified on every heartbeat.

Pinned references: Object.h:74, Common.h:236, Object.cpp:1525-1541,
Unit.cpp:347-364, SpellAuras.cpp:1583-1624,7388-7391, and the vanilla spell
visual packet declaration/writer. Game tests cover elapsed unit age before cast,
catch-up, exact GUID/kit payloads, removal, qualification and observer visibility.
World coverage sends CMSG_USE_ITEM, reads exact emote/visual packets, and stands
through the client request to remove the holder. OBS modifier visuals, other
heartbeat proc families and original-client rendering remain separate work.
