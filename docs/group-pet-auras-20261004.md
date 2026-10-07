# Group pet aura masks (2026-10-04)

Controlled current-pet visible aura slot writes and removals now notify the
social group bridge. Out-of-range stats carry finite positive and negative slot
masks, followed by u16 spell ids in build-5875 order; removed slots serialize
spell id zero. Full pet baselines derive active live slots, while partial
updates use only producer-marked changed slots. Pending name/vitals/aura bits
merge into one group packet. Pet aura application masks and pet-buff behavior
remain outside this slice.
