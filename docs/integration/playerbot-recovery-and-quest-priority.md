# Recovery and quest-travel decisions

When death is first observed, a managed bot drops its transient enemy, attack, navigation,
loot-collection and rest intent. The real quest journal, inventory and spell cooldowns remain
owned by the ordinary game systems. Ghosts continue acknowledging server movement orders;
release and corpse reclaim still pass through normal handlers and reclaim delays.

While alive and out of combat, eligible offscreen quest travel precedes optional idle grinding.
Actual combat victims, active casts, completed quest returns, supplies, nearby interactions and
useful trainers retain their existing priority. Destination discovery uses the same explicit quest
allowlist and live eligibility as travel. It never accepts or completes a quest itself.

Travel attempts remain bounded to one path plan per decision. Other unblocked spawns can be tried
on subsequent decisions; when every eligible candidate is blocked, a two-second backoff permits
a later retry. An unreachable destination does not permanently own the bot's decisions.

The inspection extension reports live position, death state and corpse facts. A distance is
reported only within the same map instance and when finite. This distinguishes transient ghost
recovery from stale saved character state; it is not evidence of successful quest progression.
