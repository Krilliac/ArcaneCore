# Atlas public listener

The public listener is a second web server inside Atlas (`arcane-atlas serve --public`, or `arcane-atlas public` as its own process). It is separate from the loopback Local listener: different port, different sessions, different command policy. It serves one page, `/public.html`, and a small HTTP and WebSocket API under `/api/public/`.

Two tiers:

- **Guest**: anyone who can reach the address. A live top-down chart of the continents the operator listed, projected so that it carries no GUIDs, no targets, no home positions and (by default) no players.
- **Account**: someone who signed in with a game account. Same chart, plus their own characters. A Moderator or higher sees players by name (see "Staff view").

Nothing from the client data (no map tiles, textures or DBC strings) is served; the page draws plain shapes and the names the operator wrote.

## Configuration

Settings live in the `Atlas:Public` section of `atlas.json`, then `ARCANECORE_ATLAS_PUBLIC__<Key>[__<SubKey>]` environment variables, then the serve flags (`--public-bind`, `--public-config`). Each layer replaces the last. The listener validates everything at start and refuses to run on an error.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Whether the public listener starts. |
| `Bind` | `[::]:8443` | Address and fixed port. The port must differ from the Local listener's. |
| `Origin` | none | The public address, `https://host[:port]`. Required with `BehindProxy` or when `Bind` is not a single literal address. |
| `AllowedHosts` | none | Extra accepted `Host` values (no wildcards). A request with another Host gets 421. |
| `MinActionSecurity` | `Moderator` | The lowest account security that may use account actions (when an action runner is wired, see below). |
| `Login:PerIpPerMinute` | 5 | Login attempts per address per minute. |
| `Login:PerAccountPer15Min` | 5 | Failed logins per account per 15 minutes. |
| `Login:AllowBelowMinActionSecurity` | `false` | Whether accounts below `MinActionSecurity` may sign in at all. |
| `Tls:CertificatePath` | none | PFX file. |
| `Tls:CertificatePasswordEnv` | none | Name of the environment variable holding the PFX password (the password is never in the file). |
| `Tls:StoreThumbprint` | none | Use a certificate from the user's store instead of a file. |
| `BehindProxy` | `false` | A reverse proxy terminates TLS; the listener is plain HTTP on a literal loopback address. |
| `TrustedProxies` | none | Required with `BehindProxy`: the proxy's address, so client addresses come from its forwarded header. |
| `AllowInsecureDev` | `false` | Plain HTTP on a loopback or private address, for development only. Cookie is `atlas-dev` instead of `__Host-atlas`. |
| `MaxConnections` / `MaxConnectionsPerIp` | 200 / 4 | Concurrent sockets. |
| `Session:IdleMinutes` / `Session:MaxHours` | 30 / 12 | Account session lifetimes. |
| `AccountActions` | `true` | Whether account actions are offered. No action runner is wired in this build (see "Account actions"). |
| `Account:AllowInstances` | `false` | Whether account sessions may view instance maps. |
| `Guest:MaxSessions` / `MaxSessionsPerIp` | 5000 / 20 | Guest session caps. |
| `Guest:Maps` | Eastern Kingdoms (0), Kalimdor (1) | Maps guests may view: `{ "Id": 0, "Name": "..." }`. Continents only (0 or 1). The names are yours, never client strings. |
| `Guest:FeedHz` | 2 | Frames per second, 0.2 to 10. |
| `Guest:MaxBytesPerSecond` | 65536 | Per-socket frame byte budget, at least 4096. |
| `Guest:ShowPlayers` | `false` | Show real players to guests, as coarse delayed cells. |
| `Guest:ShowPlayerNames` | `false` | Only meaningful with `ShowPlayers`; see the policy table. |
| `Guest:PlayerCellYards` | 250 | Cell size. |
| `Guest:PlayerDelaySeconds` | 120 | How old the player cells are. |
| `Guest:PlayerMinCellCount` | 3 | A cell with fewer players is not shown (under 3 can single a player out; a warning is logged). |
| `Guest:WorldCallsPerSecond` | 50 | Budget of calls from guests into the world. |
| `Feed:Pid` | 0 | The world's process id; 0 finds the one feed on this machine. |
| `Feed:WebMaxConnections` | 2 | The world's `World:Control:MapFeed:Web:MaxConnections` (separate-process mode), 1 to 8. |
| `Feed:WorldMaxConnections` | 2 | The world's `World:Control:MapFeed:MaxConnections` (in-process mode). |
| `Feed:WorldActionsEnabled` | `true` | What you state about the world's action channel. In-process mode needs `false`. |
| `Feed:LiveServer` | `false` | What you state about the world being the live server. In-process mode is refused when `true`. |

## TLS

Three modes:

- **Certificate**: set `Tls:CertificatePath` (and `CertificatePasswordEnv`) or `Tls:StoreThumbprint`. Kestrel serves TLS 1.2 and 1.3 only.
- **Proxy**: `BehindProxy=true`, `Bind` a literal `127.0.0.1` or `[::1]` port, `TrustedProxies` set to the proxy, `Origin` set to the public https address. The proxy must forward `Host` and `X-Forwarded-For`, and must pass WebSocket upgrades on `/api/public/ws`.
- **Insecure dev**: `AllowInsecureDev=true` on a loopback or private address. Never expose it.

Reverse-proxy recipe (Caddy):

```
atlas.example.org {
    reverse_proxy 127.0.0.1:8443
}
```

