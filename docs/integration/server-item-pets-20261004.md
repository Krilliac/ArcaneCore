# Item use and current-pet persistence — 2026-10-04

This local continuation extends the recovered source at
`7313b9eb089197b253dc51bd8bb5ceb803adcc6b`. It remains uncommitted; CI,
external database providers and actual build-5875 client acceptance are pending.

## Implemented scope

`CMSG_USE_ITEM` now reaches the normal spell pipeline with live cast-item
identity. The three-byte 5875 prefix and target block are parsed strictly.
Short packets and later-build trailers do not mutate inventory or cast state.
Use/equipment, shapeshift, combat, settlement, transit, logout, charge and trade
checks guard item use; delayed casts revalidate the item. The self-mover
producer remains absent.

On-use spells run in template order, first normal and later triggered. Instant
casts settle item charges after sibling dispatch; delayed casts settle on
completion. Positive charges, negative expendables and same-entry reagent
costs are supported. Matching reagent settlement clears cast-item provenance
before later charge consumption. Item casts skip spell power costs.

Item cooldown durations and effective categories govern server readiness and
survive existing cooldown-row persistence. A category that outlives the spell
cooldown remains a server-side gate after restoration. Client cooldown owner
metadata (`ItemId` and effective category reconstruction in initial packets)
still needs its own persistence slice; this is not complete cooldown UI parity.

Current hunter-owned controlled pets now have a characters schema version 21
store for stable number, entry, level/experience, vitals, happiness, react
state, action bar and spell/autocast state. Login preloads detached state before
world publication. Writes are serialized, relog waits for pending saves,
shutdown drains them, and character deletion invalidates late reads. Death
captures zero health before corpse removal. Loaded pets use fresh world GUIDs
while retaining the stable charm pet number.

Effect 109 revives a retained current corpse or the cached dead current pet.
Cold login leaves dead pets detached until revival. Saved passives are
reapplied; pet cooldowns, saveable auras, taming/stable subtype, loyalty and
training remain outside this slice. See the [pet scope](persistent-pets-20261004.md).

The zero-allocation tick test now measures three windows after runtime warmup;
the assertion remains exactly zero. The [allocation record](tick-allocation-20261004.md)
distinguishes the reproduced first-window signal from the earlier suite failure.

## Verification

The coordinator's Release build has zero warnings/errors. Focused checks pass:
94 Game cases (including the five unrelated regressions caught and repaired by
the first full run), two World cases, and three actual SQLite persistence/cold
host cases. The dead cold-host case dispatches registered effect 109 and checks
50 percent health and the pet GUID in the socket packet.

The final six-project suite passes 14,338 tests with six existing fixture skips
and zero failures. Native mock self-test passes all 59 checks. The portable
verification manifest retains the exact project counters, patch digest and
acceptance boundaries. These results do not qualify external providers or an
actual client.

## References and next work

Behavior is checked against pinned vmangos and mangos-classic source, with
wow_messages supplying the 5875 request layout. WoWWiki 1.12.1 and Wowhead
Classic were added to the charter/roadmap reference policy at the user's request.

The next bounded slices are listed in [next slices](next-slices-20261004.md):
item cooldown packet metadata, pet cooldown persistence, and consumable
current-spell/actual-target fidelity. The current full-resource guard covers
the tested caster-resource flows; it does not establish every target/content case.
