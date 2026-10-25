# Playerbots: social, cooperative and learning behaviour (design proposal)

Status: approved 2026-10-23; P1 implemented in this change. File references are to the code on the w43 integration branch (`src/ArcaneCore.Playerbots`, options in `src/ArcaneCore.World.Contracts/Playerbots`).

## 1. What bots already do (so we build on it, not beside it)

| Area | Exists today | Where |
|---|---|---|
| Answering players | Every channel; ~30 template intents filled with real facts; optional Anthropic / OpenAI-compatible / in-process GGUF model; 16-exchange memory per (bot, player) | `Chat/PlayerbotChat.cs`, `Chat/PlayerbotBuiltinChat.cs`, `Party/PlayerbotPartyAI.cs:372` |
| Talking on their own | LFG/LFM, WTS/WTB, party needs, run plans, guild remarks, greetings to players within 8 yd | `Chat/PlayerbotNeedsVoice.cs`, `Social/PlayerbotGuildChat.cs`, `Social/PlayerbotSocialEmotes.cs` |
| Bot ↔ bot talk | Scripted exchanges on real topics (ding, boss, loot, quest…), template-only, only where a real player can hear | `Social/PlayerbotConversations.cs` |
| Grouping | Bots sharing the same goal (elite/dungeon/raid quest, instance-only drop, "too strong alone" risk signal) auto-form role-balanced groups; dungeon/raid runs; leader handoff from a player | `Groups/PlayerbotGroupCoordinator*.cs`, `Risk/PlayerbotRiskModel.cs:242` |
| Difficulty sense | Per-target risk model (adds, patrols, elites, lethal spells, danger memory) → Engage / Detour / Avoid / "needs a group of N" | `Risk/PlayerbotRisk.cs` |
| Helping | Only when the bot's own target turns out to be another player's tagged mob and that player is losing | `PlayerbotBrain.cs:2680-2790` |
| Trading | Accepts trades from master/group; never gives items; only bot↔bot trade is the mage portal fee | `Party/PlayerbotPartyAI.cs:700`, `PlayerbotMageService.cs:777` |
| Persistence | Lifecycle, home, population, set-asides. Chat memory, strikes, opt-ins, danger memory: **in memory only** | `Data/Characters/Playerbots/*` |

## 2. Gaps against the request

1. **Bots never start a conversation with a player** or follow one up; every whisper is a reply. No goal-directed questions.
2. **No memory of people**: no affinity, trust or familiarity with players or other bots; everything social is lost on restart.
3. **Grouping only among bots that already share a goal key**: a nearby bot without the quest is never recruited; bots ignore each other's LFG; guildmates don't group; bots don't invite players by default, and decline strangers.
4. **No real trading**: no bot↔bot item trade, no handing gear to a groupmate, WTS posts never complete a sale.
5. **Reactive only in narrow cases**: no scan for players in trouble, no buffing or resurrecting strangers, little comment on surroundings or events.
6. **No learning from answers**: nothing asks, nothing stores what it was told, nothing checks whether it was true.

## 3. Hard constraints the design keeps

- **No wire-format change** (charter, `CLAUDE.md:23`): every new behaviour is an existing CMSG on the bot's own session (chat, trade, invite, cast).
- **Layering**: all behaviour in `ArcaneCore.Playerbots`; storage contracts in `Kernel`, EF stores + `*DataModule` in `Data` (next schema version after 59); options under `World:Playerbots`.
- **Policy data rule** (`docs/design/playerbot-policy-phases.md:7`): no label, reward or training example may come from an LLM. "Learning" below is a **fact and relationship memory**, never model training; an LLM may at most *parse* a player's answer into a structured claim, and every claim is checked against server ground truth before it changes behaviour.
- **Chat safety stays in charge**: `RequireOptIn`, "ai off", strike/mute, AI disclosure, speech limiter and per-player budgets apply to every new line. Anything a bot starts unprompted toward a specific player is opt-in (see decision D2).
- **Cost**: everything runs from the existing 1 s social pass or the think budget; models stay optional, templates remain the default and the fallback.

## 4. Design

### 4.1 Social memory (foundation for everything else)

New `PlayerbotSocialMemory` (Playerbots/Social) with a persisted store `IPlayerbotRelationStore` → table `playerbot_relation`:

