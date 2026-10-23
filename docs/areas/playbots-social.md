# Playerbot social life: guilds, guild chat and greetings

`PlayerbotSocialFeature` (`src/ArcaneCore.Playerbots/Social/`, namespace `ArcaneCore.World.Playerbots.BotSocial`) lets
managed playerbots found guilds, fill them, rank their members, say a short line in guild chat and, when asked to, wave at real
players. Everything is configuration under `World:Playerbots:Social`, live through `.reload config`, and runs only while
`World:Playerbots:Enabled` is on. Every action is a real client packet run through the real world handler of the bot's own session
(the bot never reaches into server state), so the server's checks apply to a bot as they do to a player. Real players are never driven:
the feature only reads the packets of running, non-scripted bots, and a player is only ever the target of a greeting or the audience of
a guild line.

## Founding a guild

`World:Playerbots:Social:Guilds:Enabled` (on) lets bots found guilds; `World:Playerbots:Social:Guilds:MaxBotGuilds` (4, 0..64) bounds the
guilds led by a registered bot (lowering it disbands nothing). `World:Playerbots:Social:Guilds:Creation` picks the way:

* **ClientCreate**: the founder sends `CMSG_GUILD_CREATE` with a generated name (vmangos `GuildHandler.cpp:47-73`,
  `HandleGuildCreateOpcode`); the server answers synchronously, so success is the founder being guilded. Needs two unguilded bots of
  one team; the others are invited afterwards. The server must honour the packet (`World:Guild:AllowClientGuildCreate`).
* **Charter**: the founder buys a charter at a petitioner that is also a tabard designer (`CMSG_PETITION_BUY`, vmangos
  `PetitionsHandler.cpp:41-60`, 1000 copper), offers it to reserved signer bots (`CMSG_OFFER_PETITION`, `:340`), each signs
  (`CMSG_PETITION_SIGN`, `:218`) and the founder turns it in once the server says it is complete (`CMSG_TURN_IN_PETITION`, `:400`).
  `World:Guild:MinPetitionSigns` signatures are needed (the founder plus that many signers of one team). A charter founding survives a
  restart (state is read back from the petition store); one stalled for ten minutes releases its reservations.
* **Auto** (default): ClientCreate when `World:Guild:AllowClientGuildCreate` is on, otherwise Charter.

