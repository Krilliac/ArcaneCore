# Verified continuation: health percentages, pet options and group names

Release builds with zero warnings/errors. Qualification is a six-project
composite: five passing projects from the original run plus an unchanged full
Game recheck under the default runtime. Total14,466 passed / six existing skips /
zero failures in the composite; native mock self-test59 checks. Focused features
pass62 cases and the unchanged allocation check passes once independently.

The original Game run failed TickStatsTests.Record_DoesNotAllocate in its third
window with7,040 bytes. The same test and production source were unchanged;
the focused case and complete4,013-test Game recheck pass. Cause remains
unproven. The zero threshold was not relaxed. Failed and passing evidence are
retained separately; this is not an uninterrupted-green-suite claim.

Aura88 multiplies out-of-combat spirit regeneration only. Aura116 combat
scaling, Aura161's flat contribution, food, Rate.Health, carry and caps retain
their source ordering. Group pet-name updates merge with other stats flags into
one packet, use the current CString/GUID, and go only to group mates who cannot
see the owner. Real invite/accept/rename sockets and combined-mask tests pass.

Pets:Names binds minimum length, strict scripts and realm zone per world. The
configured-host test proves real feature binding, isolated default-host behavior
and preservation of injected vetoes. Raw name casing remains intact. Original
profanity/reserved catalogs and broader pet group model/vital fields stay pending.

Characters schema remains24. Source remains local, unstaged and uncommitted on
codex/server-continue-20261004 at base7313b9eb089197b253dc51bd8bb5ceb803adcc6b.
Original-client acceptance, proprietary fixtures and external-provider gates
remain pending. WoWWiki1.12.1 and Wowhead Classic remain version-checked sources.
No commit, push, merge, deployment or live-service change occurred.
