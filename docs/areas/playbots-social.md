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
  `GuildOrFriends` (default), `Anyone`, `Affinity`; the same rules as `World:Playerbots:Party:InvitePolicy`) and always for the names in
  `World:Playerbots:Party:Allowlist`. The same policy decides whether a bot signs a real player's charter.
* **Invitations by affinity** (`World:Playerbots:Social:Guilds:AffinityInvites`, on). A bot-led guild's master or an officer (a guild whose
  master is a real player is never recruited for by the bots) also invites a
  guildless same-team character, player or bot, that it knows and likes: affinity at least `GuildInviteAffinity` (60) and familiarity at
  least `GuildInviteMinFamiliarity` (8), never a rival or a game master, and at most one invitation per character per
  `GuildInviteCooldownDays` (7), within `TargetSize` as above. A guildless bot accepts an invitation, from a player or a bot, whose sender it
  likes: affinity at least `GuildAcceptAffinity` (30) and no rival (affinity read through `EffectiveAffinity`); otherwise the rules above
  decide. The operator's `InvitePolicy` comes first: with `None` a bot declines every guild invitation of a player or bot it was not
  expecting, however much it likes the inviter (only the `World:Playerbots:Party:Allowlist` names still get in); the affinity acceptance
  widens `GuildOrFriends`, `Anyone` and `Affinity`, never `None`. With the switch off bots invite and accept as before.
* **Fail-closed declines.** With no invitation packet seen (the bounded capture queue may have dropped it) and no expectation, the
  answer is a decline; so it is when the inviter is offline, the guild name differs from the packet's, or the policy refuses.

## Friend and ignore lists (`World:Playerbots:Social:Lists`, live)

Bots keep real friend and ignore lists from their social memory, through the same client packets a player sends, so the server's own
rules (50 friends, 25 ignores, same team, no self) apply. Each bot makes at most one change per `ChangeSpacingSeconds` (60), by
priority: ignore, un-ignore, un-friend, friend; a few bots are looked over per pass.

| Change | Packet | When |
|---|---|---|
| friend | `CMSG_ADD_FRIEND` | affinity at least `FriendAffinity` (50), familiarity at least `FriendMinFamiliarity` (5), not a rival, not ignored |
| un-friend | `CMSG_DEL_FRIEND` | affinity below `UnfriendAffinity` (20) |
| ignore | `CMSG_ADD_IGNORE` | affinity at or below `IgnoreAffinity` (-70), or a rival that earned a chat safety strike; never a game master |
| un-ignore | `CMSG_DEL_IGNORE` | affinity above `UnignoreAffinity` (-30) and not a rival with a strike |

`Enabled` switches the lane off. The `Affinity` invite policy of `World:Playerbots:Party:InvitePolicy` reads these lists (a friend is
accepted) next to the memory.

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
| `guild.boss` | a dungeon or raid boss (instance-bind flag or world-boss rank), or a continent boss of the world-boss catalogue, was killed by a player: the guilds of the killer and its group | any bot |
| `guild.bgwin` | a bot won a battleground (`SMSG_BATTLEFIELD_WIN`), once per guild in ten minutes | any bot |
| `guild.chatter` | small talk, a chance per pass aiming at half of `MessagesPerHour` an hour | any bot |

A boss kill is remembered per guild, **map instance** and victim for ten minutes of world time (at most 4096 entries). A boss keeps the
same creature guid in every instance of its dungeon, so each instance's kill is remarked on once, and a respawning open-world boss again
after the window.

**World bosses on the continents (kill talk).** A kill on a continent is remarked on only when the creature is in the group coordinator's world-boss catalogue (`World:Playerbots:Groups:WorldBosses`, see `docs/areas/playbots-groups.md`, World-boss raids): rank 3 alone is not enough, because the faction leaders and Archbishop Benedictus are rank 3 on the continents. The coordinator publishes a frozen snapshot of the catalogue's entries that the map threads read (`PlayerbotGroupCoordinator.IsWorldBossEntry`); with no catalogue wired, or before it is built, a continent kill stays silent. The place named is the zone the boss stood in, from the area table (the map's name where the area is not known), where a dungeon boss names the dungeon; the guild line and the conversations' boss news (`BotChatEventKind.BossKill`) both use it (`PlayerbotBossKills.PlaceOf`). The scope is dungeon parity: any player's kill of a catalogued boss makes the guild bots of the killer and its group talk, whether or not a bot raid was involved (the alternative, only a group with a bot member, is an open decision). The zone lookup's fault site is `playerbot.social.bosskill`.

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
kick from a group (`AffinityPerKick`), a completed party-AI trade window in which the bot received at least 100 copper of item vendor value (gold is not counted) or gave something (`AffinityPerTrade`), helping or being helped (`AffinityPerHelp`), a chat
safety strike (`AffinityPerStrike`), a PvP kill either way (`AffinityPerKilledBy`, `AffinityPerKilledThem`; duels never count), and a
friendly or rude emote aimed at the bot, from either faction (`AffinityPerFriendlyEmote`, `AffinityPerRudeEmote`). Every event adds one
familiarity; a chat exchange or emote does so at most once per pair in `ChatFamiliarityCooldownMinutes`, and a friendly emote raises
affinity once per window too. Familiarity halves every `FamiliarityHalfLifeDays` since the pair was last seen (read lazily). Affinity at or
below `RivalAffinity` sets the Rival flag and above it clears it. Over the limit the least familiar, oldest relation goes, but never one with
affinity of 50 or more either way unless every one is like that.

**Where each event comes from**: chat lines heard by a bot and addressed to it (`PlayerbotChat.LineHeard`); the group coordinator (forming,
wipe, success); `GroupManager.MemberKicked` for kicks; the party AI for trades (the trader from a begin-trade packet of the master or a group member, or the bot's own
request; a stranger's begin-trade is not answered and sets nothing, and any close status forgets the window; completion on trade status 8) and for text emotes (`SMSG_TEXT_EMOTE` naming the bot); the chat safety screen for strikes; the
brain's assist and heal-the-tagger paths for help; `PlayerbotPvpKillFeed` for PvP kills (the feature hears `MapCombat.UnitKilled` of every map of its own world, so a kill is
recorded exactly once even right after a teleport or a brain replacement; no process-wide event, which kept every disposed world reachable). All of them go through
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
  never force-flags anyone for PvP and a player's opt-out still applies (the first rivalry that acts on this memory is Rivalry (P6a) below: emotes only; hunting parties are a later phase).

**Persistence**: loaded at startup; a bot's rows are written by the checkpoint when its version changed, and on stop or drop (not under a
split-brain latch), off the world thread with the transient-busy retry. A failed write is a fault and a warning, never a blocked stop.

