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
| `Guest:MaxBytesPerSecond` | 65536 | Byte budget per client address (IPv6 by /64), at least 4096, shared by every socket and every HTTP answer of that address; see "Byte budget". |
| `Guest:ShowPlayers` | `false` | `true` shows real players to guests as live individual positions. `false` (the default) shows none: guests get only coarse, delayed cells with a minimum count. |
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

Send HSTS from the proxy as well (`Strict-Transport-Security: max-age=31536000`): the listener sends it in TLS and proxy modes, but the proxy's own error pages and redirects need it too, and the browser only honours it over HTTPS.

Reverse-proxy recipe (Caddy, which adds HSTS with `header Strict-Transport-Security "max-age=31536000"`):

```
atlas.example.org {
    reverse_proxy 127.0.0.1:8443
}
```

with `BehindProxy=true`, `Bind=127.0.0.1:8443`, `TrustedProxies=["127.0.0.1"]`, `Origin=https://atlas.example.org`. nginx needs `proxy_set_header Host $host;`, `proxy_set_header X-Forwarded-For $remote_addr;` and the usual `Upgrade`/`Connection` headers on the `/api/public/ws` location.

IIS with Application Request Routing: install the URL Rewrite and ARR modules and enable the proxy in ARR ("Enable proxy"); enable the WebSocket Protocol Windows feature; add a rewrite rule that forwards `{R:0}` to `http://127.0.0.1:8443/{R:0}`; keep "Preserve host header" (the `Host` must stay the public name) and set the server variable `HTTP_X_FORWARDED_FOR` to `{REMOTE_ADDR}` (allow it under "View Server Variables" first); add the custom response header `Strict-Transport-Security: max-age=31536000`. Use the site's HTTPS binding for TLS.

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
| Players, `ShowPlayers=true` | individual live positions, labelled "Race Class" (for example "Human Warrior"), or "Player" when the race or class is unknown | same | by name (staff view) |
| `ShowPlayerNames=true` | names of players, only together with `ShowPlayers=true` | same | by name |
| Entities that are not real world spawns (links-based removal) | removed | removed | removed |
| Instances | never | only with `Account:AllowInstances` | same |
| Commands | `live.find`, `pick.public`, `guest.*`, bookmarks, layers | plus `me.characters` | same |

Anything not on the list of allowed commands answers one generic error, `not-available`, for every cause (unknown name, wrong tier, disabled), so the answer does not map the command set.

`pick.public` returns only what the page shows: name, type, level, faction, health percent, motion. A real player shown without a name (`ShowPlayerNames=false`) is labelled "Race Class", for example "Human Warrior" or "Night Elf Druid". Race and class come from the map feed: the world writes the player's race id (`r`) and class id (`cl`) on player and bot entities (`MapFeedCapture`), `FrameParser` reads them into `LiveEntity.Race` and `LiveEntity.Class`, and `GuestFrameProjector.Label` maps the two ids to names. The label is built from those two ids only, so it never carries the character name, level or guild. When either id is missing (a world older than this change) or is not a playable race or class, the label falls back to "Player". The separate 2D chart is unchanged.

When the world's `links` answer has never been available (an older daemon), the projector also has no list of pet and totem templates. It then drops every creature (and every bot and game object) and the status reads "reduced". A partial rule such as "creatures near a hidden player" would leave a hole in the creature field that moves with the player, which is worse than showing no creatures. Once a `links` answer has been seen, the template set it carried is remembered, so a later outage drops only creatures of those templates, followers, bots and player-ownable objects. The pool asks for `links` again after each answer (one ask in flight per connection, on the next frame). Only a correlated `unknown-op` (the world now refuses an unhandled extension op with the request's id) stops the asking for the rest of the connection; each refused line is a strike and five in a row close the connection. An `ok:false` answer (the world's own query gave up) is asked again after 5 seconds. A request that gets no answer within 12 s (longer than the world's own 10 s query timeout) is not final either, since the world may only be slow or may be an older daemon that refuses without an id: it is asked again after 1 minute, then 2, doubling up to 10, and the first timeout is logged as a warning; a successful answer resets the back-off. An older daemon's refusal carries no id, so it is a strike each time; the fifth strike, after about 16 minutes, can still close that connection, and the pool then reconnects. A pet species that first appears during an outage is not in that set; a world-side template list in `caps` would close that gap and is not built yet.

