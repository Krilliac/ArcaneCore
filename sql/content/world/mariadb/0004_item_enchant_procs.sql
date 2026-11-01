-- ArcaneCore content: PPM overrides of weapon-enchant proc spells (spell_proc_item_enchant). Idempotent: deletes the shipped spell ids, then inserts them.
-- Values as mangos-classic ships them (sql/archive/0.12.2 z1108_s0481_01 and z1539_s1035_01; also in the vmangos dump): Frostbrand 9 PPM, Fiery Weapon 6, Lifestealing 6, Icy Chill 1.6.
-- Needs the world schema with the spell_proc_item_enchant table (world schema 35, sql/content/README.md). Other spell ids are not touched; an enchant without a row procs at weapon speed / 600 percent (about 1 PPM).
-- provider: mariadb

START TRANSACTION;

DELETE FROM `spell_proc_item_enchant` WHERE `entry` IN (8034, 13897, 20004, 20005);

INSERT INTO `spell_proc_item_enchant` (`entry`, `ppmRate`) VALUES
(8034, 9.0),
(13897, 6.0),
(20004, 6.0),
(20005, 1.6);

COMMIT;