| Column | Meaning |
|---|---|
| BotId, OtherGuid, OtherIsBot | the pair |
| Affinity (-100..100) | liking; rises with grouping, helping, trades, friendly chat; falls with insults, griefing, being kicked, chat strikes |
| Familiarity (0..) | how often they've met; decays slowly |
| Trust (0..1) | reliability of what the other told it (see 4.5) and of deals |
| Flags | prefers-tank/healer, wants invites, asked-not-to-whisper, owes-favour |
| LastSeenUnixMs, LastTopic | for greetings and follow-ups ("did you find the trainer?") |

Bounded per bot (top-N by familiarity, LRU eviction), atomic per-bot replace like the set-aside store. Fed by events that already exist: `PlayerbotChat.LineHeard` / `SpeechSettled`, group formed/disbanded/wiped, trades, help given/received, chat safety strikes.

Also persist a short **conversation summary** per (bot, player) (last topic + 1-2 remembered facts, template-built, ≤ 200 chars) so `MemoryExchanges` survives restarts.

Stable **traits** from the guid hash, extending `PlayerbotOpportunityModel.Personality`: sociability, chattiness, generosity, curiosity, helpfulness. Every new behaviour multiplies its chance by the relevant trait so the population varies.

**Help and bystanders** (added 2026-10-23): a bot gains affinity toward anyone who helps it, such as joining the fight on a mob it tagged or healing it (once per fight; the helper bot gains some toward it too). It loses affinity toward a same-faction bystander who deliberately watched it die: in range, healthy, out of combat and able to matter for the whole low-health window, and did not help. The bot chooses whether that can happen: in a losing fight with such a bystander nearby it either **calls for help** (a yell plus a targeted help emote; more likely the more sociable it is and the more it likes them, never toward people it dislikes or the other faction) or **goes it alone**. Only someone it asked and who still ignored it is blamed; a bot that chose to go solo blames nobody. Bots that are asked may answer, weighted by their helpfulness and affinity (P1 builds the asking, the go-it-alone decision and the blame; answering a call for help is deferred to P2's help lane).

Uses: invite acceptance (affinity threshold instead of only guild/friends), conversation partner choice, help chance, recruitment, prompt facts ("you grouped with X twice; X tanks").

### 4.2 Proactive conversation (bots start and follow up)

`PlayerbotSocialInitiative` on the social pass picks at most a few openings per pass, each needing a real reason:

- **Shared situation**: same quest objective nearby → "You on the murloc heads too? Want to team up?" (leads into 4.3).
- **Trouble nearby**: a player at low health with adds → "Hang on!" + help (4.4).
- **Follow-up**: remembered topic with a familiar player ("Did you find that trainer?") on next meeting.
- **Questions** (4.5): a real knowledge gap.
- **Bot ↔ bot**: same openings between bots where a real player can hear, lifting the template-only restriction to allow the model provider when the player opted in.

Each opening is one line through `TryAnnounce` (so limiter, safety and opt-in apply); a reply re-enters the normal `Converse` path, which now carries the opening's context. Ignored or declined → affinity −, back off that player.

### 4.3 Cooperation: grouping beyond the same goal key

- **Recruitment**: a waiting goal (elite quest, "needs N" risk signal) may recruit nearby free bots that *lack* the goal: within level range, idle or low-priority, sociability/affinity-weighted. The coordinator shares the quest on formation (`ShareQuests` already exists); the recruit treats it as an accepted favour (affinity +).
- **Bots answer bots' LFG**: remove the "ignore bot lines" rule for LFG in `PlayerbotNeedsVoice.cs:819` when the coordinator can match; the chat line becomes the visible side of a real match ("inv me, I'll tank").
- **Guild grouping**: guildmates get a matching bonus; guild chat carries "anyone for Deadmines?" that actually forms a run.
- **Players**: `InvitePlayers` gains an affinity mode: invite a player who is on the same goal *and* has opted in or has positive affinity; accept stranger invites when affinity ≥ threshold or the player is on the same quest (new `InvitePolicy.Affinity`).
- **Harder content**: allow risk-signal groups > 5 to escalate into a raid goal for world elites/bosses when enough bots are nearby (today capped at 5, `PlayerbotRisk.cs:167`).

### 4.4 Helpful and reactive behaviour

- **Trouble scan** (cheap, uses existing visibility lists): same-faction player or bot under attack with health < 40% within 30 yd → help if healthy, healers heal, out of combat rez a dead stranger (class-gated), occasional buff. Weighted by helpfulness and affinity; stays within the think budget.
- **Comment on surroundings** (low rate, template): rare spawn seen, world event running, faction attack in zone, a player's achievement-like moment (first mount).
- Received help → thanks + affinity +; being griefed → affinity −, avoid that player.

### 4.5 Asking questions and learning from the answers

Bots on this server read the world database, so they don't *need* players to find things. Two kinds of learning, with different value:

**(a) Learning about people — always on.** Questions whose answer the server can't know: "What role do you like to play?", "Want me to ping you for Deadmines runs?", "Too much chatter?". Answers set relation flags (4.1) and change behaviour immediately (invite you as tank, stop whispering you). Parsed by the template intent classifier first; a model only when the player opted in.

**(b) Learning about the world — optional "curiosity" mode** (decision D1). A bot with a real gap asks: quest objective it has repeatedly failed to locate, an unreachable-quest set-aside, a price it has no data for, which dungeon fits its level. The answer is turned into a structured **claim** (where / who / price / recommendation) by: template patterns → optional model parse into a fixed JSON schema. Then:

1. **Verify** against ground truth where possible (does that NPC/creature exist in that zone; is the price within the auction history range). Unverifiable claims are held as "rumour" with the speaker's trust as weight.
2. **Act** on it (go there, post at that price).
3. **Score**: outcome confirms or refutes → speaker Trust ±, claim confidence ±. A player who keeps giving bad directions stops being believed (and the bot may say so, politely).
4. **Share**: confirmed claims spread to bots it talks to (rumour, confidence decays per hop) — visible as bot↔bot chat ("Heard the Defias are in the Deadmines' lower deck").

Store: `playerbot_knowledge` (Kind, Subject, Claim, Confidence, SourceGuid, Verified, ExpiresUnixMs), per bot, bounded, with expiry. This is fact memory, not training data; nothing feeds the goal policy.

### 4.6 Trading

- **Group gifts**: after a kill or a run, a bot hands a groupmate an item that is an upgrade for them and not for itself (`PlayerbotItemScore` exists): open trade, place item, accept. Generosity/affinity-weighted. Also "anyone need this?" after dungeons.
- **Bot ↔ bot**: reagent/material swaps between groupmates and guildmates, small gold loans between high-affinity bots (repaid later, tracked as owes-favour).
- **Completing WTS**: when a player agrees to the posted price, the bot opens the trade with the item and asks for the gold (validated: same price, within range, trade accepted by both).
- **Auction house**: extend the buy list beyond gear upgrades to reagents/materials the bot's class/professions use.

### 4.7 Better responses to players

- Persisted conversation summary + relation facts in the prompt and in template fills ("Hey again, Arra — still tanking?").
- More intents in the built-in classifier: recruitment, trade negotiation, directions, recommendations, "what do you know about X", answers to the bot's own questions.
- Free-form requests from non-masters handled when safe and validated on the world thread ("can you help me kill this?", "trade me linen?"), affinity-gated, never orders.
- Knowledge answers from 4.5 ("I heard from Kel the trainer moved… want me to show you?").

### 4.8 Across factions: emotes, rivalries and world PvP

Opposite factions can't understand each other's chat, so emotes are their language.

- **Neutral or friendly** (affinity ≥ 0): bots greet the other faction with emotes (`/wave`, `/bow`, `/salute`, `/cheer` after a shared fight against a world boss) and answer emotes in kind. They do not attack an unflagged player who is friendly to them.
- **Grudges**: a PvP kill, ganking or a rude emote lowers affinity; at or below the rival threshold the other side becomes a **Rival**. Bots answer rivals with rude emotes and avoid or confront them depending on strength.
- **Hunting parties**: a bot with a rival nearby that it can't beat alone raises a **rivalry goal** through the group coordinator (the same machinery as an elite quest): nearby same-faction bots with low affinity to that rival, or with high affinity to the wronged bot, join a party to hunt them.
- **Defence**: the target side does the same: a bot or player under attack by a hunting party (or a player who calls for help in /say, party or guild) gets a **defence goal**; its friends and nearby same-faction bots form a group to protect it.
- **Escalation**: each side may grow while the other side keeps growing: party → raid (up to 40) when both sides keep adding members and enough bots are nearby. It ends when one side wins, retreats or the fight goes quiet for a few minutes; the outcome feeds back into affinity (shared victory bonds the winners, and so on).
- **Safeguards (all options, on by default)**: only on PvP-enabled targets (PvP realm rules or a flagged player; never force-flags anyone), no fights inside sanctuaries or capital cities' guarded cores, a level-range check (no level 60 parties hunting level 20 players), a size cap relative to the defending side (hunters can't exceed defenders by more than a ratio until the defence also escalates), a cooldown per rival after a hunt, and corpse-camping prevention (a killed target is not attacked again for a while after it releases or resurrects). Real players can opt out of bot rivalries the same way they opt out of AI chat.