with `BehindProxy=true`, `Bind=127.0.0.1:8443`, `TrustedProxies=["127.0.0.1"]`, `Origin=https://atlas.example.org`. nginx needs `proxy_set_header Host $host;`, `proxy_set_header X-Forwarded-For $remote_addr;` and the usual `Upgrade`/`Connection` headers on the `/api/public/ws` location.

HTTP/2 note: WebSockets over HTTP/2 need extended CONNECT. Whether the Kestrel version in use accepts it is not verified here; if a proxy speaks HTTP/2 to the listener and the socket fails, make the proxy use HTTP/1.1 upstream (the default for both recipes above).

## Requests and browser rules

- Session cookie `__Host-atlas` (`atlas-dev` in insecure-dev), `HttpOnly`, `SameSite=Strict`, `Secure`.
- Every state-changing request carries the header `X-Atlas-CSRF` with the token from `/api/public/me`. The WebSocket sends it in its first frame (`{"op":"auth","csrf":...}`). A login rotates the token and closes any open guest socket (1008); the page reconnects after refreshing its session.
- Requests with a foreign `Origin` or an unlisted `Host` are refused (421 for Host).
- The page is served with a strict Content-Security-Policy (`script-src 'self'`, no inline code, nothing from other origins).

## Guest data policy

| Data | Guest | Account | Moderator and above |
| --- | --- | --- | --- |
| Creatures, objects | opaque id, kind, entry, name, position, health percent, motion | same | same |
| Entity ids | HMAC-SHA256 of the world GUID under a per-session key (not stable across sessions, not reversible) | same | same |
| Target, home position | never | never | never |
| Players, `ShowPlayers=false` | not in the frame; counted into coarse cells (`PlayerCellYards` squares, delayed by `PlayerDelaySeconds`, cells under `PlayerMinCellCount` dropped); anything linked to a player (pets, followers, owned objects) is removed too | same | by name (staff view) |
| Players, `ShowPlayers=true` | individual live positions, labelled "Player" | same | by name (staff view) |
| `ShowPlayerNames=true` | names of players, only together with `ShowPlayers=true` | same | by name |
| Entities that are not real world spawns (links-based removal) | removed | removed | removed |
| Instances | never | only with `Account:AllowInstances` | same |
| Commands | `live.find`, `pick.public`, `guest.*`, bookmarks, layers | plus `me.characters` | same |

Anything not on the list of allowed commands answers one generic error, `not-available`, for every cause (unknown name, wrong tier, disabled), so the answer does not map the command set.

`pick.public` returns only what the page shows: name, type, level, faction, health percent, motion. The "Player" label for characters is a deliberate deviation from "Race Class": a projected frame carries neither race nor class for characters, and adding them would be a data leak, so the page labels every character "Player".

## Staff view

An Account session whose security is Moderator or higher gets the staff projection: the players in the frame are shown individually with their names. Ids stay opaque and `tg` and `home` are still stripped. This is deliberately narrower than "the raw frame": a staff browser tab is still a browser tab on the Internet, so it never receives a GUID or a hidden-state field. Staff who need raw data use the Local listener.

## Login

`POST /api/public/login` with `{ user, password, code? }`. The world checks the SRP6 verifier (the same check as the remote-admin console), the account's ban state, and the TOTP code when the account has one.

- One answer for every failure: `login-failed`, whatever the cause (unknown account, wrong password, banned, wrong code). Log text of the user-supplied name is escaped.
- Attempts are limited per address (`Login:PerIpPerMinute`) and per account (`Login:PerAccountPer15Min`). The in-memory bookkeeping is not persisted.
- A TOTP step is consumed only by a successful login. A wrong password with a right code does not burn the step, and a step accepted once cannot be replayed (the claim is atomic).
- A password is never logged, stored or put in a URL; the form empties the password field as it sends it.
- A successful login ties a ticket to the session; the ticket is the only thing the action channel resolves an actor from.

## Process modes

- **Separate process** (`arcane-atlas public`): the listener connects to the world's web pipe (`World:Control:MapFeed:Web:Enabled`, `ClientAccount` = the one Windows account that runs the listener). This is the mode to use on a live server. Operator commands are read from standard input: `public.status`, `public.sessions`, `public.kick`, `public.revoke`.
- **In process** (`arcane-atlas serve --public`): the public listener runs inside the Atlas process but opens its own feed connections (role `web`, never the Local connection) from the world's shared pipe budget, one connection reserved for Local. Allowed only against a world whose action channel is off and which is not the live server. Beyond the two operator statements (`Feed:WorldActionsEnabled=false`, `Feed:LiveServer=false`), the pool asks the world for its `caps` after the role ack on every connection, at start and on every reconnect, and refuses the connection (a `Fatal` event) unless the world itself reports `actions:false` and `liveServer:false`. A missing or malformed answer is a refusal.

The feed pool opens a world feed only while someone watches that map and closes it again when the last viewer leaves; a map with no free feed shows "busy" to its viewers.

## Account actions

`AccountActions` and `MinActionSecurity` describe the policy, but no `IPublicActionRunner` is wired into this build: the listener serves the read-only chart and the account's character list, and runs no action as the account. The startup line says so (`account actions=unwired`) rather than claiming they are on.

## Running it

```
arcane-atlas serve --public --public-config atlas.json
arcane-atlas public --config atlas.json
```

Then open the address in `Origin` (or the `Bind` address) (`/` serves `public.html`). The Atlas web app's own tests (`tests/ArcaneCore.Atlas.Tests`) cover the listener with a real Kestrel host and synthetic worlds; the page has Node tests with a fake DOM (`Web/public.test.mjs`).
