-- ArcaneCore content: the spells a freshly summoned pet starts with, for seven summoned creature entries (cmangos classic-db values)
-- (petcreateinfo_spell, entry = creature entry). Idempotent: deletes the rows of its own entries, then inserts them.
-- Needs the world schema with the pet tables (world schema version 16, sql/content/README.md). The larger pet_levelstats table is not
-- shipped here; import it from a classic-db dump with arcane-content-importer (docs/areas/content-import.md).
-- provider: mariadb

START TRANSACTION;

DELETE FROM `petcreateinfo_spell` WHERE `Entry` IN (416, 417, 510, 1860, 1863, 5807, 15429);

INSERT INTO `petcreateinfo_spell` (`Entry`, `Spell1`, `Spell2`, `Spell3`, `Spell4`) VALUES
(416, 3110, 0, 0, 0),
(417, 19505, 0, 0, 0),
(510, 6873, 9672, 0, 0),
(1860, 3716, 0, 0, 0),
(1863, 7814, 0, 0, 0),
(5807, 17254, 0, 0, 0),
(15429, 25163, 0, 0, 0);

COMMIT;
