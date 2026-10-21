-- ArcaneCore content: bot scenario "Undercity siege" (game event 900). Idempotent: deletes the rows of the event, then inserts them.
-- Needs the world schema with the game_event_playerbot_* tables (docs/areas/playbots-scenarios-events.md, sql/content/README.md).
-- provider: sqlite

BEGIN;

DELETE FROM "game_event_playerbot_waypoint" WHERE "event" = 900;
DELETE FROM "game_event_playerbot_siege" WHERE "event" = 900;
DELETE FROM "game_event_playerbot_post" WHERE "event" = 900;
DELETE FROM "game_event_playerbot_group" WHERE "event" = 900;
DELETE FROM "game_event" WHERE "entry" = 900;

-- Serverside event: schedule_type 0 and no start_time/end_time, so only .event start and .event stop run it, in both dialects. occurence is twice the length
-- so that a stop sticks where the schedule is read as vmangos (the window of a stopped event reopens only after another occurence).
INSERT INTO "game_event" ("entry", "schedule_type", "occurence", "length", "holiday", "linked_to", "description", "hardcoded", "disabled", "patch_min", "patch_max")
VALUES (900, 0, 10368000, 5184000, 0, 0, 'Bot scenario: Undercity siege', 0, 0, 0, 10);

INSERT INTO "game_event_playerbot_group" ("event", "group_id", "name_prefix", "count", "team", "class_spec", "level", "provision", "start_map", "start_x", "start_y", "start_z", "start_o", "role", "comment") VALUES
(900, 1, 'Siegebot', 80, 0, '1:1,3:2,1:5,4:11,1:8,1:4,3:5,7:9,4:3,1:2,3:1,4:5,7:8,4:4,1:9,3:3,1:5,3:2,1:8,1:4', 60, 1, 0, 1925, 225, 49.41, 2.94, 2, 'Alliance attackers: marched in two raids from Tirisfal to the Undercity throne room'),
(900, 2, 'Ucltop', 10, 1, '2:1,5:5,2:7,6:11,5:9,8:8,2:3,5:4,6:1,8:7', 60, 1, 0, 1600, 277, 55.44, 0, 1, 'Horde defenders: Undercity: Ruins of Lordaeron, upper level'),
(900, 3, 'Uclbot', 10, 1, '2:1,5:5,2:7,6:11,5:9,8:8,2:3,5:4,6:1,8:7', 60, 1, 0, 1572, 288, -43.06, 0, 1, 'Horde defenders: Undercity: Ruins of Lordaeron, lower level'),
(900, 4, 'Uccanal', 10, 1, '2:1,5:5,2:7,6:11,5:9,8:8,2:3,5:4,6:1,8:7', 60, 1, 0, 1633, 287, -60.87, 0, 1, 'Horde defenders: Undercity: canals'),
(900, 5, 'Ucgate', 10, 1, '2:1,5:5,2:7,6:11,5:9,8:8,2:3,5:4,6:1,8:7', 60, 1, 0, 1462, 363, -59.31, 0, 1, 'Horde defenders: Undercity: Apothecarium gate'),
(900, 6, 'Ucroyal', 10, 1, '2:1,5:5,2:7,6:11,5:9,8:8,2:3,5:4,6:1,8:7', 60, 1, 0, 1308, 352, -65.96, 0, 1, 'Horde defenders: Undercity: Royal Quarter');

INSERT INTO "game_event_playerbot_post" ("event", "group_id", "post_id", "map", "x", "y", "z", "radius", "comment") VALUES
(900, 2, 1, 0, 1600, 277, 55.44, 40, 'Undercity: Ruins of Lordaeron, upper level'),
(900, 3, 1, 0, 1572, 288, -43.06, 40, 'Undercity: Ruins of Lordaeron, lower level'),
(900, 4, 1, 0, 1633, 287, -60.87, 40, 'Undercity: canals'),
(900, 5, 1, 0, 1462, 363, -59.31, 40, 'Undercity: Apothecarium gate'),
(900, 6, 1, 0, 1308, 352, -65.96, 40, 'Undercity: Royal Quarter');

INSERT INTO "game_event_playerbot_siege" ("event", "group_id", "objective_entry", "raids", "min_ready_pct", "comment") VALUES
(900, 1, 10181, 2, 100, 'Objective 10181 (Lady Sylvanas Windrunner), two raids');

INSERT INTO "game_event_playerbot_waypoint" ("event", "group_id", "seq", "map", "x", "y", "z", "comment") VALUES
(900, 1, 1, 0, 1925, 225, 49.41, NULL),
(900, 1, 2, 0, 1850, 240, 62.75, NULL),
(900, 1, 3, 0, 1640, 240, 62.76, NULL),
(900, 1, 4, 0, 1592, 277, 55.44, NULL),
(900, 1, 5, 0, 1579, 291, -43.03, NULL),
(900, 1, 6, 0, 1594, 350, -62.06, NULL),
(900, 1, 7, 0, 1476, 376, -59.12, NULL),
(900, 1, 8, 0, 1420, 428, -54.95, NULL),
(900, 1, 9, 0, 1350, 428, -55.71, NULL),
(900, 1, 10, 0, 1320, 323, -62.9, NULL);

COMMIT;
