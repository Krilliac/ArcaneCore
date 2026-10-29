namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// Excerpts of the two world dumps, the rows as the dumps have them (only the columns the importers read; the INSERT column lists follow
/// each dump's own CREATE TABLE, so the classic-db <c>conditions</c> rows carry <c>comments</c> and the vmangos ones do not):
/// <list type="bullet">
/// <item>classic-db 1.12.1 z2815 (Full_DB/ClassicDB_1_12_1_z2815.sql.gz): <c>dbscripts_on_spell</c> 9096 (Rift Spawn Manifestation, a DUMMY
/// spell: CAST_SPELL 9738 "Rift Spawn Becomes Visible") and <c>conditions</c> 3 (CONDITION_TEAM 67, Horde) and 4 (469, Alliance).</item>
/// <item>vmangos world database (mangos.sql): <c>spell_scripts</c> 26406 (Holiday - Lunar Teleport Cap: Stormwind, a SCRIPT_EFFECT spell: the
/// Alliance player casts 26454 on itself, the Horde player 26451 "Port to Moonglade") and <c>conditions</c> 2 (team 67) and 3 (team 469). Note
/// that vmangos 3 is Alliance where classic-db 3 is Horde.</item>
/// </list>
/// The dumps are GPL data and are not committed; these rows are fixtures, as the other real-row tests keep theirs.
/// </summary>
internal static class DbSpellScriptRows
{
    public const string ClassicDb = """
        INSERT INTO `dbscripts_on_spell` (`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`datalong3`,`buddy_entry`,`search_radius`,`data_flags`,`dataint`,`dataint2`,`dataint3`,`dataint4`,`datafloat`,`x`,`y`,`z`,`o`,`speed`,`condition_id`,`comments`) VALUES
        (9096,0,0,15,9738,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'Rift Spawn - cast Rift Spawn Becomes Visible');
        INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`,`comments`) VALUES
        (3,6,67,0,0,0,0,'Horde Player'),
        (4,6,469,0,0,0,0,'Alliance Player');
        """;

    public const string VMangos = """
        INSERT INTO `conditions` (`condition_entry`,`type`,`value1`,`value2`,`value3`,`value4`,`flags`) VALUES
        (2,6,67,0,0,0,0),
        (3,6,469,0,0,0,0);
        INSERT INTO `spell_scripts` (`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`datalong3`,`datalong4`,`target_param1`,`target_param2`,`target_type`,`data_flags`,`dataint`,`dataint2`,`dataint3`,`dataint4`,`x`,`y`,`z`,`o`,`condition_id`,`comments`) VALUES
        (26406,0,0,15,26454,2,0,0,0,0,0,5,0,0,0,0,0,0,0,0,3,'Lunar Teleport Cap: Stormwind - Teleport Alliance Player'),
        (26406,0,0,15,26451,2,0,0,0,0,0,5,0,0,0,0,0,0,0,0,2,'Lunar Teleport Cap: Stormwind - Port Horde Player to Moonglade');
        """;
}
