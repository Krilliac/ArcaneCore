# Codex game-logic security review

An independent read-only scan (Codex gpt-6-sol, 116 game-logic source paths, revision
`49448fd`) reported three medium findings, all confirmed against the code. This note records
what each was, how it was fixed, the configuration it adds and the proof.

| # | Finding | Status |
| --- | --- | --- |
| 1 | Any player can use mail from anywhere with a fabricated mailbox GUID | Fixed on `claude/vw3-sec-game-logic` |
| 2 | Alternating friend changes can grow the social write queue without a bound | Fixed on `claude/vw3-sec-game-logic` |
| 3 | One player can retain unlimited custom channels by joining unique names | Fixed by `e896eb0` on `claude/vw2-security-hardening` (not reimplemented here) |

## 1. Mailbox access

**Cause.** `DefaultMailboxAccess` accepted any GUID whose high part was `GameObject`, and every mail
opcode (get list, send, take money, take item, mark read, return, delete, create text item) trusted it.
A client could read, send and collect mail from anywhere with a made-up GUID.

**Retail.** vmangos `WorldSession::CheckMailBox` (MailHandler.cpp:65) calls
`Player::GetGameObjectIfCanInteractWith(guid, GAMEOBJECT_TYPE_MAILBOX)` (Player.cpp:2530) at the start of
all eight handlers; cMaNGOS Classic does the same (MailHandler.cpp:44, Player.cpp:2455). The object must
exist in the player's map, be of mailbox type, be spawned and be within interaction distance, and the
player must be in the world and alive.

**Fix.** `GameObjectMailboxAccess` (`World/Economy/EconomyAccess.cs`) asks the player's map's
`GameObjectMapSystem.FindInteractable(player, guid, GameObjectType.Mailbox)` (type, spawned, same map,
`InteractionDistance` 5 yd with bounding radii, alive). `EconomyFeature` uses it unless an `IMailboxAccess`
singleton is registered or the mode is Permissive. Not modelled, because the state does not exist yet:
the taxi-flight and lost-control refusals; the object system also refuses a NoInteract object.

**Configuration.** `Economy:MailboxAccess`: `Retail` (default) or `Permissive` (the old accept-any-GUID
check). Permissive is the only deviation, for synthetic test hosts and servers without game object content.
With no game object content, Retail refuses all mail because no mailbox exists.

**Proof.** `MailboxAccessTests` (World.Tests), end to end over a loopback session with real game objects:
a fabricated GUID, a chest and an out-of-range mailbox are refused for every mail opcode, an in-range
mailbox is answered, Permissive restores the old behaviour, and each opcode (including mark-as-read, which
sends no reply) is shown to consult the check with the addressed GUID. Red: with the default flipped to
Permissive, the eight Retail cases fail. `EconomyLifecycleTests` (a synthetic host with no mailbox object)
registers `PermissiveMailboxAccess` explicitly.

## 2. Social write queue

**Cause.** `SocialWriteQueue` was an unbounded channel with one entry per packet, so alternating friend
and ignore add/remove packets grew memory and database backlog. After three failed attempts a write was
dropped, and `FlushAsync` only meant "attempted".

**Fix.** (`World/Social/SocialWriteQueue.cs`; contract shared with `ReputationWriteQueue`.)

1. Coalescing. Every write sets one absolute value (a row's flags, a guild snapshot or deletion, a
   purge), so a newer value for a key with a write waiting replaces that write's value; the end state
   equals applying every write in order. A purge is a barrier: a write queued after it is never merged
   into one before it.
2. Bounds. New friend/ignore rows waiting for storage (queued or retained) are limited per character and
   in total. `ISocialPersistence.TrySetSocial` returns false beyond the limit and `FriendsService`
   disconnects the session; a row already waiting still takes changes.
3. Retention. A write that fails all attempts is retained and retried at the next write of that
   character, at login (`FlushCharacterAsync`, which throws while unrecovered; the list loaded meanwhile
   has the retained rows applied), at logout and at shutdown (`StopAsync`, which throws naming what is not
   durable). `FlushAsync` stays a pure barrier. Guild snapshots and purges are retained too; a purge discards
   retained rows of the deleted character so a retry cannot bring them back.

**Configuration.** `World:Social:WriteQueue:MaxPendingPerCharacter` (default 256, about three times the
50 + 25 list limits and far above what a player has waiting), `MaxPendingTotal` (25000), `RetryDelayMs`
(200). 0 or less disables a bound. Retail semantics of what is persisted and when are unchanged; a
refused request is the only visible difference and needs hundreds of distinct rows pending at once.

**Proof.** `SocialWriteQueueBoundsTests`: 10000 alternating changes leave one write per row, 150 random
operation sequences (rows, purges, guild saves and deletes, mid-sequence barriers, release points) end in
the same state as an in-order model, per-character and global limits, configuration binding.
`SocialWriteQueueRetentionTests`: a store failing three times then recovering persists the final state, a
newer value replaces a retained one, login barrier, logout retry, shutdown retry and throw, retained guild
snapshots, purge discarding. `FriendsServiceTests` checks the kick. Red: dropping instead of retaining
fails nine queue tests; disabling the merge fails the coalescing test. The old
`Flush_AfterAFailedWrite_StillCompletes` now also asserts that shutdown reports the retained purge.

## 3. Unlimited custom channels

Fixed by commit `e896eb0` ("Cap channels per player", `ChannelManager` plus `ChannelJoinCapTests`) on branch
`claude/vw2-security-hardening`. Verified present in the repository (`git log -1 e896eb0`; the branches
`claude/vw2-security-hardening` and `claude/vw3-inbound-queue-cap` contain it). It is not part of this change.