**Speech**: the bot's chat persona carries the relation with the asking player (met N times, liking band, preferred role, last topic) and the
bot's trait bands; the model prompt gets one line from it ("You know Sam: met 4 times, you like them, last time: greeting."), only when a
relation exists. The built-in greeting says "welcome back" at familiarity 3 or more with affinity 0 or more, and is curt at affinity -40 or less.

**GM command**: `.playerbot social $bot` (Administrator, read-only) shows the traits and the ten best-known relations.

## Helping (`World:Playerbots:Social:Helping`, live)

Design: `docs/design/playerbot-social.md` 4.4 and 4.9 (phase P2). Everything here goes through the bot's own session and the existing
handlers (`CMSG_ATTACKSWING`, `CMSG_CAST_SPELL`); the lane that decides is `PlayerbotHelpLane` (social pass, world thread) and the lane that
acts is the bot's own think (`PlayerbotBrain.Help.cs`), so a helping bot walks, fights and heals through the code every other bot uses.

**Answering a call for help** (`AnswerHelp`, on by default). A bot that is asked decides with `PlayerbotCallForHelp.Answers`: its
helpfulness (a stable trait of its guid) scaled by how it feels about the asker (`1 + affinity / 100`, through `EffectiveAffinity`), one hash
roll per bot, asker and 15-second bucket. It is asked in two ways: a text emote `/helpme` aimed at it (the party intake sees
`SMSG_TEXT_EMOTE`), or a help yell or say (the whole word "help") that the bot hears, from a same-faction player or bot within
`World:Playerbots:Social:Memory:BystanderRadiusYards`. Only a *free* bot answers: its own brain drives it (not a party, not a coordinator group, so never a dungeon run),
it is alive, out of combat, above 70% health, not in a duel, and on a low-priority goal (explore, quest, grind, vendor, train, home, mount;
never combat, rest, recover, retreat, battleground). The asker must be alive, in a fight, and neither disliked (affinity below 0) nor a rival.
On yes the bot takes the asker's nearest attacker through the assist lane that `DropIfSomeoneElses` uses (`PlayerbotContention.BeginAssist`): the
attack is a normal attack, and a priest, paladin, druid or shaman heals the asker first (`TryHealTagger`). The help is recorded once as
`HelpedThem` / `HelpedBy` (`PlayerbotSocialRecorder.NoteHelp` counts a pair once per fight). On no it stays and, about one time in six and never
to someone it dislikes, says "Sorry, can't" through the announcement path. A bot decides about one asker once a minute.

**The trouble scan** (`World:Playerbots:Social:Helping:Enabled`, `TroubleRadiusYards`). Each pass looks at up to 24 free bots, round robin, each at most once per 5 seconds, and
at the same-faction players it sees within `TroubleRadiusYards` (default 30; the sight list is capped). A player is *in trouble* when it is
alive, in a fight (not a duel) and below 40% health, or a caster below 20% mana that is moving (running). The first sighting starts an episode
(it ends after 20 seconds without trouble); the bot's yes or no is one hash roll of the bot, the victim and the episode start, scaled by
helpfulness and by `1 + affinity / 100`, so the answer is the same on every pass of one fight and a rival is never helped. The bot must be free
(see above) and take the victim's attacker under the flag rule below; the help is the same assist lane as answering a call.

**Resurrecting** (`ResurrectStrangers`). A priest, paladin, druid or shaman that is free and sees a same-faction player dead within reach (25 yards,
unit or, once released, the corpse) rolls once per corpse and casts its resurrection spell (the group AI's `FindResurrectionSpell`: highest
rank, reagents carried). A dead player the bot owes a favour (gratitude, below) bypasses the roll. A flagged corpse is not raised by an unflagged
bot. A bot that is raised by a same-faction player it does not hold as a rival accepts (`OnResurrectRequest`; a group member is accepted as before).

**Buffing** (`BuffStrangers`, `BuffCooldownMinutes`). A free priest (Power Word: Fortitude), druid (Mark of the Wild), mage (Arcane Intellect) or
paladin (Blessing of Might) that passes a same-faction player out of combat within 25 yards offers the buff with a small hash chance, once per
bot and stranger per `BuffCooldownMinutes` (default 30), never to a player who carries it already or to a flagged one when the bot is not flagged.

**Never against a fight that would flag the bot.** A creature a player controls (a pet or a totem), or a player, would flag the bot for PvP:
the bot takes such a fight only when it is flagged already or `World:Playerbots:Social:WarMode:Enabled` is on, and the brain's targets are
creatures, so a player attacker is never taken. A heal is only cast at a unit `IsSafeHealTarget` allows (never a flagged player for an unflagged
healer).

## Gratitude (`World:Playerbots:Social:Gratitude`, live)

A bot thanks whoever helped it: a rescue in a fight (`PlayerbotSocialRecorder.NoteHelp` records `HelpedBy` and queues the thanks), a resurrection
from a player outside its group (`PlayerbotPartyAI.OnResurrectRequest`) or a positive aura from a same-faction player outside its group
(`SpellSystem.HolderAdded`, seen by `ManagedPlayerbotFeature`). `PlayerbotGratitudeLane` (social pass) takes up to 16 per pass, **once per helper
per fight** (the same 60-second pair window as the help record):

