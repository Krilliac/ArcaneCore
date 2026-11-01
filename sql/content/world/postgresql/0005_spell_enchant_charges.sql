-- ArcaneCore content: charges of poison and other temporary enchants (spell_enchant_charges). Idempotent: deletes the shipped spell ids, then inserts them.
-- Rows from the vmangos migration sql/old_migrations/20180830022134_world.sql (still in effect in sql/migrations/20240304213652_world.sql); read as SpellMgr::LoadSpellEnchantCharges (SpellMgr.cpp:1889-1923): the spell id keys the table, 0 charges means duration only.
-- Needs the world schema with the spell_enchant_charges table (world schema 35, sql/content/README.md). Other spell ids are not touched.
-- provider: postgresql

BEGIN;

DELETE FROM "spell_enchant_charges" WHERE "entry" IN (8679, 8686, 8688, 11338, 11339, 11340, 5761, 8693, 11399, 2823, 2824, 11355, 11356, 25351, 13219, 13225, 13226, 13227, 14792);

INSERT INTO "spell_enchant_charges" ("entry", "charges") VALUES
(8679, 40),
(8686, 55),
(8688, 70),
(11338, 85),
(11339, 100),
(11340, 115),
(5761, 50),
(8693, 75),
(11399, 100),
(2823, 60),
(2824, 75),
(11355, 90),
(11356, 105),
(25351, 120),
(13219, 60),
(13225, 75),
(13226, 90),
(13227, 105),
(14792, 15);

COMMIT;
