# Hunter pet names and rename state

Hunter pets now carry mutable runtime name, timestamp and rename permission in
their charm state. New hunter pets start with the creature template name,
timestamp zero and one rename permission. A successful build-5875
`CMSG_PET_RENAME` validates the raw pet name without applying player-name
capitalization, clears the permission,
stamps Unix seconds and queues the current snapshot. `CMSG_PET_NAME_QUERY`
continues to answer any map creature with matching charm pet number, using its
runtime name and timestamp.

The persistent snapshot and `character_pet` row retain name, timestamp and
rename permission. The naming data module owns the characters schema step after
the existing version 23 cooldown module. External profanity/reserved-name
catalogs are intentionally an injectable veto; this repository does not claim
to ship the vmangos reserved/profanity tables.

Build 5875 follows vmangos' empty-body `SMSG_PET_NAME_INVALID`; newer
wow_messages client versions document a reason-bearing form and are outside this
wire target. Game, SQLite and cold-host socket coverage exercise first rename,
second-rename refusal, and name/timestamp retention across a fresh world host.