- a `thank` text emote (97) at the helper within 30 yards, **always**, across factions too (`CMSG_TEXT_EMOTE`);
- for a same-faction real player, a short line (`social.thanks` templates, or the model) with the bot's Chattiness as the chance, through
  `TryAnnounce` (limiter, safety screen, disclosure; the opt-in decides only whether a model may phrase the line, never who is spoken to);
- with the Generosity trait at 0.6 or more and affinity 30 or more (never a rival), one favour, chosen by a hash: with the chance
  `TipChance` x Generosity a tip, else a buff (the bot's class buff, through the help request) or a debt of resurrection (the help lane raises the helper without
  a roll if they die within 30 minutes).

The **tip** is a real trade over the existing handlers: `CMSG_INITIATE_TRADE` to the helper (a real player within 8 yards), `CMSG_SET_TRADE_GOLD`
once the helper opened the window, `CMSG_ACCEPT_TRADE`; it ends when the gold moved, the trade closed, the helper went away, the bot fought, or after 45 seconds. Its size is at most 1% of the bot's gold and
at most `MaxTipCopper` (10000, one gold) at level 60, scaled down in proportion at lower levels. `MaxTipCopper` 0 turns tips off.

## Mood (`World:Playerbots:Social:Mood`, live)

Each bot has a short-term mood, -1 to +1, in memory only (`PlayerbotMood`, kept by the social recorder; a restart resets it to 0). It is nudged by
events and decays toward 0 with a half-life of `HalfLifeMinutes` (default 30):

- up: a level (+0.30), a better equipped item (+0.15, read from the equipped item levels every 10 seconds), a group success (+0.06 per member remembered),
  help received (+0.20, `HelpedBy`);
- down: a death (-0.25), a wipe (-0.10 per member remembered), a kick (-0.40), a rude emote (-0.15), a chat strike (-0.30); a PvP death counts as the death (-0.25) only.

The social-memory events move the mood where they are recorded (`PlayerbotSocialRecorder.Note` and `NoteById`); the bot's own level, death and gear are read on the pass
(`PlayerbotMoodLane`). The mood multiplies the chance of the bot's own talk by 0.5 at -1 to 1.5 at +1 (`PlayerbotMood.ChanceMultiplier`; the thanks line uses it today and the
initiative chances below use it too). At +0.3 or more the tone is cheerful, at -0.3 or less grumpy: a template key with `.mood.cheerful` or `.mood.grumpy` lines
(`social.thanks`, `social.greet`, `help.sorry`) uses them instead of the plain lines, and the model prompt gets one mood sentence (`BotChatPersona.Mood`). The GM command
`.playerbot social <bot>` shows the mood, its tone and the chance multiplier. `Enabled` false makes every mood 0.

## Initiative (`World:Playerbots:Social:Initiative`, live)

`PlayerbotInitiativeLane` (`Social/PlayerbotSocialInitiative.cs`, social pass, world thread) lets a bot start a conversation. At most
`MaxOpeningsPerPass` (4) openings happen server-wide per pass; every one goes through `PlayerbotChat.TryAnnounce`, so the limiter, the safety screen and the AI disclosure apply (TryAnnounce itself does not refuse a whisper to a player who never opted in: the opt-in only decides whether a model may phrase it; who is whispered to is decided by the lane, below),
and a model may phrase it where the player opted in (otherwise the `social.*` templates). A round of 24 free bots (their own brain
drives them, alive, out of combat, healthy, on a low-priority goal) looks at the same-faction characters within 20 yards, one bot at most every 20 seconds. An opening needs a reason:

