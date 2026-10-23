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
default 256, per bot, plus the war-only rows below, four times that in all): affinity (-100..100), familiarity (encounters), trust (0..1000, 500 neutral), flags (role preferences, grouped, other
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
brain's assist and heal-the-tagger paths for help; the kill observer of the bot's own map for PvP kills (each brain registers its PvP callback there; there is no process-wide
event, which kept every disposed world reachable). All of them go through
`PlayerbotSocialRecorder`, which never throws into game logic (fault site `playerbot.social-memory`).

**Help, bystanders and the call for help** (`PlayerbotFightSocial`, one per bot, read once a second while the bot is in combat):

- *Help.* Every player (or player's pet) on the threat list of a creature that is fighting the bot, with threat above 0, helped it: the bot
  records HelpedBy toward the helper and, when the helper is a managed bot, the helper records HelpedThem toward the bot, once per helper
  and fight (`PlayerbotSocialRecorder.NoteHelp`, shared with the assist and heal-the-tagger paths so one assist is never counted twice; a
  victim bot leaving combat ends its fight). Healers and taunters are on the threat list too. Buffs without threat are not seen.
- *Low-health window.* When the bot's health first drops below 40% in a fight it samples up to 16 nearby bystanders once: alive, same
  faction, out of combat, at least `BystanderMinHealthPercent` healthy, within `BystanderRadiusYards`, not in a duel, level at least the
  bot's minus 5. It re-checks them every second.
- *Call for help or go alone.* If the fight looks lost (the strongest attacker has a higher health fraction than the bot) and a candidate
  is askable (not disliked, not a rival, same faction), the bot decides once per fight with a hash of its guid and the fight start: it asks
  with probability `CallForHelpBaseChance + CallForHelpSociabilityWeight * sociability + 0.002 * best affinity`. Asking is one yell
  through the chat service (`TryAnnounce`: limits, safety screen and disclosure rules apply) and the targeted help text emote at the nearest
  asked unit. Going alone says nothing. `CallForHelpEnabled` off means the bot never asks (and so blames nobody).
- *Watched me die.* If the bot dies in the window (not a duel, window at least `BystanderMinSeconds`), each asked candidate that still
  qualifies at death and never helped gets WatchedMeDie (`AffinityPerWatchedDie`). A bot that went alone blames nobody.
- *Answering* (`AnswerHelpEnabled`): the answer probability is `helpfulness * (1 + affinity / 100)` clamped to 0..1 as a hash of the
  answering bot, the asker and the call (`PlayerbotCallForHelp.Answers`). The function is in place and tested, but the answer is not wired to
  the brain's assist lane yet (that moves to P2); nothing acts on a help emote today.

**Forgiveness, caps, bad group behaviour, reliability and War Mode** (all in `PlayerbotSocialMemory`, all live):

- *Drift.* Affinity drifts toward 0 on the same lazy whole-day clock as familiarity, with half-life `AffinityHalfLifeDays` (14), or
  `RivalHalfLifeDays` (45) for an entry flagged Rival, so a grudge fades slower. A read returns the drifted value and the Rival flag the
  drifted affinity implies; a write folds the drift into the stored value first.
- *Apology.* A player or bot the bot has negative affinity toward says sorry to it (an addressed line containing "sorry", "my bad",
  "apologies" or "apologize", or the sorry text emote aimed at it by name) and gains `AffinityPerApology` (10), at most once per pair per UTC
  day (the event is `Apologized`; it does nothing for a stranger or someone the bot likes). The line is matched and dropped, never stored.
  "Sorry not sorry" and the other negated forms are not apologies. The apology of a line is recorded before the chat exchange of the same
  line, so the exchange's own affinity cannot lift a small grudge to zero first. The chat-familiarity cooldown (`ChatFamiliarityCooldownMinutes`)
  is kept in memory like the day counters, so a restart also starts it afresh.
- *Daily cap.* A pair gains at most `MaxDailyAffinityGainPerPair` (20; 0 is unlimited) affinity per UTC day from all sources together; the rest
  is dropped. Negative changes are never capped. The day counters are kept in memory only: a restart gives every pair a fresh day.
- *Bad group behaviour.* `NinjaLoot` (`AffinityPerNinjaLoot`, -8): a groupmate won a Need roll (SMSG_LOOT_ROLL_WON, taken by the party AI) on an
  item it could not use (no upgrade, bag upgrade or learnable item by the same test the bot votes with) while the bot had voted Need too.
  `KillSteal` (`AffinityPerKillSteal`, -6): the bot was engaged with a mob nobody outside its group had tagged (the bot on the mob's threat
  list, the mob untagged or tagged by the bot or a groupmate), and then a player outside the group took the tag while the bot was still on that
  threat list: the other side started second. Once per engagement. A tag taken while the bot was still walking up, on a mob that evaded or
  respawned, or by a groupmate is never a steal, and neither is a tag that stays with a former groupmate after the group changed under it (the
  tapper is remembered). Tags and groups are sampled once per think, so a groupmate who tags and leaves the group within one think can still be
  blamed. `AbandonedRun` (`AffinityPerAbandonedRun`, -10):
  a member left the server group while a coordinator run was in progress; not after the run finished, not when the leader kicked the member
  and not when the leader's group was disbanded. Each other bot member of the group records it.
- *Reliability.* Every bot member of a coordinator group that finished successfully gains `TrustPerGroupSuccess` (+30 permille) toward every
  other member, and every other bot member loses `TrustPerFirstDeathInWipe` (-30) toward the member seen dead first in a wipe. Trust stays
  within 0..1000. On a success and on a wipe each bot member also stores the role (tank, healer or damage) every other bot member played,
  replacing the one it stored before; the role of a real player is not known and is left alone.
- *War Mode* (`World:Playerbots:Social:WarMode`, **off by default**; a server mode for massive world PvP) is a separate layer. The normal layer
  (affinity, trust, flags, drift, daily cap, apology limit) is always updated with the normal rules and War Mode never writes to it. Each
  relation also has a war layer (`WarAffinity`, `WarFlags`: Rival and a seeded marker, stored in the same row). While `Enabled`, every event is
  also applied to the war layer under the war rules: negative changes multiplied by `NegativeMultiplier` (3); with `NeverForget` negative war
  affinity does not drift and the war Rival flag is not cleared by drift; with `RemoveCaps` the war layer has no daily cap and no
  once-a-day apology limit. The normal rules alone decide what a full memory evicts, so the normal layer is the same with or without War Mode;
  with `RemoveCaps` and War Mode on, a relation they evict while the war layer holds a grudge against it stays as a *war-only* row (flag
  `WarOnly` in the stored flags, normal fields blank): every normal read treats it as absent and a normal event starts it afresh as a new
  relation, while a war read (War Mode on) sees it. A bot holds at most four times `MaxRelationsPerBot` rows in all (the oldest war-only row
  goes first, logged once). The war layer is seeded from the normal affinity the first time an event reaches it. Every decision (speech facts, call for help) reads
  `PlayerbotSocialMemory.EffectiveAffinity` and `IsRival`, which return the war layer only while War Mode is on. Switching it off makes the
  normal layer effective at once; the war layer takes no events while it is off but is not frozen: its drift still follows the clock, so
  positive war affinity halves on its half-life while dormant and, with `NeverForget` off, so does negative war affinity (with it on, a
  war grudge keeps its value). It comes back as it has drifted when War Mode is switched on again, war-only rows included, unless
  `ClearOnDisable` is set, in which case the first checkpoint after the switch starts a background task (one at a time, never inside a bot's own save or stop) that wipes the war layers and deletes the war-only rows of every bot, running or not, and writes each bot it changed once. The keys are live. War Mode changes what the bots remember, not the rules of the game: it
  never force-flags anyone for PvP and a player's opt-out still applies (the rivalry that acts on this memory is a later phase).

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
