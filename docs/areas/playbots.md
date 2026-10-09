# Playerbots and playtest clients

ArcaneCore has two kinds of bot. **Managed playerbots** are server-owned P0 players inside the
world daemon (`src/ArcaneCore.World/Playerbots/`); they run autonomously or are driven by a
script, which is the **scenario harness** for automated testing of game systems. The
**MockClient playbot** (`arcane-mock playbot`, last section) is an external build-5875
protocol client.

## Managed playerbots

`ManagedPlayerbotFeature` (`IPlayerbotService`) owns persistent bot characters on random,
password-less Player accounts and logs them in through an ordinary socketless `WorldSession`
(`Net/ManagedWorldSession.cs`): every bot action is a real CMSG run through the real world
handler (`TryManagedAction`), and server replies are captured into a bounded outbound queue.
Configuration is `World:Playerbots` (`Enabled`, `MaxBots` 8, `MaxRegisteredBots` 1000, `ThinkIntervalMs` 500,
`MaxActionsPerTick` 4, `AllowedMaps` [0, 1], `FaultBackoffSeconds` 30, `MaxFaults` 3,
`FaultWindowSeconds` 3600, `AllowLocalLlm`, ...; off by default).

**Caps.** `MaxBots` (0..1000) bounds the bots **running** at once: a stopped, faulted or quarantined bot holds no slot, and a
start beyond it is refused with `playerbot-capacity` (a startup restore or quarantine retry waits for a slot). `MaxRegisteredBots`
(0..10000) bounds `.playerbot create` (`playerbot-registry-full`); every registered bot is a real account and character. Both
are live through `.reload config`: a raise admits more starts at once; lowering `MaxBots` below the running count stops nobody,
it refuses new starts until enough bots are stopped. Until 2026-10-08 `MaxBots` was capped at 64 and creation counted stopped
bots too; the ceiling of 1000 and its measurement are in `docs/integration/perf-limits-20261008.md` (a ceiling, not a
recommendation: watch `.server info` on a real world).

GM commands: `.playerbot create|start|stop` (Administrator), `.playerbot status|list|inspect`
(GameMaster), `.playerbot scenario list|run` (Administrator, below). The autonomous brain's
behaviour is described in `docs/integration/playerbot-*.md`.

Before a pull the brain weighs each candidate's risk against its reward, and in a fight it retreats past the creatures' leash when
it is losing (`World:Playerbots:Risk`, live): `docs/areas/playbots-risk.md`. The risk decision ends each `.playerbot status`
line and has its own `BOTINSPECT` line.

Content one bot cannot do (elite, dungeon and raid quests, objectives inside instances, quest targets too strong alone) becomes a
"needs a group of N" goal: `PlayerbotGroupCoordinator` matches bots by goal, team, level and role, the leader invites the others
through the ordinary group packets, and `PlayerbotGroupAI` drives the members until the content is done (`World:Playerbots:Groups`,
live; `.playerbot groups`): `docs/areas/playbots-groups.md`.

### Autonomous and scripted mode

By default a running bot is **autonomous**: each world tick `PlayerbotBrain` chooses quest,
town, trainer, combat, loot and recovery goals within the shared per-tick action budget.

Movement is separate from both: `PlayerbotMotion` advances every bot's current route every
world tick (before any think, outside the action budget) and reports it the way a 1.12 client
does — START, heartbeats every 500 ms and at turns, STOP — so observers see smooth running
whatever the think interval. Brain, goals and scripted controllers only choose routes
(`PlayerbotNavigation.TryPlan` / `TryAdvance`). It also gives up navigation loops. Details and
the 2026-10-07 root causes: `docs/integration/playerbot-movement-and-tick-health.md`; whether
real terrain/collision/navmesh data is needed: `docs/integration/maps-vmaps-mmaps.md`.

**Game events.** A bot respects the server's game-event state (`World:GameEvents`, `game_event_quest`, `game_event_creature`): a quest
of an event that is not running is never offered to it (the server's `CanTakeQuest`, vmangos `Quest::IsActive`) nor turned in
(`IsRewardable`, vmangos `PrepareQuestMenu`), and one already in its log is kept but its objectives are not hunted (alone or as a group
goal) until the event runs again; a quest giver or ender whose spawn is listed under an event is not a destination while the spawn gate
keeps it out of the world (`PlayerbotWorldDestinations.IsPresent`), so a complete quest whose only ender is a holiday NPC is not walked
to off season. This needs the world's game-event tables: a world migrated from the Codex-line schema has them empty (the live world of
the wave-8 rehearsal: bots took "Winter's Presents" 8827, "Dearest Colara," 8898 and the Lunar Festival and Midsummer quests in October)
until `arcane-content-importer refresh` fills them (docs/areas/content-import.md).

The live snapshot replay (`PlayerbotLiveSnapshotReplayTests`: `ARCANECORE_TEST_BOT_REPLAY_DB`, `ARCANECORE_TEST_WORLD_DB`,
`ARCANECORE_TEST_TERRAIN_DIR`, optional `ARCANECORE_TEST_BOT_REPLAY_SETTINGS` without a `Database` section, `..._NAMES`, `..._TRACE`,
`..._TIME`) runs the game-event clock from the snapshot's moment (`ARCANECORE_TEST_BOT_REPLAY_TIME`, ISO 8601, else the characters
file's last write time) with game time, as it already ran the death clock: before, events followed the real date while the replay ran
its game time about 30 times faster, so a replay in December would have turned Winter Veil on for an October snapshot. It waits for each
step's quest reward writes (on the fast clock a bot's 15-second exchange deadline ran out while the write was on its way, and the reward
counted as refused) and prints each bot's quests taken, rewarded and still open.