- **Shared objective**: the bot is on a quest and the other holds the same quest unfinished (`PlayerbotGroupCoordinator.SharesGoal`): "You on the <quest> too? Want to team up?"
  The reply is the player's **next say** within hearing of the bot (they need not name it; `PlayerbotSocialRecorder.NoteHeardSay`). For this offer only a clear yes ("sure", "yes", "ok") counts;
  negations ("not sure", "no", "can't") do not. A yes raises a group signal (source `contention`, the bot's objective creature and quest) and the lane then calls the coordinator's
  `Volunteer(bot, player)` (retried each pass until the bot waits, two minutes at most), which matches that very player; with `World:Playerbots:Groups:InvitePlayers` the leader sends the ordinary
  invitation and, once accepted, the player and the bot are in one group. Between two bots, the answering bot rolls its own yes and the lane follows it through with `OfferLfg`, so the answerer pledges to the asker's waiting group.
- **Follow-up**: familiarity 3 or more, affinity 0 or more and a last topic set: "How did <topic> go?" (once per pair per 6 hours).
- **Greeting**: familiarity 2 or more, affinity 0 or more and the other was not in sight for 10 minutes: "Welcome back" (once per pair per 6 hours).
- **Bot to bot**: the same openings between two bots only where a real same-faction player stands within `World:Playerbots:Social:Memory:BystanderRadiusYards`.

Each has a base chance (0.5, 0.3, 0.25) times 0.5 + the bot's Sociability, times the mood multiplier (0.5..1.5), rolled as a hash of the bot, the other and the minute.
A bot never opens with a rival or someone it dislikes, nor with a player who ignores it. **Unprompted whispers and greetings** only go to a real player the bot has really interacted with
(`PlayerbotRelationFlags.Interacted`, set by a chat exchange, a completed trade, help received, a friendly emote, an apology or a group, and persisted) or who opted in; help the bot
gave on its own does not count. Everyone else gets a public /say. A real player is opened with at most once per `OpeningCooldownMinutes` (30) by any bot. If they do not answer within 120 seconds the bot
loses 1 affinity (`OpeningIgnored`, no familiarity) and the cooldown for that player doubles (up to 16x). A **decline** ("no", "not now", "can't") backs off the same way, with no affinity
penalty; only a positive reply clears the back-off.

**Surroundings comments** (`CommentsEnabled`): a free bot with a real same-faction listener within earshot rarely says (template, or the model) one of: a rare creature (rank rare or rare elite)
within 60 yards, a festival that started since the last pass (see World events, below), four or more real (not managed-bot) players of the other faction within 80 yards, a familiar player's first mount. The mount is
simplified and in memory only: a player of level 40 or more seen on foot and later seen mounted (nothing is persisted, and there is no spellbook or skill check, so a restart forgets who was seen on foot).
Chance 0.35 x (0.5 + Chattiness) x the mood multiplier per minute; at most one per bot per `CommentBotGapMinutes` (10) and one per zone per `CommentZoneGapSeconds` (60).

## Rivalry (P6a, `World:Playerbots:Social:Rivalry`, live)

`PlayerbotRivalryLane` (lane `rivalry`, `Social/PlayerbotRivalry.cs`; design phase P6, first slice: emotes and the rival stance, **no combat**). Two switches
share a name and are not the same: `World:Playerbots:Social:Emotes:Enabled` is the same-faction wave (lane `emotes`, above);
`World:Playerbots:Social:Rivalry:Emotes:Enabled` is this lane, the **cross-faction** greetings and rude emotes. `World:Playerbots:Social:Rivalry:Enabled` switches everything here off at once
(replies go back to the old table; recorded opt-outs are kept).

**Stance.** One pure function, `PlayerbotRivalryRules.StanceOf(relation, layer)`, reads one layer of the bot's memory: `Rival` (that layer's Rival flag), `Disliked`
(affinity below 0), `Friendly` (affinity at least `World:Playerbots:Social:Rivalry:Emotes:FriendlyAffinity`, default 20), otherwise `Neutral` (a stranger too).
**Toward another bot the normal layer decides, War Mode or not**; toward a real player the lane's own emotes read the layer the memory's decisions read (the war layer
while War Mode is on), and replies to a real player read the normal layer (a reply is budgeted speech). Every emote the lane sends is a direct, unbudgeted `CMSG_TEXT_EMOTE`,
rolls are hashes of bot, target and the half hour, and limiters are kept apart for bot targets and player targets, so what War Mode makes a bot do to a player never
changes what it does to a bot (test: `PlayerbotRivalryWarParityWorldTests`).