**Petitioner in reach.** A bot never walks to a petitioner (travel belongs to the brain's goals): a charter is bought only where a
petitioner and tabard designer is already inside the server's own interaction check (`QuestNpcServices.InteractableNpc`) from where the
bot stands, and the bot has the money. If no bot qualifies nothing happens this time.

At most one charter founding is in flight and attempts are at least five minutes apart. Founding is fail closed: it waits for the first
read of the bot registry, a name that does not pass the word filter and the guild-name rules (`PlayerbotGuildNames`) is never used, and
a refusal by the server (name taken, already guilded, no money) only ends that attempt. A new guild's master then sets the message of
the day from a template (`CMSG_GUILD_MOTD`), screened like every other line.

## Membership, caps and the invite policy

* `World:Playerbots:Social:Guilds:TargetSize` (25, 2..500) caps a bot guild's members **plus pending invitations**. A bot guild's
  master or an officer invites one eligible unguilded bot of its team at most every five seconds (`CMSG_GUILD_INVITE`), and a bot that
  refused is not asked again for five minutes.
* A bot answers every invitation it receives, even with `World:Playerbots:Social:Guilds:Enabled` off. An invitation of a guild the
  founding bots are building (expected) is accepted (`CMSG_GUILD_ACCEPT`); one from another managed bot that nobody expected is declined
  (`CMSG_GUILD_DECLINE`); one from a real player is accepted by `World:Playerbots:Social:Guilds:InvitePolicy` (`None`,
  `GuildOrFriends` (default), `Anyone`; the same rules as `World:Playerbots:Party:InvitePolicy`) and always for the names in
  `World:Playerbots:Party:Allowlist`. The same policy decides whether a bot signs a real player's charter.
* **Fail-closed declines.** With no invitation packet seen (the bounded capture queue may have dropped it) and no expectation, the
  answer is a decline; so it is when the inviter is offline, the guild name differs from the packet's, or the policy refuses.

## Ranks

The five default ranks (vmangos `Guild::CreateDefaultGuildRanks`, `Guild.cpp:176`): Guild Master, Officer, Veteran, Member, Initiate. A
bot guild's master promotes one member a minute at most (`CMSG_GUILD_PROMOTE`, checked by the server like a player's): first one officer
per ten members (the highest-level non-leader with a Member's level), then Initiates of level 10 become Members and Members of level 40
Veterans. Nobody is promoted past Officer, and a guild whose ranks were changed from the five defaults is left alone.

## Guild chat

`World:Playerbots:Social:GuildChat:Enabled` (on) and `World:Playerbots:Social:GuildChat:MessagesPerHour` (4, 0..60; 0 is silent).
Events queue **topics** per guild (at most four, the oldest dropped; one older than five minutes of world time is dropped unsaid):

| Topic | Cause | Said by |
|---|---|---|
| `guild.welcome` | a real player joined (not through a charter) | any bot but the newcomer |
| `guild.ding` | a bot member reached level 10, 20, ... | that bot |
| `guild.grats` | a real player reached such a level | another bot |
| `guild.boss` | a dungeon or raid boss (instance-bind flag or world-boss rank) was killed by a player: the guilds of the killer and its group | any bot |
| `guild.bgwin` | a bot won a battleground (`SMSG_BATTLEFIELD_WIN`), once per guild in ten minutes | any bot |
| `guild.chatter` | small talk, a chance per pass aiming at half of `MessagesPerHour` an hour | any bot |

A boss kill is remembered per guild, **map instance** and victim for ten minutes of world time (at most 4096 entries). A boss keeps the
same creature guid in every instance of its dungeon, so each instance's kill is remarked on once, and a respawning open-world boss again
after the window.

**Rate limits and the safety path.** A topic is said only when guild chat is on, `MessagesPerHour` is above zero, the bot chat service
is on (`World:Playerbots:Chat:Enabled`), a real player who can read guild chat is online (nothing is ever said to bots alone), the
guild's sliding hourly window has room (counted when the line is queued), and a bot member that may speak, is alive, out of combat,
not scripted and silent for two minutes exists. One guild line per guild per pass at most. The line goes through
`PlayerbotChat.TryAnnounce`: the same providers, spend caps, queue limit and output safety screening as every bot chat line
(`World:Playerbots:Chat:Safety`), with the built-in lines (`PlayerbotSocialTemplates`) when a player has not opted in under
`RequireOptIn`. A flagged sentence or name is never said and the next clean topic is tried; a refused line (budget, queue) is tried
again next pass.

## Emotes

`World:Playerbots:Social:Emotes:Enabled` (on by default). An idle bot (autonomous, in no party or bot group, alive, out of combat)
waves (`CMSG_TEXT_EMOTE`, `TEXTEMOTE_WAVE` = 101, vmangos `SharedDefines.h`) or greets in /say with a built-in `social.greet` line
(never a model, screened by the output filter) at a real player of its faction within 8 yards on the same map, alive, out of combat,
who has not ignored the bot. Limits, on world time: once per bot and player in 30 minutes, once per player in 10, once per bot in 5,
twelve in all per minute.

## Social memory

Every managed bot keeps a bounded memory of the players and bots it has dealt with (design: `docs/design/playerbot-social.md`, phase P1).
It only records, persists and colours the bot's speech; nothing it stores changes what the bot does yet.

**What is stored** (`playerbot_relation`, characters schema 64, one row per bot and other character, at most `MaxRelationsPerBot`,
default 256, per bot): affinity (-100..100), familiarity (encounters), trust (0..1000, 500 neutral), flags (role preferences, grouped, other
faction, rival), when it was last seen, a topic key (at most 32 characters) and a summary (at most 160). The topic is an intent key and the
summary is one of the bot's own built-in template lines: a player's words are never stored, and neither is anything a model wrote.
Each bot also has five stable traits from its guid (sociability, chattiness, generosity, curiosity, helpfulness; `PlayerbotTraits`).

**Events and deltas** (`World:Playerbots:Social:Memory`, all live, on by default; `PlayerbotSocialMemory.Note`): a chat exchange
(`AffinityPerChat`), grouped (`AffinityPerGroup`), a group's run succeeding (`AffinityPerGroupSuccess`), a wipe (+1, shared hardship), a
kick from a group (`AffinityPerKick`), a completed trade (`AffinityPerTrade`), helping or being helped (`AffinityPerHelp`), a chat
safety strike (`AffinityPerStrike`), a PvP kill either way (`AffinityPerKilledBy`, `AffinityPerKilledThem`; duels never count), and a
friendly or rude emote aimed at the bot, from either faction (`AffinityPerFriendlyEmote`, `AffinityPerRudeEmote`). Every event adds one
familiarity; a chat exchange or emote does so at most once per pair in `ChatFamiliarityCooldownMinutes`, and a friendly emote raises
affinity once per window too. Familiarity halves every `FamiliarityHalfLifeDays` since the pair was last seen (read lazily). Affinity at or
below `RivalAffinity` sets the Rival flag and above it clears it. Over the limit the least familiar, oldest relation goes, but never one with
affinity of 50 or more either way unless every one is like that.

**Where each event comes from**: chat lines heard by a bot and addressed to it (`PlayerbotChat.LineHeard`); the group coordinator (forming,
wipe, success); `GroupManager.MemberKicked` for kicks; the party AI for trades (the trader from the begin-trade packet or the bot's own
request, completion on trade status 8) and for text emotes (`SMSG_TEXT_EMOTE` naming the bot); the chat safety screen for strikes; the
brain's assist and heal-the-tagger paths for help; `PlayerbotKillObserver.PlayerKilled` for PvP kills. All of them go through
`PlayerbotSocialRecorder`, which never throws into game logic (fault site `playerbot.social-memory`).

**Persistence**: loaded at startup; a bot's rows are written by the checkpoint when its version changed, and on stop or drop (not under a
split-brain latch), off the world thread with the transient-busy retry. A failed write is a fault and a warning, never a blocked stop.