A far goal (a quest giver or ender, a corpse) is approached a step at a time (`PlayerbotNavigation.TryPlanToward`): the point
`MaxPathPoints - 2` yards along the straight line (at most 90% of `MaxRouteYards`), or the walkable point the mesh reaches nearest to it.
Navigation-mesh tiles load with the map's grids round the players, so that point may lie on a tile not loaded yet, where the mesh
answers a straight line (vmangos `PathFinder`'s `HaveTiles` shortcut) that the terrain stepper refuses over any hill; the step is then
halved (down to 24 yards) until it stays on the loaded mesh, and walking it loads the next tile. Before, the bot stood at such a tile edge
for good: the wave-8 rehearsal's quest-35 stall (Dawnrover 107 yards north of the Elwynn tile boundary, 610 yards from Guard Thomas).

`World:Playerbots:MovementPackets` (default `true`, live through `.reload config`) chooses how
those moves reach the world. `true`: each one is a client MSG_MOVE_* packet, encoded and dispatched
through the opcode table to `MovementHandlers` like any client's. `false`: the server applies the
same move itself (`WorldSession.TryManagedMovement` -> `MovementHandlers.MoveManagedBot`): the
same admission (not while teleported or on a taxi, a valid block, the bot moves itself), the same
relocation with the locomotion observers and the same MSG_MOVE_* relay to observers, without the
dispatch or the packet encode/decode. Observers receive byte-identical streams in both modes
(`PlayerbotMovementPacketsTests`); `PlayerbotMotion` reads the option at every move, so a reload
switches running bots at their next move. Acknowledgements (speed, root, teleport, far transfer)
and `CMSG_AREATRIGGER` stay client packets in both modes.

In **scripted mode** an `IPlayerbotController` replaces the brain.
`ManagedPlayerbotFeature.StartScriptedAsync(idOrName, controller)` starts a bot that way;
`SetControllerAsync(botId, controller)` switches a running bot (null returns it to autonomous
mode). While scripted, the brain is never updated (no goals, no packets drained, no actions)
and the shared action budget does not apply; the controller's `Tick` runs on the world thread
every tick with a `PlayerbotControllerContext` (`TryAction`, `AcknowledgeServerOrders`).
Stopping a bot detaches its controller; `IsScripted(botId)` reports the mode. Switching back
to autonomous mode resumes the brain with whatever state it had before.

### Faults and quarantine

An exception out of a bot's update (brain, scripted controller or `PlayerbotMotion`) is an
**action fault**. It is logged with the whole exception (type, message and stack), the bot's
session closes and the next checkpoint (every 5 s) quarantines it: the character is saved and
the `managed_playerbot` row turns `Faulted` with the fault as `ErrorCode`
(`quarantined (fault 1/3): action: <message>`), but **keeps `DesiredEnabled`**. After
`FaultBackoffSeconds` (30; doubled for every further fault, at most an hour) the bot logs in
again, autonomous (a scenario controller that faulted is not reattached). The `MaxFaults`-th
fault (3) within `FaultWindowSeconds` (3600 s of world time) disables it for good:
`DesiredEnabled` off, `ErrorCode` `disabled after 3 faults: ...`, no retry. A failed retry
login counts as a fault, and so does a failed restore at startup (for example a bot saved on a
map outside `AllowedMaps`): before, that cleared `DesiredEnabled` too. A failed operator
`.playerbot start` is still reported and not retried. `.playerbot start` or `.playerbot stop` clears the quarantine and the
fault history, and wins over a retry already scheduled: the retry re-checks under the operation lock that the bot is still
desired and still in that quarantine, and otherwise ends (`quarantine-cleared`) without logging the bot in. The stored
`ErrorCode` (echoed by `.playerbot` status as `error=`) has control characters of the exception message replaced by spaces
and is cut to 128 UTF-16 units without splitting a surrogate pair. A world restart restores every desired bot, quarantined ones included (the
fault history is per process). Before 2026-10-07 one fault set `DesiredEnabled` off, so a
single transient bug removed a bot until an operator noticed:
`docs/integration/playerbot-faults-20261007.md`. Ordinary session closes that are not faults
(a GM kick) still stop the bot and clear `DesiredEnabled`, as before.

### Party bots (a real player's group)

`Playerbots/Party/` (vmangos `src/game/PlayerBots/PartyBotAI.cpp`; the player-facing actions of mangoszero's
`modules/Bots/playerbot/strategy/actions`). A player can invite a bot; it then follows, assists, obeys its master and goes
where the master goes.

- **Intake.** Every world tick for a grouped bot, and once per think interval for an autonomous bot in no group (a scripted
  controller drains its own queue), `PlayerbotPartyAI.Intake` drains only SMSG_GROUP_INVITE, SMSG_MESSAGECHAT,
  SMSG_LOOT_START_ROLL and SMSG_RESURRECT_REQUEST from the bot's capture queue (a filtered drain; an unfiltered one would steal
  other components' packets). The answers are a client's own replies and do not wait for the shared action budget. A pending
  invitation is also read from the group state, so one whose packet the 128-entry drop-oldest capture queue evicted is still
  answered; an evicted SMSG_LOOT_START_ROLL is not (that roll waits out its timer).
- **Invitations** are accepted (CMSG_GROUP_ACCEPT) or declined (CMSG_GROUP_DECLINE, which tells the inviter) by
  `World:Playerbots:Party:InvitePolicy`: `None` (only the `Allowlist`), `GuildOrFriends` (default: the allowlist, the bot's guild
  mates and players on the bot's own friend list) or `Anyone`. A player's own friend list does not count: anyone can put any bot
  on it with one CMSG_ADD_FRIEND, so it shows no consent. `.playerbot invite <bot>` (GameMaster; the
  vmangos `.partybot add` analogue) invites a running autonomous bot into the GM's group through the ordinary invite and makes it
  accept whatever its policy says; the invite keeps every ordinary rule (faction, full group, leader or assistant).
- **Master.** The group's leader when it is a real player online (a socket client, never a managed bot), otherwise the first
  real player online in member order (vmangos `GetPartyLeader`). While the bot has one, `ManagedPlayerbotFeature` ticks its
  party AI instead of `PlayerbotBrain` (a scripted controller still wins). Out of the group the brain drives it again, a fresh
  one (the old one's routes and targets belong to another place). When every real player of the group is offline or gone, the
  bot waits `MasterTimeoutSeconds` (60, 1..3600), then leaves the group (CMSG_GROUP_DISBAND, vmangos `requestRemoval`).
- **Out of combat** the bot follows at 2-5 yards at a random angle (`PlayerbotNavigation.TryPlan`/`TryAdvance`), eats and drinks
  through `PlayerbotConsumables` while its master is within 30 yards, and teleports to a master more than 100 yards away or on
  another map (`TeleportToLeader`, default on; vmangos `.goname`, only while the bot is out of combat, as vmangos does it inside
  its `!IsInCombat()` block; in combat it walks after a far master on its map) through the teleport service the GM
  `.goname`/`.namego` commands use, so the map resolver's instance rules and the group's instance bind decide where it lands
  (into the master's instance). It lands on the master's spot, not 5 yards above as `.goname` puts a GM: it has no client to
  fall. A master on a map outside `AllowedMaps`, on a taxi flight (vmangos idles then), or in another instance of the bot's own
  map id (the service teleports within one map id by a near teleport, which keeps the bot's instance) is waited for, not
  followed. A refused teleport is retried after 10 s.
- **Combat** (vmangos `SelectAttackTarget`/`SelectPartyAttackTarget`): the target the master ordered, else the master's
  victim, else whoever attacks the bot, else whoever attacks another member within 50 yards; `PlayerbotCombatSpells`, then a
  chase to 4 yards and CMSG_ATTACKSWING.
- **Round-robin loot.** vmangos PartyBotAI.cpp:565-585 unassigns the bot's round-robin loot on every party kill
  (SMSG_PARTYKILLLOG, broadcast to the group there). ArcaneCore sends that packet to the killer alone, so at every think out of
  combat the bot looks over the corpses it can see (within 74 yards, the group reward distance) for loot it holds
  (`LootBag.Owner`), whoever made the kill and whatever the bot was doing (fighting another mob of the pack, passive, staying).
  It walks up, opens each and releases it untouched (CMSG_LOOT, CMSG_LOOT_RELEASE), and the loot service opens the leftovers to
  the group. A staying bot walks back to its place afterwards. A release the shared action budget held back is tried on the
  next think; a corpse it could not reach or open three times is left.
- **Loot rolls** are answered at once with `LootRoll` (`Pass` by default, or `Greed`; never need), so a roll never waits out its
  timer for a bot (mangoszero `LootRollAction`).
- **Dead:** a group member's resurrection is accepted (CMSG_RESURRECT_RESPONSE; a stranger's declined). With `AutoRevive`
  (default on) the bot revives in place at half health when vmangos `ShouldAutoRevive` allows it (a released ghost; or nobody
  fighting, no living healer class to resurrect it, and a living member within 15 yards), and otherwise waits up to 2 minutes
  (vmangos would wait for ever and never releases a party bot outside battlegrounds); then, or without `AutoRevive`, it
  releases and runs back to its body like the brain (`PlayerbotRecovery`), to the end: the released ghost is not revived at the
  graveyard.
- **Commands**, by whisper or party chat and only from the master, one word, any case: `follow`, `stay` (hold this place: no
  following, no teleport, fight back only what attacks the bot), `attack` (the master's current target), `stop`/`passive`
  (stop fighting, follow without attacking), `come` (walk to the master, then hold there), `status` (a whisper back: level,
  health %, mana % and the current activity) and `leave`. A command sent right after the bot joined counts even before the
  bot's first turn in the action budget. Each is acknowledged by whisper (CMSG_MESSAGECHAT in the bot's own
  language). With [bot chat](#bot-chat) on (the default) any other line is the chat's; with it off, a whispered word that is
  no command gets the command list and a whisper from anyone else gets one polite answer (once per sender a minute, at most 8
  senders a minute). Other bots' lines are always ignored.

`.playerbot inspect` prints `BOTINSPECT party=master:<name> mode:<follow|stay|passive>` (or `party=none`); while the party AI
drives a bot its goal is `Follow` or `Assist` (appended to the persisted `PlayerbotGoalKind`). A controller that takes over a
grouped bot (scripted mode) makes the party AI let go: no party goal, master or mode is reported while it drives, and when it
detaches a bot still grouped is engaged afresh. Brain bots do not acknowledge a
far teleport (the brain returns while the player is in no map, before `PlayerbotMovementControl`); the party AI does, so it can
follow its master into an instance.

### Bot chat

`Playerbots/Chat/`, configured under `World:Playerbots:Chat` (every key live through `.reload config`). Bots answer players who
talk to them, in character, from their real state. It is on by default with only the **built-in** provider: no network, no key,
no cost. Language-model providers are opt-in additions in front of it.

- **Triggers.** A whisper to the bot; a party (or raid) line that names the bot as a whole word ("Bob, where are you?"); a
  `/say` line within hearing that names it, from a player of the bot's own faction. `Channels` (`Whisper, Party, Say` by default)
  narrows this. Lines from other bots and from the bot itself are never answered. The one-word party commands (`follow`,
  `stay`, ...) keep their meaning and are never sent to a provider.
- **Order.** `PlayerbotPartyAI` builds the bot's facts on the world thread (name, race, class, level, zone and subzone from the
  area table, the brain's or party AI's goal and the current quest's title, the party master, and recent events seen between
  looks: a level gained, a death, a return to life, money picked up) and hands the line to `PlayerbotChat.TryAsk`: cheap checks
  and a bounded queue (`MaxQueuedRequests`, 16), never I/O. Two background workers try the providers in order and put the reply
  on a queue that `ManagedPlayerbotFeature` drains on the world thread; the bot says it through its own CMSG_MESSAGECHAT (a
  whisper back, the party line, or `/say`), so observers get the ordinary SMSG_MESSAGECHAT. A line taken by the chat but not
  answered (the cooldown, every cap spent, a full queue, every provider failing) gets no reply: the fixed replies above apply
  only with chat off or on a channel it does not answer.
- **Failover.** A provider is skipped while its key variable is unset, while it cools down, past its `MaxRepliesPerHour`, or,
  if priced, once the day's spend estimate reaches `MaxDailySpendUsd`. A failure moves on to the next provider: a 429 waits out
  its `retry-after` (at most 10 minutes), a 5xx/529, timeout (`TimeoutSeconds`, 10) or connection failure backs off 15 s
  doubling to 5 minutes, a 401/403 waits 10 minutes, another 4xx (a bad model id) 5 minutes; a refusal or an unreadable answer
  just moves on. The built-in provider is always last, so a bot whose model providers all fail still answers. No failure reaches
  the bot's behaviour: a reply that cannot be said is dropped and logged, never a fault.
- **Limits.** `PerPlayerCooldownSeconds` (8): one reply per player across all bots in that time. `MaxRepliesPerHour` per provider
  (120). `MemoryExchanges` (4): the earlier exchanges with the same player sent with a model request (at most 512 conversations
  are kept). Replies are one line of at most 255 characters with `|` (the client's link and colour escape) replaced.
- **Orders in plain language** (`NaturalLanguageCommands`, on): from the master only, "follow me", "wait here", "attack my
  target", "stop attacking", "come here" run the same party command as the one-word form (acknowledged the same way). A model
  provider returns `{"reply", "intent"}` JSON whose intent is one of `follow`, `stay`, `attack`, `stop`, `come` or `none`;
  anyone else's order gets a polite no.
- **GM.** `.playerbot chat status` (GameMaster): on/off, channels, queued, answered, unanswered, the spend estimate and cap, and
  per provider its kind, model, key variable and whether it is set (`key=ANTHROPIC_API_KEY:present`), replies this hour against
  the cap, errors, the last error class (`rate-limited`, `unavailable`, `unauthorized`, `rejected`, `refused`, `timeout`,
  `network`, `empty-reply`, `moderation-failed`) and the remaining cooldown, then a `Bot chat safety:` line (below).
  `.playerbot chat flags [player]` and `.playerbot chat pardon <player>` (GameMaster) are described under Safety and provider
  policies.

**The built-in provider** (`PlayerbotBuiltinChat`, templates in `PlayerbotChatTemplates`) reads the line's intent with a few
patterns, in this order: severe abuse (ignored), an insult (a short brush-off), "are you a bot?" (the honest answer: a bot run
by this server), an order, help, grouping ("want to group?": a yes when the bot's `InvitePolicy` would accept the player's
invitation, otherwise a polite no; already grouped says so), what it is doing or where it is going (its goal, quest title and
zone), where it is (zone and subzone, or that it does not know), its level, race and class, thanks, goodbye, a greeting, and
otherwise a short in-character line. Each answer has several phrasings plus race, class and level variants (`greeting.race.Orc`,
`who.class.Mage`, `who.level.novice` below 10, `who.level.veteran` at 60); a phrasing whose placeholder has no value is
skipped, and the same line is never said twice in a row to the same player. It follows the same triggers, cooldown and hourly
cap as the models. To add a line, add a string to the table; to add a variant, add a key. Lines say only what the bot knows of
itself and plain game mechanics, never invented lore.

**Model providers** (`Providers`, at most 8, tried in order; a listed `Builtin` entry must be last and sets its own cap):

| Key | Meaning |
|---|---|
| `Kind` | `Anthropic` (Messages API: `POST {BaseUrl}/v1/messages`, headers `x-api-key` and `anthropic-version: 2023-06-01`), `OpenAICompatible` (`POST {BaseUrl}/chat/completions`, `Authorization: Bearer` when a key is set: OpenAI, OpenRouter, Ollama, LM Studio), or `Builtin`. |
| `BaseUrl` | Anthropic: the origin (empty = `https://api.anthropic.com`). OpenAICompatible: the API base with its version (`https://api.openai.com/v1`, `https://openrouter.ai/api/v1`, `http://localhost:11434/v1`). A key is never sent over plain `http` to a host other than this machine (the configuration is refused). |
| `Model` | Empty = `claude-haiku-4-5` for Anthropic; required for OpenAICompatible. |
| `ApiKeyEnvironmentVariable` | The variable that holds the key. Unset: `ANTHROPIC_API_KEY` for Anthropic, none otherwise. `""`: a keyless local server. Keys are read only from the environment, at each request: never from configuration, never logged or shown. |
| `Headers` | Extra headers, e.g. OpenRouter's `HTTP-Referer` and `X-Title`; they cannot replace the key or version headers. |
| `MaxRepliesPerHour` | 1..100000 (120). |
| `MaxTokens` | The reply limit, 16..1024 (150). |
| `TokenLimitParameter` | OpenAICompatible: `max_tokens` (default) or `max_completion_tokens` (newer OpenAI models). |
| `InputUsdPerMillionTokens`, `OutputUsdPerMillionTokens` | Prices for the spend estimate. Unset: the built-in Anthropic price of a known model (Claude Haiku 4.5 $1/$5), otherwise unpriced. Set 0 for a local model. |
| `Moderation`, `ModerationBaseUrl`, `ModerationModel`, `ModerationApiKeyEnvironmentVariable` | The optional moderation step before a line is sent to this provider: `None` (default), `Endpoint` or `Classify` (see Safety and provider policies). |

The prompt's system part comes in two pieces: the rules (1.12 setting, short in-game chat style, nothing outside the game, no
claim of being an AI unless asked directly, a brush-off for abuse, the JSON answer format), identical for every bot and request,
then the bot's own facts. Anthropic gets the rules as a separate system block with `cache_control`; OpenAI-compatible endpoints
get one system message that starts with them, so providers that cache prompt prefixes can. Claude Haiku 4.5 caches only a
prefix of 4096 tokens or more, and the rules are far shorter, so in practice nothing is cached there today; the breakpoint costs
nothing.

A local model first, the cloud as a fallback, the templates last (a local model costs nothing; `OPENROUTER_API_KEY` set in the
server's environment; the OpenRouter prices are an example, check the provider's current price):

```json
"Chat": {
  "Providers": [
    { "Kind": "OpenAICompatible", "BaseUrl": "http://localhost:11434/v1", "Model": "qwen3:4b",
      "ApiKeyEnvironmentVariable": "", "InputUsdPerMillionTokens": 0, "OutputUsdPerMillionTokens": 0, "MaxRepliesPerHour": 1000 },
    { "Kind": "OpenAICompatible", "BaseUrl": "https://openrouter.ai/api/v1", "Model": "openai/gpt-4o-mini",
      "ApiKeyEnvironmentVariable": "OPENROUTER_API_KEY", "InputUsdPerMillionTokens": 0.15, "OutputUsdPerMillionTokens": 0.6,
      "Headers": { "HTTP-Referer": "https://github.com/Krilliac/ArcaneCore", "X-Title": "ArcaneCore" } },
    { "Kind": "Anthropic", "MaxRepliesPerHour": 60 }
  ]
}
```

**Cost control.** Every model reply is one request of roughly 400-800 input tokens (the rules, the facts, up to four remembered
exchanges) and at most `MaxTokens` (150) output tokens. On Claude Haiku 4.5 ($1 input, $5 output per million tokens) that is
about $0.0015 a reply at most, so the default cap of 120 replies an hour costs at most about $0.18 an hour per provider, and the
default `MaxDailySpendUsd` of $1 stops the priced providers after roughly 650 replies in a UTC day. The estimate uses the token
counts each response reports (cache writes at 1.25x and reads at 0.1x the input price) and is an estimate, not a bill; a provider
without a known price is held back by its `MaxRepliesPerHour` alone, so give OpenRouter and OpenAI entries their prices. The
per-player cooldown keeps one player from spending the budget, the bounded queue drops rather than piles up, and the built-in
templates answer once the money or the providers run out. Tests never touch the network: `IBotChatClient` is faked, and the four
live checks in `PlayerbotChatLiveTests` (one tiny request each to Anthropic, OpenAI, OpenRouter and a local server) run only
with `ARCANECORE_TEST_LIVE_LLM=1` and that provider's key or `ARCANECORE_TEST_LOCAL_LLM_URL` set.

#### Safety and provider policies

`Playerbots/Chat/PlayerbotChatSafety*.cs`, configured under `World:Playerbots:Chat:Safety` (every key live through `.reload config`,
range-checked; see the configuration reference). A public server that sends players' lines to an external model provider is
responsible for what it sends: Anthropic, OpenAI and OpenRouter each have usage policies that apply to the requests made with the
server's key, whoever typed the words. These safeguards keep the obvious violations away from the provider and limit players who
keep trying; they reduce the risk, they do not remove it, and they are no substitute for reading the policies of the providers you
configure. Nothing here applies to the built-in provider, which sends nothing anywhere.

- **Screening before a provider sees a line** (`Enabled`, on). Every line is screened on the chat worker before the first model
  provider is tried:
  1. the **local filter** (`PlayerbotChatSafetyFilter`), by category (`Categories`, all by default): `SexualMinors`, `SelfHarm`,
     `Threats` (real-world: "I know where you live", doxxing, swatting), `Hate` (slurs, calls for violence against protected
     groups), `Harassment` ("kys"), `IllegalGoods` (making explosives, buying hard drugs) and `PersonalData` (email addresses,
     phone numbers, street addresses). The term and pattern lists ship in `PlayerbotChatSafetyTerms.json` (an embedded resource);
     `TermsFile` names an operator file of the same shape that is added to them (or replaces them, `ReplaceDefaultTerms`) and is
     read again within 10 seconds of a change. The shipped list is deliberately conservative: it aims at what provider policies
     forbid, not at rudeness ("noob" and "idiot" stay the built-in brush-off's business), and stays clear of words common in WoW
     chat ("kill", "die", "bomb", "gun", "naked", a "suicide pull"). Matching folds case, accents, leetspeak (`k1ll y0urs3lf`),
     spaced-out letters (`k y s`) and repeated letters (`kiiill`); terms match whole words, and a trailing `*` matches longer words.
     Patterns are compiled with `RegexOptions.NonBacktracking` (matching time linear in the line, whatever the pattern: an
     operator's pattern with a back-reference or lookaround is skipped with a warning) and a 100 ms timeout; a line the filter
     cannot judge in time goes to no model (the built-in provider answers) but costs no strike.
  2. each provider's optional **moderation step** (`Moderation` on the provider entry, `None` by default): `Endpoint` posts the
     line to an OpenAI-compatible `/moderations` endpoint (`ModerationBaseUrl`, default the provider's own `BaseUrl`;
     `ModerationModel`, default `omni-moderation-latest`; `ModerationApiKeyEnvironmentVariable`, default the provider's key), so an
     Anthropic entry can use OpenAI's moderation endpoint; `Classify` sends the provider a one-word SAFE/UNSAFE classification
     request (at most 16 output tokens, counted in the spend estimate) before the reply request. A flag keeps the line from every
     provider; a failed step skips that provider (`moderation-failed`) and the next one is tried, so a line is never sent
     unchecked.

  A flagged line reaches no model provider. `OnFlagged` (`BuiltinReply`) answers it with the built-in brush-off (for `SelfHarm`, a
  short line suggesting a trusted person or a local crisis line; for `PersonalData`, a caution), or nothing (`Ignore`). It is not
  remembered either, so it is never sent later as conversation memory.
- **Screening what a model says** (`ScreenOutput`, on). A model's reply is run through the same local filter before the bot says
  it; a hit is never said: the built-in provider answers the player's line instead (`OnOutputFlagged`: `BuiltinReply`) or nothing
  (`Drop`). The flag is recorded with the reply's excerpt and is no strike against the player.
- **Flags, strikes and cut-off.** Every flag is kept in an in-memory ring of `FlagLogSize` (200) records and logged as a warning:
  the character's name and GUID, the account id, the bot, the category, the source (local filter, moderation or output
  screening), the time, and, while `StoreExcerpt` is on, at most 60 characters of the line with emails, phone numbers and addresses
  replaced. There is no persistent flag table (that would need a schema change): the log warnings are the durable record, and the
  ring, strikes, cut-offs and players' choices are lost at a restart. A flag of `SexualMinors`, `Threats`, `Hate`, `Harassment`,
  `IllegalGoods` or the moderation step is a strike (`SelfHarm` and `PersonalData` are not); strikes older than
  `StrikeWindowMinutes` (60) fall away. `StrikesBeforeCutoff` (3) strikes within the window cut the player off from the model
  providers for `CutoffMinutes` (60): built-in replies only. With `AutoMute` (off) the cut-off also mutes the player's chat for
  `AutoMuteMinutes` (30) through the account mute of `.mute` (persisted, set by "Bot chat safety" at GameMaster level, lifted by
  `.unmute`; a longer mute already in force is kept).
- **GM.** `.playerbot chat flags [player]` lists the newest 15 flag records (one player's when named, after a line with their
  strikes, remaining cut-off and AI choice); `.playerbot chat pardon <player>` clears a player's strikes and cut-off (not a mute).
  `.playerbot chat status` ends with `Bot chat safety: screening=on screened=.. flagged=.. moderation-flagged=..
  output-flagged=.. builtin-only=.. cut-offs=.. cut-off-now=.. auto-mutes=.. opted-out=.. opted-in=.. disclosed=..` and the
  filter's size.
- **Disclosure and choice.** While a model provider is configured, a player who talks to a bot gets, once per login and before
  the first reply, a system message: `DisclosureText` ("Bot replies on this server may be written by an AI service, which
  receives what you say to bots.") plus how to opt out (`Disclosure`, on). With `AllowOptOut` (on), whispering any bot `ai off`
  limits that player to built-in replies, `ai on` undoes it and `ai` tells them where they stand; these whispers are answered by
  the safety itself and never sent to a provider. `RequireOptIn` (off) turns it around: the model providers answer only players
  who whispered `ai on`, and the disclosure says so. A player's choice lasts until the server restarts.
- **What is sent.** A model request carries the bot's facts, the bounded memory (`MemoryExchanges`) and the current line, with
  players named only by character name: never an account name, an address or a GUID. With `StripPersonalData` (on), email
  addresses, phone numbers and street addresses in the line and the memory are replaced by `[email]`, `[phone]` and `[address]`
  (this matters when `PersonalData` is left out of `Categories`; otherwise such a line is flagged before it gets that far).

**Operator responsibilities.** Read and follow the usage policies of every provider you configure, and keep the server's own
rules in line with them. Keep `Enabled` on whenever a model provider is configured (the start log warns when it is off). Review
`.playerbot chat flags` and the log warnings, extend the list through `TermsFile` for what your players actually say, and consider
a provider's moderation step on a busy public server. Tell your players, in your server's own terms, that bot chat may be
processed by a third-party AI service. The screening is a set of word lists and patterns: it will miss some lines and flag a few
harmless ones, and it says nothing about what a provider will accept.

## Scenario harness

Namespace `ArcaneCore.World.Playerbots.Scenarios`. A scenario is an `IPlayerbotScenario`
(`Name`, `Description`, `RunAsync(ScenarioContext)`) that logs managed bots in scripted mode
and drives them step by step against the real world handlers, asserting server state.
`ScenarioRunner.RunAsync(scenario, bots, world, services, clock, options)` runs it and returns
a `ScenarioReport`; afterwards it stops (logs out and saves) the bots, or returns them to
autonomous mode (`ScenarioRunOptions.StopBotsAfterRun`).

**Bots.** `ScenarioContext.LoginAsync(name, race, class)` reuses the bot of that name or
creates one, and starts it scripted. `ScenarioBot` is the controller: it records every packet
it sends and every packet its session captures (`ScenarioPacketLog`, run-wide sequence
numbers; the tap sits before the session's bounded drain queue, so bursts cannot evict a
reply) and, by default, acknowledges server teleports and movement orders like a client.

**Typed client actions** (`ScenarioBot`; each runs one CMSG through its handler and the bool
is transport admission only, outcomes come from replies or state): `TargetAsync`,
`AttackAsync` (selection + swing), `StopAttackAsync`, `CastAsync`; `LootAsync`,
`LootMoneyAsync`, `LootItemAsync`, `ReleaseLootAsync`, `UseGameObjectAsync`; `InviteAsync`,
`AcceptInviteAsync`, `DeclineInviteAsync`, `LeaveGroupAsync`, `SetLootMethodAsync`;
`InitiateTradeAsync`, `BeginTradeAsync`, `SetTradeItemAsync` (item GUID; bag and slot are
resolved), `SetTradeGoldAsync`, `AcceptTradeAsync`, `CancelTradeAsync`; `SendMailAsync`,
`GetMailListAsync`, `TakeMailItemAsync`, `TakeMailMoneyAsync`; `RequestDuelAsync` (spell 7266),
`AcceptDuelAsync`, `CancelDuelAsync`; `SayAsync`, `PartyAsync`, `WhisperAsync`, `ChatAsync`
(in the bot's team language: the server refuses Universal outside AFK/DND); `QuestHelloAsync`,
`AcceptQuestAsync`, `CompleteQuestAsync`, `RequestQuestRewardAsync`, `ChooseQuestRewardAsync`;
`AreaTriggerAsync` (instance entry: the far teleport is then acknowledged automatically);
`SendAsync(opcode, payload)` for anything else. Payload layouts are in `ScenarioPackets` and
follow the server's own handler parsing. The MockClient keeps its own independent encodings
on purpose (it is a second oracle), so the builders are not shared with it.
Ships (`ScenarioTransports`, docs/areas/transports.md): `context.ShipAsync(entry)` finds a route's ship,
`BoardAsync(ship, x, y, z)` sends a heartbeat standing on it at that offset, `LeaveShipAsync()` one without it,
`TimeSkippedAsync(ms)` a CMSG_MOVE_TIME_SKIPPED; `ScenarioTransports.TransferPending` and `NewWorld` decode the
map-change packets.

**Typed decoders** (`ScenarioDecoders`, mirroring the server writers): group list, party
command result, group invite, trade status, mail result and mail-list count, duel requested,
duel complete, duel winner, loot response, loot-money share, attacker state update, spell go,
cast result, spell failure, chat message, XP gain, quest kill update and quest complete. The proc engine's packets have their own
decoders (`ScenarioProcDecoders`: SMSG_SPELLDAMAGESHIELD, SMSG_PROCRESIST), and so have the class scripts' (`ScenarioClassDecoders`:
SMSG_SPELLNONMELEEDAMAGELOG, SMSG_PERIODICAURALOG).
`ScenarioBot.WaitForPacketAsync(opcode, decoder, match, since)` waits for a decoded reply
received after a `Mark()`.

**Setup helpers** (`ScenarioContext`, world thread, harness only): `PlaceAsync` (an ordinary
teleport, waited until acknowledged and arrived), `PlaceFacingAsync` (two bots face to face,
two yards apart), `GiveItemAsync`, `GiveMoneyAsync`, `LearnSpellAsync`, `SetHealthAsync`. They
exist only on the harness object, which only tests and the Administrator-only, config-gated
scenario command construct; no opcode or player command reaches them.

**Steps, waits, assertions, report.** `StepAsync(name, body)` records each step's wall and
game time; the first failing step ends the run. `WaitUntilAsync(what, condition, timeout)`
evaluates the condition on the world thread and is always bounded. `Expect`, `ExpectEqual`,
`ExpectAsync(bot, fact)`, `ExpectMoneyAsync`, `ExpectItemCountAsync`, `ExpectGroupAsync` (server
roster) and `QuestStateAsync` assert server state. `ScenarioReport` lists the steps (ok/FAIL,
wall ms, game ms) and, on failure, the failing step, the message and each bot's last relevant
packets (movement and object-update noise filtered).

**Deterministic clock.** `WorldRuntime.UseManualClock(stepMs = 50)` is a code-only test seam
(chosen before `Start`, never configuration). Game time (`NowMs`, `Uptime`, tick diffs) then
advances only through `AdvanceClockAsync(ms)` or `AdvanceClockUntilAsync(max, condition)`, which
run ticks of at most the step back to back and check the condition after every tick. Posted
commands keep running every tick interval with a zero diff, so sessions and `InvokeAsync` work
while the simulation stands still. `ScenarioClock.Manual(world, time)` drives it; a
`ScenarioTimeProvider` registered as the host's `TimeProvider` follows game time tick by tick
(mail delay, trade anti-scam window, duel countdown) and can jump ahead (`Advance`). On the
manual clock a wait's timeout is game time, after which it keeps polling without advancing until
the same timeout has also passed in wall time (and at least `ManualWallGrace`, 3 s, after the
game budget ran out): the game budget burns in well under a second, while asynchronous I/O such
as a database commit runs on real time and can take seconds on a loaded machine. Code that reads
`DateTime` or `Stopwatch` directly, rather than the world clock or `TimeProvider`, does not
follow the manual clock. `ScenarioClock.Real` (live server) polls in wall time.
A production wall-clock budget shorter than that wait defeats it: the quest reward, economy and
dungeon chest loot settlements give up after 5 s and answer with their failure reply (a loaded
SQLite file made the AV scraps turn-in and an auction cancel miss it), so `ScenarioTestWorld` raises
all three settlement budgets to its 30 s step timeout.

**Built-in scenarios** (`PlayerbotScenarioCatalog`; bots `Scnalpha` and `Scnbeta`, created on
first use): `smoke` (login, hear own /say), `group-chat` (invite, accept, both group lists and
the server roster, party chat, leave), `trade` (Linen Cloth 2589 for 75 copper through the
trade window; both inventories and purses), `duel` (spell 7266, accept, countdown, melee to the
1-health finish; SMSG_DUEL_WINNER checked against server state). After them come the other public
scenarios this assembly ships (`PlayerbotScenarioCatalog.Shipped`, discovered: `dungeon`, `ship`, `wsg`), then
`IPlayerbotScenario` services registered in DI; a name belongs to its first entry. Shipped content
scenarios need content a live world may not have and then fail at a named step. `ScenarioSteps` holds reusable blocks (form a group, open and
accept a trade, leave earlier groups).

**Battlegrounds** (`ScenarioBattlegrounds`, kept out of the shared harness files): bot actions `BattlemasterHelloAsync`,
`JoinBattlegroundAsync` (CMSG_BATTLEMASTER_JOIN), `PortBattlegroundAsync`, `LeaveBattlefieldAsync`, `BattlefieldStatusAsync`, `PvpLogDataAsync`,
`PlayerPositionsAsync`, `CancelAuraAsync`; decoders for SMSG_BATTLEFIELD_STATUS, MSG_PVP_LOG_DATA, SMSG_UPDATE_WORLD_STATE and
MSG_BATTLEGROUND_PLAYER_POSITIONS; lookups of a battlemaster spawn of a type (`battlemaster_entry`), a game object spawn and an area trigger. The
scenario `wsg` (`WarsongGulchScenario`, listed by `.playerbot scenario list` through the shipped-scenario discovery) logs in a human and an
orc warrior, queues each at a battlemaster of its continent, ports both into one match, waits out the two-minute start, captures the Horde flag,
drops the Alliance flag by cancelling the flag aura, returns it, captures twice more (SMSG_BATTLEFIELD_WIN / _LOSE, the final scoreboard) and
waits until both bots are back at their entry points. `WarsongGulchScenario.EnterMatchAsync` is the reusable opening. On a live
world it needs the Warsong Gulch content (map 489, its triggers, safe locations, flag objects and battlemasters), a battleground
template of one player per team (the retail minimum is five, so two bots never start a match otherwise) and
`World:Playerbots:Scenarios:MaxDurationSeconds` of about 600 (two 2-minute waits plus the captures).

**Dungeons** (`ScenarioDungeons`): `RaiseLevelAsync` (ordinary level-up to an entrance's level) and `TakeAreaTriggerAsync` (stand
in a trigger, send CMSG_AREATRIGGER, wait for the far teleport). The scenario `dungeon` (`DungeonEntryScenario`) groups `Scnalpha`
and `Scnbeta`, raises them to level 10, sends the leader through The Deadmines entrance (trigger 78, map 36) into a new instance
that the group is bound to (non-permanent), sends the member through the same trigger into the same instance, checks party chat
inside, and, when `AllowedMaps` lists map 36, logs the member out inside and back in into the same instance (a managed bot may
only log in on an allowed map). Both leave through the exit trigger 119 and the group is disbanded. It needs map 36 and triggers
78 and 119 with their teleports (`map_template`, `areatrigger_template`, `areatrigger_teleport`) and fails at its first step
naming what is missing. On the live content it passes once the world database has been refreshed with Map.dbc and AreaTable.dbc
(`DungeonImportedMapsTests` runs it on the importer's own output; docs/integration/instance-maps-20261008.md). If a step fails after the entrance (the member refused, party chat, the exit), the scenario still
brings every bot that is inside back to where it stood before the entrance and disbands the group (`cleanup: ...` steps, run
under their own bound even after the run's deadline): otherwise the shared bots would stay saved on map 36, the default
`AllowedMaps` [0, 1] would refuse their next login (`login-refused`), and every pair scenario would stop working. A bot that is
offline at that point (a failed relog inside, only tried when `AllowedMaps` lists 36) cannot be moved, and its login there is
allowed. It leaves both scenario bots at level 10 or more. Tests: `DungeonScenarioTests` (including a dungeon that admits one
player, so the member is refused inside the run).

**Ships** (`ScenarioTransports`): `ShipAsync`, `BoardAsync` (a heartbeat carrying ONTRANSPORT, the ship and an offset),
`LeaveShipAsync`, `TimeSkippedAsync`, and the SMSG_TRANSFER_PENDING / SMSG_NEW_WORLD decoders. The scenario `ship`
(`ShipCrossingScenario`) waits for the Ratchet - Booty Bay boat (`gameobject_template` 20808) at one of its ports, places
`Scnalpha` on the dock, boards it at a fixed deck offset, waits for the map change (SMSG_TRANSFER_PENDING naming the ship and the
old map, SMSG_NEW_WORLD carrying the offset, the bot aboard on the other continent), waits for the other port (told apart by its
TaxiPathNode row), checks the bot is at its deck offset, and steps it off within 30 yards of the port. A cleanup step takes the
bot off the ship and back to where it stood, also after a failure. It needs `World:Transports:Enabled` and the ship content
([transports](transports.md)); on the real clock it takes up to one round trip (about six minutes), so
`World:Playerbots:Scenarios:MaxDurationSeconds` must be 600 for `.playerbot scenario run ship`. Tests: `TransportScenarioTests`
(synthetic boat), `RealTransportContentTests.ABot_RidesTheRealBootyBayBoat_...` (real content and terrain, env-gated).

### Running scenarios on a live server

`.playerbot scenario list` and `.playerbot scenario run <name>` (Administrator) run a registered
scenario against the running world on the real clock and print the report lines to the GM.
Both are refused unless `World:Playerbots:Enabled` and `World:Playerbots:Scenarios:Enabled` are
true (default false). `World:Playerbots:Scenarios:MaxDurationSeconds` (120, 5..600) and
`StepTimeoutSeconds` (20, 1..300) bound a run; one run at a time. Scenario bots are real,
persistent characters (running, they count against `MaxBots`; registered, against `MaxRegisteredBots`); they are placed where `Scnalpha`
stands, and the setup helpers change their money, items and health. Enable it only on test
realms.

### Scenario tests

`tests/ArcaneCore.World.Tests/Playerbots/Scenarios/`. `ScenarioTestWorld` is a `WorldTestHost`
with the manual clock, a `ScenarioTimeProvider`, a SQLite character database (bots, items,
mail, quests and spells persist through the real EF stores) and small synthetic content
(`ScenarioTestContent`: a mailbox and a quest giver at the human start, a hostile wolf with
loot and a kobold quest target 60+ yards away, the Duel spell and flag, faction templates).
The tests run the built-ins plus `proc-damage-shield` and `proc-reflect-duel` (`ProcScenarioTests`, docs/areas/procs.md), `class-seal-judgement` and
`class-consecration` (`ClassScriptScenarioTests`, docs/areas/class-scripts.md), `control-possess`, `control-charm` and `spirit-of-redemption`
(`UnitControlScenarioTests`, docs/areas/unit-control.md; decoders and client packets in `ScenarioControlPackets`), `group-loot` (group, free-for-all loot, kill, money split,
item), `mail-item` (persisted letter with item, delivery delay, take), `melee-kill` (swing,
kill, XP credit) and `kill-quest` (accept, kill credit, turn in, settled reward row), and
check database rows after the run. Setting `ARCANE_SCENARIO_REPORT_DIR` collects every report.
`CombatStatScenarioTests` runs `combat-stat-auras` (duel, a damage taken curse and an attacker hit buff cast through CMSG_CAST_SPELL, white
swings that must all land for tenfold damage); its two spells are installed by swapping the spell store on the world thread.
`ScenarioTestWorld.StartAsync(configure)` registers extra services after the synthetic content (a later store registration
replaces it). `GameObjectScenarioTests` uses it for `meeting-stone` (a party queued at a meeting stone takes in a solo bot,
which later leaves) and `ritual-of-summoning` (a warlock's ritual, two helpers, a far bot that accepts the summon); the
meeting stone actions and decoders are in `ScenarioMeetingStones` (`JoinMeetingStoneAsync`, `LeaveMeetingStoneAsync`,
`MeetingStoneInfoAsync`, `SetQueue`, `MemberAdded`, `JoinFailed`). The scenario content only supports human warriors:
creating a human priest (5) or warlock (9) bot there fails with `create-failed`.
`ScenarioTestWorld.StartAsync(configure)` also lets a test register its own content and seams after
`ScenarioTestContent` (a later registration wins). `CreatureAiScenarioTests` uses it for creature AI across sessions: an
orc bot walks up to a CALLS_GUARDS townsman, a human bot hears the shout and the guard post's guard runs to the orc and
swings (`SMSG_ATTACKERSTATEUPDATE`); the human waves (`ScenarioCreatureActions.TextEmoteAsync`, CMSG_TEXT_EMOTE) at a herald
whose EventAI RECEIVE_EMOTE row greets it by name (`ScenarioCreatureDecoders.MonsterChat`).

Teleport and death lane (wave 2): `pet-teleport` (`PetTeleportScenarioTests`: a hunter bot's pet comes back at its side after a far
teleport to Kalimdor, the hunter gets its pet bar again and a watcher bot's client is sent the pet; the content creates warriors only,
so the bot's class byte is set to hunter on the world thread) and `raid-lock` (`RaidLockScenarioTests`: two bots form a raid group with
CMSG_GROUP_RAID_CONVERT, Molten Core is added to the map registry on the world thread, the leader is locked inside, the stored
`group_instance` row is read back from SQLite, and the member who was outside enters the same instance and is locked too).
`BattlegroundScenarioTests` and `BattlegroundWorldScenarioTests` start the scenario world with `WarsongGulchTestContent` (map 489, its safe
locations, flag stands with their event rows, flag room triggers, flag auras, a battlemaster per side) through the `StartAsync(configure)` hook.
`TransportScenarioTests` use the same hook for synthetic ship routes (`TransportWorldContent`): `ship-duel` (two
bots board a ferry, duel aboard while it sails away from the flag, and the duel ends fled when one steps off) and
`ship-crossing` (a bot rides a ship through its map change and arrives aboard on map 1).
`PartyScenarioTests` runs `party-master` (`Scenarios/ScenarioParty.cs`, `PartyScenario`): the master is a real socket client
(`IPartyScenarioMaster`, a `WorldTestClient` whose reader records every packet and acknowledges teleports like a game client) and
the bot `Scnfollower` runs autonomously. The master, on the test's `World:Playerbots:Party:Allowlist`, invites the bot and it
accepts; the bot follows a 40-yard walk;
the master targets the wolf and whispers `attack`, and the bot kills it; the group roll on the wolf's uncommon item gets the
bot's vote within 2 s of game time and resolves on the master's vote; after `stay` the bot holds while the master walks off;
`status` is answered; the master takes the Deadmines entrance (trigger 78) and the bot lands in the same instance, and comes
out with it through the exit (119); the master leaves the group and the brain drives the bot again. The test adds the
Deadmines content, an uncommon item to the wolf's loot, AllowedMaps [0, 1, 36] and flat ground at the start's height (the bot
plans its walks there). Without a master (the catalog's instance, `.playerbot scenario run party-master`) it fails at its first
step: a live run has no socket master to give it.

## MockClient playbot (external protocol client)

`arcane-mock playbot` runs one external build-5875 client against an owned numeric
loopback realm. It uses the real SRP, world authentication, character creation,
login, gameplay and logout handlers. Use a disposable Player account and an
explicit character name; the first repertoire creates/selects a Human Warrior.

```powershell
# Set ARCANE_BOT_PASSWORD privately before running; no password argument is accepted.
dotnet <verified-artifact-path>/arcane-mock.dll playbot --account BOTONE --character Botone --password-env ARCANE_BOT_PASSWORD --duration-s 120 --steps 100 --attack-entry 6 --report <new-report-path>.json
```

The deterministic selector queries nearby observed creatures, explores using
three-yard movement steps and approaches an explicitly allowed creature entry.
Combat is disabled when `--attack-entry` is omitted or zero. It waits when player
health/combat facts are unknown, stops its attack
when injured or its target disappears/dies, and releases spirit once after an
observed player death. A nearby configured NPC that is observed targeting this
character with its full GUID and combat flags can be answered defensively.
Below 60 percent health outside combat, an observed owned starter-food item117
with positive stack can be used through the normal sit/item-use flow. The client
waits for own spell433, food aura, one consumed stack and healing, then stands.
That action shares the same reader and the overall session traffic budget.
Heroic Strike requires spell 78 in the observed initial
spellbook and at least 150 raw rage. Loot comes from an observed nearby lootable
corpse and a decoded server loot window. Reports distinguish actions sent from
packet families received; movement sends alone do not establish terrain acceptance.

The action loop adapts to received state and chooses again after each observation
window. It does not run the fixed `starting-zone` quest script. Current coverage
does not include quest chains, terrain navigation, general food selection, corpse recovery,
general spell planning or arbitrary classes. Local exploration stays within 30
yards of the login origin and 20 exploration/approach movement sends. Approach
targets must be within 25 yards and two yards vertically. The runtime bounds
duration to 1..600 seconds, actions to 1..500, observed objects to 4096, and
post-authentication traffic to 10000 frames/eight MiB. Logout has a separate
30-second cleanup bound. Each process owns one connection and one frame reader.
Run a second client with a separate disposable account/character rather than
sharing a character across processes.

## Optional installed local model

Add `--llm true --model qwen3.5:4b`. The model provider defaults to
`http://127.0.0.1:11435/`; `--model-endpoint` accepts another literal loopback HTTP
origin. It uses the installed Ollama model and never calls a model download API.
Warm and decision requests use a 30-second idle lease. This reduces idle
retention; it does not lower peak loading memory. The observed qwen3.5:4b runner
committed about11.8GiB on this Windows host despite a2048-token context, so model
loading and builds need separate resource admission. The default selector works
without loading it.
Explicit loading has a 30-second bound; ordinary decisions have five seconds.
Generation disables thinking/streaming, uses a 2048-token context, limits output
to 96 tokens, and requests a JSON schema containing only supplied action IDs.
Responses are bounded to 16 KiB and validated locally. Credentials, character
names, chat, coordinates, proprietary DBC data and raw logs are excluded from
the model request. Proxying and redirects are disabled.

The model selects from deterministic candidates; it cannot introduce commands,
packets, targets or coordinates. The client drains incoming traffic and validates
the selected candidate again before sending. Provider failures, malformed or
stale selections use the deterministic fallback. Model loading failure also
uses the baseline. LLM operation is disabled by default. The existing eight-case
local selector benchmark establishes a useful candidate, not general gameplay
accuracy; actual client qualification and live reports are separate evidence.

Reports contain action kinds/providers/fallback codes, numeric target/health
facts, traffic counts, received packet families and logout status. They exclude
account/password values and server chat. `--report` creates a new file and refuses
an existing path, preserving earlier evidence. Standard output also receives the
JSON report; a run/traffic/protocol or logout failure returns a nonzero exit code.

Protocol layouts reuse `ScenarioConnection`, `ScenarioWire`, `StartingZoneProbe`,
`StartingZoneLoot` and the normal client protocol classes. Their pinned
vmangos/wow_messages citations remain authoritative for build 5875. WoWWiki
1.12.1 and Wowhead Classic are supplemental version-checked information sources;
later Classic mechanics are not automatically treated as 1.12.1 behavior.
