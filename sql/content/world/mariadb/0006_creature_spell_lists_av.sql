-- ArcaneCore content: the Alterac Valley commanders' spell lists (creature_spell_list_entry, creature_spell_list). Idempotent: deletes the shipped list ids, then inserts them.
-- The AV entry scripts replace the EventAI of these commanders, so the spells and timers of their classic-db creature_ai_scripts rows ship as lists here; AvEventAI names the list of each entry (entry * 100 + 1), the templates stay as imported.
-- Needs the world schema with the spell list tables (world schema 68, sql/content/README.md). Other list ids are not touched. A full import or refresh that carries the tables replaces them: run this file again afterwards.
-- provider: mariadb

START TRANSACTION;

DELETE FROM `creature_spell_list` WHERE `Id` IN (1317901, 1318001, 1318101, 1343701, 1343801, 1343901, 1357701);
DELETE FROM `creature_spell_list_entry` WHERE `Id` IN (1317901, 1318001, 1318101, 1343701, 1343801, 1343901, 1357701);

INSERT INTO `creature_spell_list_entry` (`Id`, `Name`, `ChanceSupportAction`, `ChanceRangedAttack`) VALUES
(1317901, 'Wing Commander Guse', 0, 0),
(1318001, 'Wing Commander Jeztor', 0, 0),
(1318101, 'Wing Commander Mulverick', 0, 0),
(1343701, 'Wing Commander Ichman', 0, 0),
(1343801, 'Wing Commander Slidore', 0, 0),
(1343901, 'Wing Commander Vipore', 0, 0),
(1357701, 'Ram Rider Commander', 0, 0);

INSERT INTO `creature_spell_list` (`Id`, `Position`, `SpellId`, `Flags`, `CombatCondition`, `TargetId`, `ScriptId`, `Availability`, `Probability`, `InitialMin`, `InitialMax`, `RepeatMin`, `RepeatMax`) VALUES
(1317901, 1, 15572, 0, -1, 1, 0, 100, 0, 0, 0, 5000, 9000),
(1317901, 2, 15580, 0, -1, 1, 0, 100, 0, 4100, 9300, 10200, 17900),
(1318001, 1, 15572, 0, -1, 1, 0, 100, 0, 0, 0, 5000, 9000),
(1318001, 2, 15580, 0, -1, 1, 0, 100, 0, 4100, 9300, 10200, 17900),
(1318101, 1, 15572, 0, -1, 1, 0, 100, 0, 0, 0, 5000, 9000),
(1318101, 2, 15580, 0, -1, 1, 0, 100, 0, 4100, 9300, 10200, 17900),
(1343701, 1, 15580, 0, -1, 1, 0, 100, 0, 4000, 6000, 7000, 9000),
(1343701, 2, 16509, 0, -1, 1, 0, 100, 0, 9000, 13000, 18000, 34000),
(1343801, 1, 15580, 0, -1, 1, 0, 100, 0, 4000, 6000, 7000, 9000),
(1343801, 2, 16509, 0, -1, 1, 0, 100, 0, 9000, 13000, 18000, 34000),
(1343901, 1, 15580, 0, -1, 1, 0, 100, 0, 4000, 6000, 7000, 9000),
(1343901, 2, 16509, 0, -1, 1, 0, 100, 0, 9000, 13000, 18000, 34000),
(1357701, 1, 16856, 0, -1, 1, 0, 100, 0, 5000, 8000, 9000, 13000),
(1357701, 2, 15284, 0, -1, 1, 0, 100, 0, 4000, 7000, 7000, 9000);

COMMIT;