## Byte budget

`Guest:MaxBytesPerSecond` is one bucket per client address, shared by every socket and HTTP answer of that address. Messages come in three classes:

- **Essential**: the session routes (`status`, `me`, `login`, `logout`), every HTTP error body, and on a socket the hello, status notes, pongs, error replies and command refusals. They are charged but never refused for bytes, because login and logout have already changed the session by the time the answer is written, and `status` and `me` start the page. The bucket may go up to one second into debt. Each such message is at most 2 KiB and sits behind its own rate limit (20 HTTP requests per second, 10 socket messages per second, at most 2 socket error replies per second, one hello per socket), which bounds the total. The size limit is enforced, not assumed: a reply never repeats client text (a refusal names the command only when it is one the listener lists, otherwise `?`), the hello leaves out maps that would not fit, and a message that still exceeds 2 KiB is charged in full, logged as an error, counted and replaced (HTTP) or not sent (socket). The `429 rate-limited` answer to a request flood is the one error body that is not charged, so a flood cannot keep a shared address in debt.
- **Interactive**: command results over HTTP and over the socket. A result that does not fit is replaced by a small refusal (`429 rate-limited` with `Retry-After` over HTTP, a `rate-limited` result on the socket). While the address is in debt a command is not run at all.
- **Droppable**: live frames. A frame never takes the bucket below a reserve (a quarter of it, at least 1 KiB), so a command answer still fits between frames; a frame that does not fit is dropped, never queued, and the guest sees "Updates are slowed to stay within the data limit".

A socket that keeps sending messages faster than 10 per second is answered with at most 2 error replies per second; once 100 refused messages have piled up it is closed with 1008 "flood". The page waits out one 429 on `status`/`me` (at most 3 s) and retries once.

The page itself (`public.html`, scripts and styles) is not charged; the HTTP request rate limit covers it.

## Staff view

An Account session whose security is Moderator or higher gets the staff projection: the players in the frame are shown individually with their names. Ids stay opaque and `tg` and `home` are still stripped. This is deliberately narrower than "the raw frame": a staff browser tab is still a browser tab on the Internet, so it never receives a GUID or a hidden-state field. Staff who need raw data use the Local listener.

## Login

`POST /api/public/login` with `{ user, password, code? }`. The world checks the SRP6 verifier (the same check as the remote-admin console), the account's ban state, and the TOTP code when the account has one.

- One answer for every failure: `login-failed`, whatever the cause (unknown account, wrong password, banned, wrong code). Log text of the user-supplied name is escaped.
- Attempts are limited per address (`Login:PerIpPerMinute`) and per account name (`Login:PerAccountPer15Min`). Every attempt is charged, not only failed ones, and the bookkeeping is in memory and not persisted.
- The per-name bucket is global: it counts attempts from every address. The cost is that a stranger who knows a staff account's name can use up its budget and make the real owner wait for a refill (the answer is the same `login-failed` with `Retry-After`). The benefit is a hard ceiling on guessing for that name from a spread of addresses. The world adds its own lockout keyed by account and network, which does not have this property.
- Give every staff account a second factor (a TOTP authenticator; a fixed PIN is weaker): only staff accounts can sign in by default, and a password alone is all that stands between the Internet and a GM account.
- An account whose `LockFlags` include the realm's IP lock cannot sign in on the web: the lock binds the game client's address, which a browser session cannot satisfy. The answer is the ordinary `login-failed`.
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