### 4.10 World events, world PvP and world bosses (requested 2026-10-23)

Today bots play battlegrounds (`Battlegrounds/`) and GM-run event scenarios (`EventScenarios/`), and only *talk* about world bosses and the war effort. The server already runs the content (`game_event`, Silithyst, the Eastern Plaguelands towers, the Ahn'Qiraj war effort, world bosses, holidays), so bots join it as players would, through real packets and the existing goal machinery. Each activity is one more goal source for the brain and the group coordinator, weighted by traits and affinity.

**Holidays and seasonal events** (`game_event` active → event goals):
- Event quests and repeatables at the right level: Lunar Festival elders, Love is in the Air, Noblegarden eggs, Children's Week, Midsummer bonfires (honour your own, and with low affinity toward the other faction, desecrate theirs), Harvest Festival, Hallow's End candy buckets and the Headless Horseman-style event bosses where present, Winter Veil gifts and Greatfather Winter, Darkmoon Faire turn-ins (gathered or crafted items), the Stranglethorn Fishing Extravaganza.
- Social flavour: holiday emotes and greetings, costumes/wands used on friends (and on rivals), dancing at the Midsummer fires, gifts to high-affinity friends.

**Ahn'Qiraj war effort** (while the war-effort event is active):
- Bots gather or buy the requested materials (herbs, bars, leather, bandages, food) through their existing gathering/professions and the AH, and turn them in at their faction's collection NPCs; per-resource progress steers what they gather (the most behind first).
- Commendation/reputation turn-ins, chatter about progress ("Copper bars are almost done!"), guilds pooling materials.
- The gong and the opening of the gates as a server-wide moment: bots travel to Silithus to see it and fight in the opening battle.

**World PvP objectives** (bots of the right level in the zone):
- Eastern Plaguelands towers: capture and defend towers in groups; hold them for the buff.
- Silithus: gather and turn in Silithyst, steal it from carriers of the other faction, escort friendly carriers.
- These are natural rivalry generators: captures, steals and kills feed affinity (P1) and hunting/defence groups (P6). In War Mode they become the front lines.
- Same safeguards as 4.8 (PvP rules, level ranges, opt-out, caps unless War Mode).

**World bosses** (Azuregos, Lord Kazzak, the Emerald Dragons and any others in the world database):
- A boss spawn (or a scout bot seeing one) raises a raid goal through the group coordinator: bots of the right level from both nearby zones and guilds are recruited (affinity and trust decide who gets asked and who answers), assigned roles, travel together and pull when enough are there.
- Real players can lead or join: bots answer "LFM Azuregos" in world/LFG chat and accept raid invites from players they like.
- The other faction may contest the boss: in normal mode a standoff with emotes or a fight depending on affinity; in War Mode a battle.
- Loot follows normal raid rules (no ninja looting, see 4.9); a kill is a shared victory that bonds everyone involved.

**Battlegrounds** (already supported): add the social layer: friends and guildmates queue together as premades, rivals are remembered from battleground fights too, and bots chat about results.

### 4.9 Additions approved 2026-10-23

| # | Addition | Phase |
|---|---|---|
| 1 | Forgiveness: affinity drifts back to neutral (rivals slower); apologies repair | P1 |
| 2 | Daily cap on affinity gains per pair (anti-farming) | P1 |
| 3 | Bad group behaviour costs affinity: ninja-looting, kill-stealing a tagged mob, abandoning a run | P1 |
| 4 | Reliability: trust from group success / dying first in wipes; remembered roles | P1 |
| 5 | Gratitude: thanks, buff, resurrect or tip the helper | P2 |
| 6 | Mood: short-term mood from recent events changes chattiness | P2 |
| 7 | Friends / ignore lists and guild invites driven by affinity (real packets) | P3 |
| 8 | Mentoring: high-level bots help low-level friends; low-level bots ask high-level bots | P3 / P5 |
| 9 | Word of mouth: bots tell friends and guild who helped or abandoned them (second-hand affinity at reduced weight) | P5 |
| 10 | Server-wide caps on concurrent hunts and raid-size fights | P6 |
| 11 | Metrics for every social event + a long population soak test that checks affinity settles instead of polarising | each phase |

**War Mode** (`World:Playerbots:Social:WarMode`, off by default; a server mode for massive world PvP): hate mechanics at maximum: negative affinity changes multiplied, never forgotten (no drift back for grudges and rivals), and the caps lifted (daily caps, hunt concurrency, hunter-to-defender ratio, per-rival cooldowns), so feuds can grow into server-wide raid wars. It still never force-flags anyone for PvP, players who opted out stay out, and the bots' think budget still bounds CPU. War Mode keeps its own **war layer** of affinity and rivalries alongside the normal one. Normal affinity keeps following the normal rules the whole time and War Mode never writes to it; decisions read the war layer only while War Mode is on. Turning it off therefore puts every bot back exactly where normal mode would have left it. The war layer stays dormant for the next war (or is wiped, with `ClearOnDisable`), and hunts that only qualified under War Mode wind down. P1 implements its memory side; P6 its rivalry side.

## 5. Phasing (each phase is shippable on its own, behind options that default to today's behaviour where it changes anything visible)

| Phase | Delivers | Main files |
|---|---|---|
| P1 Social memory | `playerbot_relation` + conversation summaries persisted; traits; events wired; prompt/template facts; `.playerbot social <bot>` GM inspect | new `Social/PlayerbotSocialMemory.cs`, Kernel store, Data module (schema 64) |
| P2 Initiative & help | proactive openings, follow-ups, trouble scan, rez/buff strangers, surroundings comments | `Social/PlayerbotSocialInitiative.cs`, `PlayerbotBrain` help lane |
| P3 Cooperation | recruitment of non-goal bots, bot LFG answering, guild grouping, affinity invite policy, raid escalation for world elites | `Groups/PlayerbotGroupCoordinator.cs`, `Party/PlayerbotGroupInvites.cs` |
| P4 Trading | group gifts, bot↔bot swaps/loans, WTS completion, AH reagents | new `Economy/PlayerbotTrades.cs` |
| P6 Rivalry & world PvP | cross-faction emotes, rival flag, hunting parties, defence groups, escalation to raids, safeguards | `Social/PlayerbotRivalry.cs`, `Groups/PlayerbotGroupCoordinator.cs` (rivalry/defence goals), `Risk/` |
| P7 World events | holidays and seasonal events; Ahn'Qiraj war effort; world PvP objectives (EPL towers, Silithyst); world-boss raids; social layer for battlegrounds | new `Events/PlayerbotWorldEventGoals.cs`, `Events/PlayerbotWarEffort.cs`, `Events/PlayerbotWorldPvpObjectives.cs`, `Groups/PlayerbotGroupCoordinator.cs` (world-boss raid goal) |
| P5 Questions & learning | people-questions (a); curiosity mode (b) with claims, verification, trust, rumour spread | `Social/PlayerbotQuestions.cs`, `playerbot_knowledge` (schema 61) |

Every phase: QA scenario tests in `tests/ArcaneCore.World.Tests/Playerbots/`, docs in `docs/areas/playbots-social.md`, options documented (DocKeyAudit), fault sites under `playerbot.social`.

## 6. Decisions (approved 2026-10-23)

Approved with every new behaviour **on by default** and the **model enabled**:

- **D1 Curiosity mode**: on. Bots ask players about world facts they lack (prices and "which dungeon for my level" first), verify the answers and keep or drop them by trust.
- **D2 Unprompted contact**: on. Whispers go first to players who opted in to AI chat or have positive affinity; everyone else gets public `/say` openings, at most 1 per player per 30 min.
- **D3 Knowledge sharing**: rumours spread between bots, with confidence decaying per hop.
- **D4 Model use**: model-backed openings, answers and claim parsing are enabled. The chat safety layer (opt-in, "ai off", strikes, disclosure, spend cap) still decides per player whether a model or a template speaks; templates remain the fallback.
- **D5 Order**: P1 → P3 → P2 → P4 → P5, each phase its own PR.
- **D7 World events** (added 2026-10-23): after the current work. Holidays and the war effort need no grouping and can follow P1; world-boss raids need P3; world PvP objectives build on P6. So: P7a holidays + war effort after P1, P7b world bosses after P3, P7c world PvP objectives after P6. **Status (2026-10-24):** P7a slice 1 is built on `claude/p7a-world-events`: the festival gathering (idle bots stand 8 to 12 yards from a festival NPC for the event's length), their arrival lines and emotes, and the "an event has started" remark gated on `World:Playerbots:Social:Events:Enabled`; see [World events: festivals](../areas/playbots-social.md). Not in slice 1: the war effort, and emotes aimed at a player.
- **D6 Rivalries** (added 2026-10-23): cross-faction emotes for neutral/friendly pairs; hunting parties against disliked targets and defence groups for the target, escalating up to raids. P1 already records PvP kills and emotes; P6 builds the behaviour after P3 (it reuses the group coordinator). Safeguards in 4.8 on by default.
