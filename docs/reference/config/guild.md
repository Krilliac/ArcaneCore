# Guild options

8 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `World:Guild`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `AllowClientGuildCreate` | bool | `true` | true \| false | World | start | Honour CMSG_GUILD_CREATE (vmangos HandleGuildCreateOpcode, GuildHandler.cpp:47-72, honours it). Default on, as vmangos; an operator may turn it off to found guilds through charters only. |
| `CharterSpamPatterns` | List<string> | `(empty)` | a list: numbered keys 0, 1, 2, ...; each value is text | World | start | Antispam patterns for guild and charter names (vmangos AntispamInterface::filterMessage at the charter purchase and at Guild::Create; vmangos ships no implementation, so it filters nothing, and neither does the empty default). A name that contains a pattern, compared without case and spaces, is refused as an invalid guild name (`PatternCharterAntispamFilter`). Bound from World:Guild:CharterSpamPatterns (a list). |
| `DeleteRankMovesMembers` | bool | `false` | true \| false | World | start | Deleting a rank moves its members to the new lowest rank. Default false: vmangos Guild::DelRank (Guild.cpp:696-707) leaves them on the dead rank id (name "&lt;unknown&gt;", no rights) until the next load clamps them (Guild.cpp:458-460). |
| `KickOnOversizedText` | bool | `true` | true \| false | World | start | Disconnect a client that sends over-long guild text (name, MOTD, info, notes, rank names), as vmangos does through ProcessAnticheatAction (GuildHandler.cpp:58-62,470-474,518-522, 554-558,580-584,600-604). The retail client never sends such text. |
| `MinCharterNameLength` | int | `2` | a whole number, -2147483648 to 2147483647 | World | start | MinCharterName (mangosd.conf.dist.in:1299, default 2, World.cpp:625 clamps to 2..24). |
| `MinPetitionSigns` | int | `9` | a whole number, -2147483648 to 2147483647 | World | start | MinPetitionSigns (mangosd.conf.dist.in:1341, default 9, World.cpp:666 clamps to 0..9). |
| `RealmZone` | int | `1` | a whole number, -2147483648 to 2147483647 | World | start | vmangos RealmZone (RealmZone.h:23-61, mangosd.conf RealmZone, default 1 = development: any language), the zone whose scripts StrictCharterNames bit 0x2 accepts. The character creation and pet name options carry their own copy of the setting. |
| `StrictCharterNames` | int | `0` | a whole number, -2147483648 to 2147483647 | World | start | StrictCharterNames (mangosd.conf.dist.in:1296, default 0 = any single script). Bit 0x1 accepts basic Latin; bit 0x2 accepts the scripts of the realm zone (`RealmZone`, vmangos GetRealmLanguageType with create false). |
