# Autonomous protocol playtest clients

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
