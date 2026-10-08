namespace ArcaneCore.World.Tests.Npc;

/// <summary>
/// An excerpt of the classic-db 1.12.1 world dump z2815 (D:/refs/classic-db/Full_DB/ClassicDB_1_12_1_z2815.sql.gz), the rows as the dump has
/// them (only the columns the importers read, and no quest texts): four scripted quests, their givers and enders, their DB scripts and Ruul
/// Snowhoof's escort path.
/// <list type="bullet">
/// <item>2843 "Gnomer-gooooone!" (Scooty, 7853): StartScript 2843, QUEST_EXPLORED 2843 at 10 s.</item>
/// <item>2480 "Hinott's Assistance" (Serge Hinott, 2391): StartScript 2480, MOVE_TO at 2 s, TALK 3326 at 20 s, QUEST_EXPLORED at 30 s,
/// MOVE_TO back at 31 s.</item>
/// <item>8984 "The Source Revealed" (ended by Apothecary Staffron Lerent, 16107): CompleteScript 9028 (not the quest id), NPC flag
/// questgiver off at 1 s, Annalise Lerent (16110) summoned at 2 s, the dialogue, the flag back at 54 s.</item>
/// <item>6482 "Freedom to Ruul" (Ruul Snowhoof, 12818; ended by Yama Snowhoof, 12837): StartScript 6482 (OPEN_DOOR 48166, a command this
/// server does not run), and the ScriptDev2 escort over the 36 script_waypoint points of entry 12818; the ambushers 3924-3926.</item>
/// </list>
/// The dump is GPL data and is not committed; these rows are fixtures, as the other real-row tests keep theirs.
/// </summary>
internal static class ClassicDbScriptedQuestRows
{
    public const string Dump = """
        INSERT INTO `creature_template` (`Entry`,`Name`,`MinLevel`,`MaxLevel`,`ModelId1`,`Faction`,`NpcFlags`,`UnitFlags`,`SpeedWalk`,`SpeedRun`,`Rank`,`MinLevelHealth`,`MaxLevelHealth`,`MovementType`,`InhabitType`,`Civilian`,`AIName`) VALUES
        (2391,'Serge Hinott',32,32,3678,68,19,37376,1,1.14286,0,1162,1162,0,3,1,''),
        (3924,'Thistlefur Shaman',23,24,6801,82,0,0,0.666668,0.992063,0,543,582,1,3,0,'EventAI'),
        (3925,'Thistlefur Avenger',23,24,6823,82,0,0,0.666668,0.992063,0,617,664,1,3,0,'EventAI'),
        (3926,'Thistlefur Pathfinder',23,24,897,82,0,0,1.05,1.14286,0,617,664,1,3,0,'EventAI'),
        (7853,'Scooty',30,30,7036,120,3,512,1,1.14286,0,1002,1002,0,3,1,''),
        (12818,'Ruul Snowhoof',26,26,12969,714,2,4352,1.2,1.14286,0,1125,1125,0,3,0,''),
        (12837,'Yama Snowhoof',26,26,12976,104,2,4096,1.05,1.14286,0,787,787,0,3,0,''),
        (16107,'Apothecary Staffron Lerent',5,5,16007,35,2,0,1.1,1.14286,0,102,102,0,3,0,''),
        (16110,'Annalise Lerent',5,5,16010,35,0,0,1,1.14286,0,42,42,0,3,0,'');
        INSERT INTO `creature` (`guid`,`id`,`map`,`spawnMask`,`position_x`,`position_y`,`position_z`,`orientation`,`spawntimesecsmin`,`spawntimesecsmax`,`spawndist`,`MovementType`) VALUES
        (2,7853,0,1,-14464.9,459.585,15.2488,3.735,300,300,0,0),
        (15293,2391,0,1,-9.28586,-905.473,57.5575,1.37881,300,300,0,0),
        (32333,12818,1,1,3347.35,-694.701,159.926,3.05433,300,300,0,0),
        (79560,16107,0,1,90.2991,-1723.58,220.193,3.63373,30,30,0,0);
        INSERT INTO `quest_template` (`entry`,`Method`,`ZoneOrSort`,`MinLevel`,`MaxLevel`,`QuestLevel`,`Type`,`RequiredClasses`,`RequiredRaces`,`RequiredSkill`,`RequiredSkillValue`,`RequiredCondition`,`QuestFlags`,`SpecialFlags`,`PrevQuestId`,`NextQuestId`,`ExclusiveGroup`,`NextQuestInChain`,`SrcItemId`,`SrcItemCount`,`SrcSpell`,`Title`,`ReqItemId1`,`ReqItemCount1`,`ReqCreatureOrGOId1`,`ReqCreatureOrGOCount1`,`RewOrReqMoney`,`RewMoneyMaxLevel`,`RewRepFaction1`,`RewRepValue1`,`StartScript`,`CompleteScript`) VALUES
        (2480,2,-162,20,255,20,0,8,178,0,0,0,2,2,2479,0,0,0,0,0,0,'Hinott''s Assistance',0,0,0,0,0,96,0,0,2480,0),
        (2843,2,133,20,255,35,81,0,178,0,0,0,2,2,2842,0,0,0,0,0,0,'Gnomer-gooooone!',0,0,0,0,0,0,0,0,2843,0),
        (6482,2,331,19,255,24,0,0,178,0,0,0,2,2,0,0,0,0,0,0,0,'Freedom to Ruul',0,0,0,0,1700,1440,81,350,6482,0),
        (8984,2,-22,1,255,60,0,0,178,0,0,0,8,0,8983,9029,9028,9029,0,0,0,'The Source Revealed',0,0,0,0,0,0,0,0,0,9028);
        INSERT INTO `creature_questrelation` (`id`,`quest`) VALUES
        (2391,2480),
        (7853,2843),
        (12818,6482),
        (16109,8984);
        INSERT INTO `creature_involvedrelation` (`id`,`quest`) VALUES
        (2391,2480),
        (7853,2843),
        (12837,6482),
        (16107,8984);
        INSERT INTO `dbscripts_on_quest_start` (`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`datalong3`,`buddy_entry`,`search_radius`,`data_flags`,`dataint`,`dataint2`,`dataint3`,`dataint4`,`datafloat`,`x`,`y`,`z`,`o`,`speed`,`condition_id`,`comments`) VALUES
        (2480,2000,0,3,0,0,0,0,0,0,0,0,0,0,0,-4.33,-900.68,57.54,1.54,0,0,''),
        (2480,20000,0,0,0,0,0,0,0,0,3326,0,0,0,0,0,0,0,0,0,0,''),
        (2480,30000,0,7,2480,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,''),
        (2480,31000,0,3,0,0,0,0,0,0,0,0,0,0,0,-4.66,-903.92,57.54,3.48,0,0,''),
        (2843,10000,0,7,2843,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,''),
        (6482,0,0,11,48166,30,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'');
        INSERT INTO `dbscripts_on_quest_end` (`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`datalong3`,`buddy_entry`,`search_radius`,`data_flags`,`dataint`,`dataint2`,`dataint3`,`dataint4`,`datafloat`,`x`,`y`,`z`,`o`,`speed`,`condition_id`,`comments`) VALUES
        (9028,1000,0,29,2,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'Staffron - Remove NPC Flag Questgiver'),
        (9028,2000,0,10,16110,40000,0,0,0,0,0,0,0,0,0,95.6559,-1713.36,220.826,4.26772,0,0,'Summon Annalise Lerent'),
        (9028,5000,0,0,0,0,0,16110,40,0,11838,0,0,0,0,0,0,0,0,0,0,'Annalise - Dialogue 1'),
        (9028,9000,0,36,0,0,0,16110,40,1,0,0,0,0,0,0,0,0,0,0,0,'Staffron - Turns to Annalise'),
        (9028,13000,0,0,0,0,0,0,0,0,11836,0,0,0,0,0,0,0,0,0,0,'Staffron - Dialogue 1'),
        (9028,17000,0,0,0,0,0,16110,40,0,11837,0,0,0,0,0,0,0,0,0,0,'Annalise - Dialogue 2'),
        (9028,21000,0,0,0,0,0,0,0,0,11839,0,0,0,0,0,0,0,0,0,0,'Staffron - Dialogue 2'),
        (9028,25000,0,0,0,0,0,0,0,0,11840,0,0,0,0,0,0,0,0,0,0,'Staffron - Dialogue 3'),
        (9028,29000,0,0,0,0,0,16110,40,0,11841,0,0,0,0,0,0,0,0,0,0,'Annalise - Dialogue 3'),
        (9028,33000,0,0,0,0,0,16110,40,0,11842,0,0,0,0,0,0,0,0,0,0,'Annalise - Dialogue 4'),
        (9028,37000,0,0,0,0,0,16110,40,0,11843,0,0,0,0,0,0,0,0,0,0,'Annalise - Dialogue 5'),
        (9028,41000,0,0,0,0,0,0,0,0,11844,0,0,0,0,0,0,0,0,0,0,'Staffron - Dialogue 4'),
        (9028,45000,0,0,0,0,0,0,0,0,11845,0,0,0,0,0,0,0,0,0,0,'Staffron - Dialogue 5'),
        (9028,49000,0,0,0,0,0,0,0,0,11846,0,0,0,0,0,0,0,0,0,0,'Staffron - Dialogue 6'),
        (9028,53000,0,0,0,0,0,0,0,0,11847,0,0,0,0,0,0,0,0,0,0,'Staffron - Dialogue 7'),
        (9028,54000,0,36,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'Staffron - Reset orientation'),
        (9028,54000,0,29,2,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'Staffron - Add NPC Flag Questgiver');
        INSERT INTO `script_waypoint` (`Entry`,`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`,`Comment`) VALUES
        (12818,0,1,3347.25,-694.701,159.926,0,0,0,''),
        (12818,0,2,3341.53,-694.726,161.125,0,1000,0,''),
        (12818,0,3,3338.35,-686.088,163.444,0,0,0,''),
        (12818,0,4,3352.74,-677.722,162.316,0,0,0,''),
        (12818,0,5,3370.29,-669.367,160.751,0,0,0,''),
        (12818,0,6,3381.48,-659.449,162.545,0,0,0,''),
        (12818,0,7,3389.55,-648.5,163.652,0,0,0,''),
        (12818,0,8,3396.65,-641.509,164.216,0,0,0,''),
        (12818,0,9,3410.5,-634.3,165.773,0,0,0,''),
        (12818,0,10,3418.46,-631.792,166.478,0,0,0,''),
        (12818,0,11,3429.5,-631.589,166.921,0,0,0,''),
        (12818,0,12,3434.95,-629.245,168.334,0,0,0,''),
        (12818,0,13,3438.93,-618.503,171.503,0,0,0,''),
        (12818,0,14,3444.22,-609.294,173.078,0,1000,0,'Ambush 1'),
        (12818,0,15,3460.51,-593.794,174.342,0,0,0,''),
        (12818,0,16,3480.28,-578.21,176.652,0,0,0,''),
        (12818,0,17,3492.91,-562.335,181.396,0,0,0,''),
        (12818,0,18,3495.23,-550.978,184.652,0,0,0,''),
        (12818,0,19,3496.25,-529.194,188.172,0,0,0,''),
        (12818,0,20,3497.62,-510.411,188.345,0,0,0,''),
        (12818,0,21,3498.5,-497.788,185.806,0,0,0,''),
        (12818,0,22,3484.22,-489.718,182.39,0,0,0,''),
        (12818,0,23,3469.44,-481.94,175.62,0,0,0,''),
        (12818,0,24,3449.15,-471.29,168.49,0,0,0,''),
        (12818,0,25,3426.67,-456.34,158.85,0,0,0,''),
        (12818,0,26,3406.53,-446.61,153.57,0,0,0,''),
        (12818,0,27,3386.2,-437.82,151.93,0,0,0,''),
        (12818,0,28,3349.58,-439.28,151.92,0,0,0,''),
        (12818,0,29,3310.3,-467.28,152.24,0,0,0,''),
        (12818,0,30,3290.63,-507.6,153.61,0,0,0,''),
        (12818,0,31,3272.89,-524.97,154.31,0,1000,0,'Ambush 2'),
        (12818,0,32,3231.15,-524.41,147.63,0,2000,0,'Quest credit'),
        (12818,0,33,3231.15,-524.41,147.63,0,4000,0,'Thanks players'),
        (12818,0,34,3231.15,-524.41,147.63,0,4000,0,'Shapeshift'),
        (12818,0,35,3175.96,-494.54,140.79,0,0,0,''),
        (12818,0,36,3168.17,-480.43,139.36,0,0,0,'Despawn');
        """;
}
