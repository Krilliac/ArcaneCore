# Group loot and group experience

Lane `group-loot-xp` (branch `claude/vw2-group-loot-xp`). Standing directive: vanilla 1.12.1 behaviour
as read from the references (vmangos primary, `D:\refs`), deviations only behind a config option.

## Delivered: looter-selection-fix

The group's round-robin looter pointer now follows vmangos instead of a one-shot "next member after
the last looter" pick, and master loot no longer corrupts the master looter.

| Behaviour | Where | Reference |
|---|---|---|
| Pure pointer rule: keep the current looter if still in reach (`ifNeeded`), else next eligible member after him in group order, wrapping, current looter last, nobody eligible clears the pointer; a looter who is not a member searches from the start | `Groups/GroupLooterSelection.Next` | `vmangos Group/Group.cpp:2474-2543` (UpdateLooterGuid) |
| Master loot and free-for-all never move the pointer | `GroupLooterSelection.Next` | `Group.cpp:2476-2481` |
| Two calls per kill: first with `ifNeeded` (who loots THIS kill, the leader on the first kill because `Group::Create` sets the looter to the leader), then without (advance for the next kill) | `LootService.PlanLooter` | `Objects/Unit.cpp:1037` and `:1078`, `Group.cpp:134`, `GroupManager.cs:135` |
| Looter changed: SMSG_GROUP_LIST is resent (`ILootGroups.LooterChanged`, wired to `GroupManager.SendUpdate`) | `LootService.CommitLooter`, `GameObjectLootFeature.SocialGroups` | `Group.cpp:2530-2542` |
| Offline master looter: first online leader/assistant other than the master takes over, otherwise the group switches to group loot with threshold uncommon (`ILootGroups.IsMemberOnline`, default true) | `GroupLooterSelection.MasterLooterFallback`, `LootService.EnsureMasterLooterAvailable` | `Unit.cpp:1041-1063` |
| Chests (plain and durable) use the same plan; a durable chest only moves the pointer when its generation committed (as before) | `LootService.OpenGameObject`, `OpenDurableGameObject`/`FinishGeneration` | unchanged commit rule |

Behaviour changes that existing tests pinned (updated in the same commit):
the first group kill goes to the leader (it used to go to the member after him), and the pointer
afterwards advances; `GameObjectTestKit.FakeGroups.Create` now sets `LooterGuid` to the first member
exactly like `GroupManager`.

Tests: `tests/ArcaneCore.Game.Tests/Social/GroupLooterSelectionTests.cs` (pure rule, hand-simulated),
`GameObjects/LootServiceTests.cs` (leader first then rotation, out-of-reach loses the turn, master
looter untouched, offline master fallback and group-loot downgrade), `GameObjects/DurableChestTests.cs`,
`World.Tests/GameObjects/InstanceChestDurabilityTests.cs` (owner roles swapped to the leader-first order).

## Limits (recorded, not delivered)

* Master loot has no master-give yet (no SMSG_LOOT_MASTER_LIST, CMSG_LOOT_MASTER_GIVE, master slot
  type, steal protection). Until it exists the shared loot of a master-loot kill is held by the master
  looter when he is within reward distance (otherwise it is open to every recipient). Retail shows
  every opener the under-threshold items and the master the rest (`LootMgr.cpp:829-981`).
* Loot threshold, roll (need/greed/group loot) packets and state, quest-item sharing, chest
  `groupLootRules`, money split rules, loot errors, open range and the 74 yd 3D reward distance are
  unchanged and still differ from retail (see `docs/integration/gameobjects-loot.md`). The reviewed
  design of this lane lists them as further slices.
* Loot recipient is still the killer's group, not a tap list (stats-combat-formulas lane owns
  `ITapInfo`), so pets/totems credit nobody.
* The "in reach" test for the pointer is membership of the bag's recipients (same map, 3D <= 74 yd);
  vmangos uses `IsWithinLootXPDist` (2D, strict, bounding radii, raid maps unlimited, world boss
  +150 yd). Switching to that helper is the `group-reward-range` slice.
* Unlike vmangos, SMSG_GROUP_LIST is only resent when the pointer value actually changes (vmangos also
  resends when it is already cleared).
* Group state stays memory-only (docs/areas/social.md).

Schema: none.