**Greetings.** Each pass up to 24 idle bots (autonomous, alive, out of combat, not in a battleground or instance, round robin) look at characters of the **other** team within
`min(Emotes:RangeYards, World:ListenRangeTextEmote)` (20 yards by default; the listen range is read every pass), alive, out of combat, not ignoring the bot. A `Neutral` or `Friendly`
target is greeted when its roll passes: a stranger with `World:Playerbots:Social:Rivalry:Emotes:GreetStrangerChance` (0.25) times the bot's sociability, a known neutral half the sociability, a friend always;
a failed roll leaves that target out for the half hour and the walk goes on to the next. A friend gets a wave, bow, salute, cheer or hail, anyone else a wave, nod or bow. Limits (world
time, apart from the same-faction wave's): once per bot and target in 30 minutes, once per target in 10, once per bot in 5, and `World:Playerbots:Social:Rivalry:Emotes:EmotesPerMinute` (12) for all bots,
counted separately for bot targets and player targets. A bot is only greeted where a real player is within the listen range of the greeter (`World:Playerbots:Social:Rivalry:Emotes:BotToBot`).

**Rude emotes.** A `Rival` gets one of rude, chicken, threaten, mock or violin (`RivalEmotes`), at most once per bot and target in `RudePairGapMinutes` (30), once per bot in 10 minutes
and **once per target in `RudePerTargetMinutes` (30) from all bots together**; they count against `EmotesPerMinute`. Never at a player who opted out (below); with
`World:Playerbots:Social:Rivalry:Emotes:RudeRequiresOptIn` only at players who whispered `rivals on`. Never at a bot unless `World:Playerbots:Social:Rivalry:Emotes:RudeAtBots` (off by default). Never while the bot or the target stands in a
capital, in a `World:Playerbots:Social:Rivalry:Safeguards:SafeAreas` area (area ids, default the six towns with a guard post for both factions: Booty Bay 35, Ratchet 392, Gadgetzan 976, Everlook 2255, Nighthaven 2361,
Light's Hope Chapel 2268) or anywhere the zone table cannot place (zone 0 or a zone it does not hold): the check fails closed, greetings are unaffected. The first rude emote a real
player receives in a login is followed by one system notice that says how to stop them.

**Replies.** An emote aimed at a bot is answered by `PlayerbotRivalryRules.ReplyTo(stance, emote, toBot, roll, rudeAtBots, optedOut)` (only while bot chat is on, as before):

| The bot's stance toward the emoter | Friendly emote | Rude emote | Other |
|---|---|---|---|
| Rival | to a player a rude emote or nothing; to a bot a shrug or nothing | to a player a rude emote; to a bot a shrug or a sigh | as before |
| Disliked | a shrug or nothing | to a player a rude emote; to a bot a shrug or a sigh | as before |
| Friendly | as before | a shrug or a sigh | as before |
| Neutral | as before | as before to a player; to a bot a shrug or a sigh | as before |

**Toward a bot, an answer that classifies as rude is replaced by a shrug unless `RudeAtBots` is on**, in every stance (this also stops the old one-in-three rude answer to a rude emote and the
chicken answer to a threat): two bots with different opinions of each other can then never trade rude emotes, so no exchange between bots lowers an affinity. A player who opted out is not
answered at all until `rivals on`. **A rude answer to a real player keeps the lane's safeguards**: with `RudeRequiresOptIn` it needs a `rivals on`, and when either character
stands in a capital, a safe area or a place the zone table cannot place it becomes a shrug (the per-target and per-pair rude caps belong to the lane; a reply is limited by `EmoteReplyGapSeconds` and the speech budget). A rival bot can
still answer a real player's friendly emote rudely (the table above): the owner's ruling R11 covers it.

**Opting out.** Whisper any bot of **your own faction** `rivals off`: the server drops a whisper between factions unless `World:AllowTwoSideChat`, but the choice is server-wide, one store shared by every
bot of both factions, and holds for the whole account. `rivals on` undoes it and `rivals` alone tells the status; the line is read as the "ai off" command is (letters and spaces, any case), answered by a
system message at most once per 10 seconds, and consumed (the chat service never hears it). It works with bot chat off and when the bot ignores you. **The choice lasts until the server restarts**
(it is kept in memory); the answers say so. The store holds 8192 players: past that the oldest `rivals on` is dropped to make room, an opt-out is never dropped, and a new choice made while every slot holds an opt-out is refused (the player is told, and the optouts meter counts `refused`). A bare `rivals` whispered to a bot is consumed even while `World:Playerbots:Social:Rivalry:Enabled` is off, so a player can opt out beforehand. `World:Playerbots:Social:Rivalry:AllowOptOut` (on) only decides whether the line is parsed (it is also parsed while `RudeRequiresOptIn` is on); a choice already recorded is always honoured.

**Organised kills.** `World:Playerbots:Social:Memory:OrganisedPvpKills` (off): a PvP kill on a battleground map, or with either side in a GM-ordered siege group, records no `KilledBy` or `KilledThem`, so players met
in a match are not remembered as rivals in the open world. On, they are recorded like any other.

Meters: `arcanecore_playerbot_rivalry_emotes_total{kind}` (`greet`, `rude`, `reply_rude`, `reply_shrug`) and `arcanecore_playerbot_rivalry_optouts_total{change}` (`off`, `on`, `refused`; docs/ops/metrics.md); `.playerbot social $bot` ends with `rivals=N`, the
number of rivals in the bot's normal layer. Faults go to `playerbot.social`.

## World events: festivals (`World:Playerbots:Social:Events`, live)

While a festival runs, idle bots that stay in a town gather around the festival's NPCs instead of at the inn, celebrate there with text
emotes, and now and then say a line that names the festival. Code: `src/ArcaneCore.Playerbots/Events/` (`PlayerbotHolidayCatalog`,
`PlayerbotWorldEventBoard`, `PlayerbotFestivalRules`, `PlayerbotFestivalClaims`, `PlayerbotEventListeners`, `PlayerbotEventLane`,
`PlayerbotWorldEventGoals`, `PlayerbotEventMeters`) and `PlayerbotBrain.Events.cs`. Everything is on by default and every key is live.

- **Which events are festivals.** A game event with a holiday id (the Hourly Bells, the day and night events and the war-effort events never
  are) is a *root*. An event linked to a running root (`game_event.linked_to`, at most four links; Wickerman Ember to Hallow's End) counts
  while its root runs; event 21 (Winter Veil: Gifts) is not linked in the data, counts as nothing in slice 1 and is held for the later event-quest stage. A festival has a flavour (Midsummer, Winter Veil, Lunar
  Festival, Love is in the Air, Noblegarden, Children's Week, Hallow's End, Harvest Festival, Darkmoon Faire, fishing contest, or a plain
  "festival") that sets its name, its emotes and its `event.<flavour>.arrive` lines. The fishing contest names itself but does not gather
  (its NPCs run a contest, not a party). `World:Playerbots:Social:Events:Holidays:ExcludedEvents` is a comma list of event ids (at most 64) that are never festivals.
- **The board.** `PlayerbotWorldEventBoard` listens to the game events and rebuilds its picture only when a festival event starts or stops:
  the placed NPCs of the running festival (the positive `game_event_creature` rows of the root and its running members) that talk, give
  quests or sell, are selectable and are no guard. Per town and team it keeps the anchors a bot may use: present for the team, inside the
  bot's home, within `World:Playerbots:Social:Events:Holidays:SpotRadiusYards` of the town centre, approachable (a hostile NPC is not), outside
  zones where PvP is enforced on the bot's team and outside the areas of a running event scenario.
- **A spot.** A bot that is idle in a town it stays in rolls once per game day per festival (`World:Playerbots:Social:Events:Holidays:CelebrateChance`,
  scaled by its sociability, staggered by bot so they do not all start at the same minute). If it celebrates, it takes a place on a ring 8 to
  12 yards from an anchor (a gossip NPC first, then a vendor or quest giver), so it stands near the NPC and never in a player's interaction
  range. Places are capped: `World:Playerbots:Social:Events:Holidays:MaxPerGossipAnchor`, `World:Playerbots:Social:Events:Holidays:MaxPerServiceAnchor` and
  `World:Playerbots:Social:Events:Holidays:MaxPerTown` per team. The place is the bot's idle centre: the town idle rule (`PlayerbotTownIdle`) walks to it and holds there as it does at the inn, and a place 5 or more
  yards above or below (Undercity, Thunder Bluff, Ironforge) is never "arrived". A walk the idle rule gives up, or a bot on the wrong floor,
  sets that NPC aside for the bot for `World:Playerbots:Social:Events:Holidays:SpotBackoffMinutes`, frees the place, reports the Info anomaly `event/festival-spot-unreachable` (once per NPC per hour) and sends the bot
  back to the inn. A bot has arrived at its place within 3 yards of it (so never within 5 yards of the NPC) and is held there only while it stands at least 5 yards from the NPC (a stalled walk that stops inside the interaction distance is given up like any other). A secondary town's bot takes no place in its explore window. The Social section has no master switch of its own; the gathering is gated by `World:Playerbots:Social:Events:Enabled` and `World:Playerbots:Social:Events:Holidays:Enabled` only. A place lasts 60 seconds past the last held think. Any other goal (a quest, combat, a hunt, rest) frees it at once: the
  festival is idle behaviour and never keeps a bot from questing. A bot a player attacks at a spot sets the town's festival aside for its team for
  the same time (a crowd that draws attacks disperses). A festival that ends, or a switch turned off, sends the bots back to the inn at their next think.
- **Arrival line** (events lane, once per place): with chance 0.6 x (0.5 + chattiness) x the mood multiplier a bot says one /say line
  `event.<flavour>.arrive`. The listeners are the real players of **either faction** within `ListenRangeSay` (25 yards): none, one who ignores the bot, or one the chat safety
  cut off means no line; every listener must be free in the initiative's per-player ledger (`OpeningCooldownMinutes`, the same one the openings use, so festival lines, event remarks and
  openings together reach a player once per 30 minutes); a listener who said "ai off" makes the line built-in only (no model); at most one line per bot per
  `World:Playerbots:Social:Events:Holidays:TalkBotGapMinutes` and per zone per `World:Playerbots:Social:Events:Holidays:TalkZoneGapSeconds`.
  `World:Playerbots:Social:Events:Holidays:TalkEnabled` turns the lines off.
- **Celebration emotes.** A held bot sends one untargeted text emote from its festival's set every `World:Playerbots:Social:Events:Holidays:EmoteMinSeconds` to
  `World:Playerbots:Social:Events:Holidays:EmoteMaxSeconds` (shorter for sociable bots). The server shows a text emote to everyone within `ListenRangeTextEmote` (25 yards), so it is sent only when
  every real player in that range has room in its own budget (`World:Playerbots:Social:Events:Holidays:EmotesPerListenerMinute`, from all bots together) and the anchor has room in its own
  (`World:Playerbots:Social:Events:Holidays:EmotesPerAnchorMinute`, listeners or not). No emote is aimed at anyone: a friendly emote at a bot would feed the social memory (affinity, familiarity), so festivals add no
  memory source.
- **The "an event has started" remark** (the initiative's surroundings comment) is now said only for a festival root, in a zone that has an anchor NPC of it, to the
  same listener filter and ledger, with the festival's name (`{event}`); the Hourly Bells and member events say nothing.
- **Switches.** `World:Playerbots:Social:Events:Enabled` is the master switch, `World:Playerbots:Social:Events:Holidays:Enabled` the holiday half. Off, bots give their places up at their next think and say and emote nothing.
- **Observability.** `.playerbot social <bot>` adds a line with the bot's place (or that a festival runs and it holds none). Metrics:
  `arcanecore_playerbot_events_total{kind,outcome}`, `arcanecore_playerbot_event_lines_total{kind}` and `arcanecore_playerbot_event_suppressed_total{reason}`
  ([metrics](../ops/metrics.md)); the suppression reasons are `no_listener`, `listener_cap`, `cut_off`, `ignored`, `emote_listener_budget` and `emote_anchor_budget`, so a cap that never lets anything through is visible.
  A fault in the festival code is recorded under `playerbot.social.events` and pauses that bot's festival goals for a minute.
- **Cost.** One dictionary lookup per held bot and pass, no allocation per think; a small listener list is allocated per emote or line attempt (one attempt per 30 to 90 s per held bot) and per arrival; the board is rebuilt only when a festival event changes; the walk to a place is the ordinary town walk.
- **Not done in this slice.** Targeted festival emotes at friendly real players, and the war-effort and scenario festival chapters.
  Tests: `tests/ArcaneCore.World.Tests/Playerbots/Events/`.

## World PvP: Silithyst (P7c phase 1, `World:Playerbots:WorldPvp:Silithyst`, live)

Bots take part in the Silithus objective as a player does. The server half is unchanged ([outdoor PvP](outdoor-pvp.md)); `Events/PlayerbotWorldPvpObjectives.cs`
is the bot half, one controller per brain.

- **Who.** A bot in Silithus proper (map 1, zone 1377), level 55 or more, alive, out of combat, that rolls in. The roll is made once per two-minute window from the bot's guid
  against `ParticipationChance` (default 0.5), or `WarModeParticipationChance` (default 0.9) while `World:Playerbots:Social:WarMode:Enabled` is on, so a crowd of bots is not all on the geysers at once.
  `Enabled` (default true) off: no new run starts; a bot already carrying finishes its trip.
- **The run.** *Seek*: the nearest spawned Silithyst Geyser (181598) or Mound (181597) that is ready, not set aside and claimed by fewer than `MaxBotsPerGeyser` (1..8, default 1) other bots
  (`PlayerbotClaims`, the same claim table as mobs and chests). *Use*: walk to within 4 yards, stop, CMSG_GAMEOBJ_USE (the goober casts 29518, whose script gives the flag aura 29519).
  *Await*: 3 s for the flag. *Carry*: walk on foot to the centre of the own faction's turn-in trigger (4162 Alliance, 4168 Horde); the ordinary area trigger packet fires on entering it and the server counts the
  delivery and applies Traces of Silithyst (29534). *Deliver* is done when the flag is gone within the trigger's radius plus 8 yards and 29534 was newly put on by this turn-in (an older 29534 still running does not count). A bot that picked the flag up inside the
  trigger volume, or stands at its centre with the flag still on, walks out (radius + 8 yards) and back in, up to twice more, because the trigger fires on entering (the motion's loop blacklist is lifted for those two places). The carry limit (5 minutes) counts only the time the carry rung runs, not rest or fights.
  A mound a carrier dropped (the server puts it down at the carrier's feet when the dust is cancelled or lost) is taken the same way as a geyser by the next bot that seeks.
  The geyser is looked at again after a minute. After a delivery or any give-up the bot starts no new run for two minutes (one roll window).
- **Priority.** A run in progress, or a bot found holding the flag, is driven high in the think ladder: after rest and equipment, before class upkeep, home, town errands, the goal policy and quests (a fight still comes first).
  A new run is only started low, next to the opportunity goal. The goal kind reported is `WorldPvp` (appended to `PlayerbotGoalKind`; it is not a goal-policy rung).
- **A carrier never mounts or stealths.** The server drops the dust when the carrier mounts or stealths away from its trigger. `PlayerbotMount.CanMountHere` refuses while 29519 is on inside zone 1377 (the server drops the dust only there; elsewhere on the map the carrier may ride), and the class upkeep gets
  `RotationState.NoStealth` (rogue Stealth and Vanish, druid Prowl); a rogue's retreat does not choose Vanish either (`EscapeSituation.CarriesSilithyst`). A mount-up cast under way when the flag lands, and a mount already on, are cancelled in zone 1377 only.
  Using the geyser still dismounts the bot (ordinary game object use). A cancel of that cast the action budget refuses is kept and sent at the next think.
- **Giving up.** A geyser someone else used, or a lost flag away from the trigger, ends the run quietly (the geyser is set aside 30 s to a minute). A death, no route, or a stalled walk sets it aside with the
  ledger's backoff (60 s doubling to 5 min, `PlayerbotOpportunityLedger`). The brain's stall watch giving the goal up ends the run the same way. A carry given up while the flag is still on (no trigger row, no route, a refused turn-in,
  the carry limit, a stall) cancels the flag with CMSG_CANCEL_AURA (five tries, one second apart) and does not take the flag up again for ten minutes: the aura never expires, so otherwise every think would open a new carry and
  report the same fault again. The fault is reported once for as long as the same flag is held.
- **Anomalies** (area `world-pvp`, once per run, none on success or an early loss of the flag): `silithyst-use-no-aura` (the use gave no 29519 within 3 s),
  `silithyst-carry-timeout` (a carry of more than 5 minutes of carry time) and `silithyst-unreachable-trigger` (no route to the trigger, no trigger row loaded, or the flag still on after three entries into the volume).
- **Observability (server).** Each pickup and each turn-in writes a debug line `outdoor-pvp.silithyst` with the player, team and count; `SilithusZone.PickupsTotal` and `TurnInsTotal` count them since start.
- **Cost.** The controller's own flag and Traces view is one aura walk per think, shared by the two ladder rungs of the run (`SilithystThisThink`), and the brain takes it only in zone 1377 or while the controller has work
  (a run, a drop or cancel pending, or a flag seen at the last scan). A bot elsewhere with no run does no walk and allocates nothing, so a flag holder outside the zone is adopted only if it was seen at the last scan or is in a run, and then only on map 1;
  off map 1 the flag is cancelled once and no longer watched, and a run ends quietly when the bot changes map. The mount check, the retreat and the combat view do their own zone-gated walks (a zone compare, one walk only in zone 1377). Once the controller exists, `Update` costs a few field checks per rung, plus `Eligible` every 5 s. An eligible bot not cooling down seeks every 5 s with one dictionary lookup per geyser spawn of the map (16 in the real data, no enumerator, no allocation). The geyser guids come from the map's
  spawn table (`GameObjectMapSystem.SpawnGuidsOf`), so the list is complete whichever grids are loaded: an object in an unloaded grid is skipped at the lookup and found at the next seek once its grid loads. The table is read once
  per map, shared by the bots of the map, and again every 10 minutes (a content reload). Mounds (181597) are not in the spawn table, so the loaded objects of the map are scanned for them once a minute per map (one enumerator);
  a mound in a grid that loads in between is seen up to a minute late.
- **Not done in phase 1.** Eastern Plaguelands towers, dust bags, dropping the flag on purpose, escorting a carrier, killing enemy carriers on purpose and group runs. A flag the server dropped (the Silithyst Mound, 181597) is sought like a geyser.
  Tests: `tests/ArcaneCore.World.Tests/Playerbots/WorldPvp/`, and the server end to end in `tests/ArcaneCore.World.Tests/OutdoorPvP/SilithystEndToEndTests.cs`.

## Review round 1 (P2)

- **PvP flags.** A player-flagged creature or player is a flagging fight (`PlayerbotHelpRules.WouldFlag`).
- **Winnable fights only.** A helper takes an attacker only within 3 levels above it; an elite needs a group of 3 or more; a healer needs 30% mana (`HelperFit`).
- **Calls are capped.** One call draws at most `World:Playerbots:Social:Helping:MaxAnswersPerCall` (default 2) answers.
- **Once per death.** A dead player is offered one resurrection per death, however long the body lies; corpses older than `World:Playerbots:Social:Helping:ResurrectMaxCorpseMinutes` (default 10) are left.
- **Assist credit.** The help is recorded only once the bot is on the attacker's threat list; a bot-given resurrection by a stranger is thanked once (the gratitude lane; the voice "ty" is for group members).
- **Lane isolation.** Each social lane runs in its own try/catch; a lane that throws is reported (anomaly `feature-fault`, subject `social`, lane in the message) and the others still run.
- **Tips.** A tip trade is cancelled (`CMSG_CANCEL_TRADE`) when gratitude is disabled live or the feature stops, and `TipsPaid` counts completed trades (`SMSG_TRADE_STATUS` complete), not requests. A dead bot owes its thanks for up to 2 minutes.
- **Mood.** A PvP death moves the mood once (the death), not also as `KilledBy`.
- **The wait for an answer starts when the opening is said.** The speech path calls the opening's `Sent` callback once the line was really said; only then does the lane wait for the player's next say and start the player's opening cooldown (`OpeningCooldownMinutes`, for a player still online). An opening the limiter or the place refused costs the player nothing: no wait, no penalty, no back-off, and no hold for long: from the moment the opening is queued the player is held against a second bot until the line is said (the cooldown then runs) or dropped (the hold is given back at the next world-thread drain, not at once; `BotSpeech.Dropped` fires for a refusal, a provider chain that gave nothing, or a bot that stopped). A chat-service shutdown or detach, and a delivery fault, never fire it: those fall back to the 10 minute hold (`UnsaidHoldMs`). A say that names a character standing within earshot of the bot (same map, `BystanderRadiusYards`, and not a reply word such as "sure") is for them, not the answer; the wait goes on. The **first** line of an awaited player is always kept, whatever the shared 256-line reply queue holds; its later lines, until the lane reads the first, follow the shared cap.
- **A waiting bot opens on its goal.** A bot that waits for a group has no quest id of its own; the quest and creature of the goal it waits for are used for its team-up line. The answering bot's yes (a bot-to-bot opening) raises the asker's group signal and the lane then offers it to the coordinator (`OfferLfg`), retried each pass until the asker waits (two minutes).
- **Relations stored before this change have no `Interacted` bit,** so a bot greets or whispers such a player only after their next exchange, trade, help or group.
- **Lane faults** are reported (fault ledger and anomaly `bot-social/feature-fault`, the lane in the message) once per lane per minute; every fault is counted (`LaneFaultCount`). A ghost is judged by where its corpse lies, and a corpse in sight counts as a dead player in reach. Trade-completed notes (tip payments) live five minutes and are capped at 256; the unmounted sightings behind the first-mount comment drop the oldest first.
- **`{objective}` in announcements.** The template placeholder `{objective}` may now be filled from the bot's own facts (it was topic-only for a moment, which broke `doing.objective`). It is always the bot's own quest progress, or the shared quest title the initiative lane puts in the topic; no template can show another player's objective, because no fact of another player's is ever put into a bot's persona.
- **Verified.** `TryAnnounce` does not refuse whispers to non-opted players (the opt-in only gates model use), so the Interacted gate in the lane is the guard. `SpellSystem.HolderAdded` is raised on the world thread. The thank emote id 97 is the repo's `PlayerbotSocialEmotes.TextEmotes.Thank`.


## Trading (`World:Playerbots:Social:Trading`, P4a foundation)

Status: the 26 Trading keys are registered and documented, but until the trade desk and the deal steps land (later P4 steps) nothing reads them except `ScamMemoryDays` and `NinjaMemoryDays`, which the trade ledger applies when it reads a row. Setting the others changes no bot behaviour yet.

The trade ledger (`playerbot_trade_record`, characters schema) keeps, per bot and partner, the fair deals and scams counted, the last scam and ninja times and the last UTC day a gift counted: counts and times only, no items, prices or chat. It is fed by the social recorder (a scam, ninja loot or gift note) whether or not `World:Playerbots:Social:Trading:Enabled` or `World:Playerbots:Social:Memory:Enabled` is on; the switches gate what bots do with it, not what is counted. It holds at most 4096 partners per bot and is written at the bot's checkpoint only when it changed. Deleting a bot removes its own rows and the rows other bots hold about it (written again without them right away, like the relation table). Rows about a real player have no time expiry: they stay until the bot's 4096-partner bound evicts them (a partner with no scam or ninja loot inside its window and the fewest fair deals goes first). A bot that was deleted is not fed again.

## Cost and persistence

One pass every second of world time. A pass does a constant number of dictionary lookups per bot and per bot guild (the bot list is
gathered once per pass and only when something needs it; an event handler's pass gathers none), drains only the guild-invite,
charter-signature and battleground-win packets of each bot's capture queue, and makes at most one chat admission per guild. The rate
limiters and the boss and battleground-win maps are bounded and swept; a pass that throws is logged (the first five in full) and the world
carries on.

**The pass is incremental** (`World:Playerbots:Social:PassBudgetMs`, default 4; 0 = no limit). A pass starts when its interval has passed and no
pass is in flight, takes a snapshot of the running bots (a bot that leaves the world meanwhile is dropped from it) and walks its stages in
order, at most the budget per world tick (plus one slice): the packet intake, the injected test lanes, then the lanes in their usual order.
The lanes that act on one bot at a time and independently of the others (`answer-invites`, `emotes`) run on slices of the snapshot and stop
between slices once the budget is spent; the next tick resumes at the next bot. Every other lane is population-wide (it has its own cursor
and per-pass quota, or reasons about guilds, charters and the set of running bots) and runs whole as one stage, so the registry check, the
intake and these lanes keep their once-per-pass meaning. A stage never starts before the one before it has finished, a pass that outlasts
its interval is simply followed by the next as soon as it ends (no catch-up passes), and a pass in flight for more than 20 intervals is
reported once to the anomaly ledger (`bot-social` / `pass-starved`).

Guilds, ranks, petitions and members persist through the existing social system (the social memory has its own table, above), and the feature's own
state is in memory or read back from it.

## Tests

`tests/ArcaneCore.World.Tests/Playerbots/Social/`: `PlayerbotGuildFoundingWorldTests`, `PlayerbotGuildInviteWorldTests`,
`PlayerbotGuildChatWorldTests`, `PlayerbotGuildRulesTests`, `PlayerbotGuildNamesTests`, `PlayerbotSocialRateLimiterTests`,
`PlayerbotAnnounceTests`, `PlayerbotSocialListsWorldTests` (the list thresholds and spacing, never ignoring a game master, guild invitations
by affinity and their weekly limit), `PlayerbotHelpRulesTests` (trouble, rolls, classes, help-call text), `PlayerbotHelpAnswerWorldTests`
(a `/helpme` through the real packets to a bot that fights), `PlayerbotTroubleWorldTests` (the scan, the flag rule, rivals, switches),
`PlayerbotHelpCastWorldTests` (the real resurrection once per corpse, a buff and its cooldown), `PlayerbotInitiativeTests` (the rules; greeting by say then whisper; the player cooldown and the per-pass cap; rivals and the switch; ignored openings; a yes raising a group signal; a rare-creature comment and its gap), `PlayerbotMoodTests` (events, half-life, multiplier, tone templates, prompt fact, GM line, the level-up lane), `PlayerbotGratitudeWorldTests` (the tip cap and favour rules, the thank emote across factions once per fight, a tip by a real trade).
