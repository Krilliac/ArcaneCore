# GM stream: live debug channels in chat

A GameMaster can follow named debug channels and get the events as chat lines, prefixed with the channel name, for example
`[damage] Grunt(Creature:123) -> Thrall(Player:5) melee 45 Physical (absorb 0, resist 0, block 12) CRIT`.
The command syntax is in the generated [GM command reference](../reference/gm-commands.md) (`.arcane stream`); this page describes the
channels, scopes and limits.

## Commands

| Command | Effect |
|---|---|
| `.arcane stream on <channel> [target\|near\|all]` | Follow a channel. Running it again renews the timeout and sets the scope. |
| `.arcane stream off <channel>\|all` | Stop following one channel, or every channel. |
| `.arcane stream` or `.arcane stream list` | The channels you follow with scope and seconds left (or "You follow no stream channels."), then every available channel with its scopes. |

An unknown channel is refused with the list of channels that exist. `on` and `off` are recorded in the debug-write audit with the scope.

## Channels

| Channel | Scopes | Carries |
|---|---|---|
| `damage` | target, near, all | Damage and heals: source, target, spell, amount, school, absorb/resist/block, crit. Melee swings, spell damage, periodic ticks, heals and environmental damage. |
| `procs` | target, near, all | Proc chance rolls: aura, chance, roll, result. |
| `spells` | target, near, all | Cast start, fail with reason, go with hit and miss per target, spell misses and interrupts. |
| `moves` | target, near | Movement opcodes with position and speed. Not available for everyone: it would flood the chat. |
| `anticheat` | target, near, all | Anticheat findings with score and verdict (verdict None included), and the packet-heuristics reports. Lines carry the player GUID and the account id only, never a player or account name: `(Player:guid) account N: Type 'detail' weight W score S -> Action at map M (x, y, z)` for a finding, `packet CODE (Player:guid) account N: text [Severity]` for a packet report, and `packet CODE account N: text [Severity]` while the session has no player yet (the account id alone identifies it). A packet report has no position, so it reaches `all` scopes and a `target` scope whose GM has that player selected, never a `near` scope. |

The `damage`, `spells` and `moves` channels are fed from the packets the server sends to the players who can see a unit, so they show what a
client would be told, also for units nobody is watching. Spell lines show the spell id (a name where the spell code has it: cast failures and procs).

## Scopes

* `target` (the default): events whose actor or other party is you or the unit you have selected, and that happened within range of you
  (same map and instance). Events that involve you always match, wherever they happen.
* `near`: any event within range of you on the same map and instance. Events that involve you always match.
* `all`: every event of the channel, anywhere.

Range is the distance in the horizontal plane, measured from your position as read once per world tick, and defaults to 100 yards (the map
visibility distance). The position is a snapshot, so a new subscription's `target` and `near` scopes start matching from the next world tick. An event with no
position (for example a packet-heuristics finding) matches the `target` scope by GUID alone and never matches `near`. A line published as global (no
actor, no place) ignores the scope and reaches every GM that follows the channel.
For a multi-target spell go, the `target` scope matches on the caster and the first target only.

## Limits

* At most 5 lines per second per GM across all channels (a token bucket holding one second's worth). Lines over the limit are dropped and
  counted; one notice, "N lines suppressed (limit 5/s)", follows. Only a GM whose scope matches an event spends a token for it.
* A subscription turns itself off 10 minutes after it was last started or renewed, and when you log out; you are told when it times out.
* At most 8 channels per GM at a time.
* Zero cost when nobody follows a channel: the producers check a lock-free flag before decoding or formatting anything. The limits, the
  timeout and the range are code defaults of `GmStreamOptions`; no configuration key binds them.
