# World-thread audit, lane 8: group stats snapshot (item 14.1)

Status: finding confirmed on current code and fixed in place.

## Finding

`GroupManager.UpdateOutOfRangeStats` (the group feature tick, about once a second) called
`GroupMemberStatsSnapshot.Capture` for every grouped online player. Each call allocated a snapshot plus two
`uint[48]` aura arrays (own and pet). `UpdateOfflineLeaders`, run at the end of the same pass, also allocated
`Distinct().ToArray()` whenever the offline-leader delay was enabled.

## Fix

- `GroupMemberStatsSnapshot` keeps its two aura arrays for life; `CaptureFrom(Player)` overwrites every field
  and both arrays in place. `Capture` stays as the allocating one-shot used by `RequestMemberStats` and the
  packet helpers.
- `GroupManager` keeps a second buffer per grouped player (`_spareStats`). A pass refills the spare, diffs it
  against the sent snapshot (`_sentStats`), then swaps the two. Both dictionaries are cleaned at every site that
  dropped `_sentStats`.
- `UpdateOfflineLeaders` reuses a scratch list and set instead of `Distinct().ToArray()`.

## Threading and aliasing

- World thread only, as before (the group feature tick). No new cross-thread state.
- `SendStatsOutOfRange` builds its packet bytes from the new and previous snapshot (`GroupPackets.BuildPartyMemberStats`
  writes values into a fresh `byte[]`) and stores no reference, so the swap cannot change data still in use.
- `_pendingAuras`, `_pendingPetAuras` and `_pendingPetName` are untouched.
- Skipping the capture when no mate is out of range was not done: it would change which pass reports a change
  (the sent snapshot would go stale), and the saving is only the field reads.

## Tests (tests/ArcaneCore.Game.Tests/Social/GroupStatsSnapshotReuseTests.cs)

- An HP change sends one `SMSG_PARTY_MEMBER_STATS` with only the CurrentHp bit; an unchanged pass sends none,
  across several buffer swaps.
- A cleared aura slot is still reported after the buffers have swapped.
- `AllocationProbe`: a steady pass over 40 grouped players allocates 0 bytes. This one fails before the change
  (new snapshot and arrays per player per pass).

The lane ran without a build or test run (integration-branch policy); the tests run in the integration gate.

## Conflicts

`GroupManager.cs` is also in the file list of the `s3a-groups-via-holder` lane (holder-side group authority).
The hunks here are confined to the stats fields, `UpdateOutOfRangeStats`, `UpdateOfflineLeaders` and the
`_sentStats` removal sites.