**Speech**: the bot's chat persona carries the relation with the asking player (met N times, liking band, preferred role, last topic) and the
bot's trait bands; the model prompt gets one line from it ("You know Sam: met 4 times, you like them, last time: greeting."), only when a
relation exists. The built-in greeting says "welcome back" at familiarity 3 or more with affinity 0 or more, and is curt at affinity -40 or less.

**GM command**: `.playerbot social $bot` (Administrator, read-only) shows the traits and the ten best-known relations.

## Cost and persistence

One pass every second of world time. A pass does a constant number of dictionary lookups per bot and per bot guild (the bot list is
gathered once per pass and only when something needs it; an event handler's pass gathers none), drains only the guild-invite,
charter-signature and battleground-win packets of each bot's capture queue, and makes at most one chat admission per guild. The rate
limiters and the boss and battleground-win maps are bounded and swept; a pass that throws is logged (the first five in full) and the world
carries on. Guilds, ranks, petitions and members persist through the existing social system (the social memory has its own table, above), and the feature's own
state is in memory or read back from it.

## Tests

`tests/ArcaneCore.World.Tests/Playerbots/Social/`: `PlayerbotGuildFoundingWorldTests`, `PlayerbotGuildInviteWorldTests`,
`PlayerbotGuildChatWorldTests`, `PlayerbotGuildRulesTests`, `PlayerbotGuildNamesTests`, `PlayerbotSocialRateLimiterTests`,
`PlayerbotAnnounceTests`.
