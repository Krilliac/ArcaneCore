# Reputation options

7 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `Reputation`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `CombatReactions` | bool | `true` | true \| false | World | start | Retail (true): combat attackability and creature aggro follow player reputation (at war, Hated guards, contested guards, forced reactions; Object.cpp:3608-3816). False keeps the template-only hooks. Needs Faction.dbc and FactionTemplate.dbc. |
| `FactionDbcPath` | string | `(empty)` | text | World | start | Optional developer-supplied build-5875 Faction.dbc. Absent means no reputation factions: SMSG_INITIALIZE_FACTIONS stays empty and nonzero NPC factions keep failing closed. |
| `PeaceForcedUsesEffectiveStanding` | bool | `false` | true \| false | World | start | Retail (false) lets forced peace be lifted by the RELATIVE standing only (ReputationMgr.cpp:334-336); true compares the effective rank including the race base. Deliberate deviation, default retail. |
| `RateGain` | float | `1` | a number, '.' is the decimal point | World | start | Multiplier on every reputation gain; the vmangos key `Rate.Reputation.Gain` (default 1, mangosd.conf.dist.in:2831). |
| `RateLowLevelKill` | float | `0.2` | a number, '.' is the decimal point | World | start | Multiplier on reputation gained from killing low-level creatures; the vmangos key `Rate.Reputation.LowLevel.Kill` (default 0.2, mangosd.conf.dist.in:2832). |
| `SendForcedReactions` | bool | `false` | true \| false | World | start | Send SMSG_SET_FORCED_REACTIONS when a forced-reaction aura applies or fades (default false). The layout is vmangos (u32 faction, u32 rank); gtker/wow_messages types the faction as a u16, so the width is unconfirmed by a real client. The forced reaction itself works on the server either way; only the client display of it needs the packet. |
| `SpilloverEnabled` | bool | `true` | true \| false | World | start | Retail (true): reputation_spillover_template applies (ReputationMgr.cpp:211-243). False switches every spillover off. |
