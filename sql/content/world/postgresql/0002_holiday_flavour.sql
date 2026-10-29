-- ArcaneCore content: holiday flavour (how bots tell each festival: line-template key, name, crowd emotes, whether they gather). Idempotent: deletes the shipped holiday ids, then inserts them.
-- Needs the world schema with the holiday_flavour table (sql/content/README.md). Holiday id 0 is the flavour of a holiday the table does not name; rows with other ids are not touched.
-- provider: postgresql

BEGIN;

DELETE FROM "holiday_flavour" WHERE "holiday_id" IN (0, 141, 181, 201, 301, 321, 324, 327, 335, 341, 374, 375);

INSERT INTO "holiday_flavour" ("holiday_id", "flavour_key", "name", "emote1", "emote2", "emote3", "gather") VALUES
(0, 'festival', '', 21, 101, 34, 1),
(141, 'winterveil', 'Winter Veil', 21, 5, 0, 1),
(181, 'noblegarden', 'Noblegarden', 21, 101, 34, 1),
(201, 'children', 'Children''s Week', 21, 101, 34, 1),
(301, 'fishing', 'Fishing Extravaganza', 21, 101, 34, 0),
(321, 'harvest', 'Harvest Festival', 21, 101, 34, 1),
(324, 'hallows', 'Hallow''s End', 60, 21, 0, 1),
(327, 'lunar', 'Lunar Festival', 17, 21, 0, 1),
(335, 'love', 'Love is in the Air', 21, 101, 34, 1),
(341, 'midsummer', 'Midsummer', 34, 21, 0, 1),
(374, 'darkmoon', 'Darkmoon Faire', 21, 5, 60, 1),
(375, 'darkmoon', 'Darkmoon Faire', 21, 5, 60, 1);

COMMIT;
