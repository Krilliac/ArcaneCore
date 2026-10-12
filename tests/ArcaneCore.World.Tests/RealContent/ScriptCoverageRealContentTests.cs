using System.Globalization;
using System.Text.RegularExpressions;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.RealContent;

/// <summary>
/// The script coverage of the 5-player dungeons of the real world fixture (<c>ARCANECORE_TEST_WORLD_DB</c>, opened read-only and immutable, never
/// written): every ScriptName a spawned creature of a dungeon map carries must resolve to an implementation through <see cref="ScriptCatalog"/>,
/// or be in one of two explicit lists. A new gap fails this test; closing a gap fails it too until the entry leaves its list.
/// </summary>
public sealed class ScriptCoverageRealContentTests(ITestOutputHelper output)
{
    /// <summary>
    /// (map, creature entry, ScriptName) rows whose ScriptName NO layer of the catalogue implements and that nothing registers at run time
    /// either: the creature runs its AIName's AI (EventAI/default) instead. Generated from the fixture state; each is a real content gap
    /// (a ScriptDev2 script not ported), not a verified-absent behaviour. Remove a row when its script lands.
    /// </summary>
    private static readonly (uint Map, uint Entry, string Script)[] KnownMissing =
    [
        // Uldaman: the adds only wake through ArchaedasAi (the constants in UldamanInstance.cs); their own script is not ported, EventAI runs them.
        (70, 7076, "mob_archaeras_add"), (70, 7077, "mob_archaeras_add"), (70, 7309, "mob_archaeras_add"), (70, 10120, "mob_archaeras_add"),
        // Blackrock Depths: BlackrockDepthsInstance.Events.cs registers an AI for other entries only; these have a ScriptName and no AI.
        (230, 8982, "npc_ironhand_guardian"), (230, 9022, "npc_dughal_stormwing"), (230, 9023, "npc_marshal_windsor"),
        (230, 9500, "npc_mistress_nagmara"), (230, 9503, "npc_rocknot"), (230, 9679, "npc_tobias_seecher"), (230, 16079, "npc_theldren_trigger"),
        // Doom'rel has only the gossip (DoomrelGossip, an INpcGossipScript registered by DungeonEventFeature); the catalogue answers for AIs.
        (230, 9039, "boss_doomrel"),
        // Stratholme: Aurius runs EventAI only.
        (329, 10917, "npc_aurius"),
    ];

    /// <summary>
    /// Rows the catalogue cannot see statically because an instance script registers the AI when the map's instance is created
    /// (<c>RegisterEntryAi</c>, DungeonBossAis-style); each cites the registering source file, and the test checks that file still names the entry.
    /// </summary>
    private static readonly (uint Map, uint Entry, string Script, string SourceFile, string Token)[] RegisteredByInstanceScript =
    [
        (33, 3849, "npc_shadowfang_prisoner", Sfk, "NpcAda"), (33, 3850, "npc_shadowfang_prisoner", Sfk, "NpcAsh"),
        (33, 4275, "boss_arugal", Sfk, "NpcArchmageArugal"), (33, 4444, "npc_deathstalker_vincent", Sfk, "4444"),
        (36, 646, "boss_mr_smite", "Deadmines/DeadminesInstance.cs", "NpcMrSmite"),
        (43, 3678, "npc_disciple_of_naralex", "WailingCaverns/WailingCavernsInstance.Events.cs", "NpcDisciple"),
        (47, 4508, "npc_willix_the_importer", "Classic/RazorfenKraulInstance.cs", "4508"),
        (70, 2748, "boss_archaedas", "Uldaman/UldamanInstance.cs", "Archaedas"),
        (90, 7800, "boss_thermaplugg", Gnomer, "7800"), (90, 7850, "npc_kernobee", Gnomer, "7850"),
        (90, 7998, "npc_blastmaster_emi_shortfuse", Gnomer, "Blastmaster"),
        (129, 8516, "npc_belnistrasz", "RazorfenDowns/RazorfenDownsInstance.cs", "BelnistraszAi.Entry"),
        (189, 3975, "boss_herod", Sm, "3975"), (189, 3976, "boss_scarlet_commander_mograine", Sm, "Mograine"),
        (189, 3977, "boss_high_inquisitor_whitemane", Sm, "Whitemane"), (189, 6487, "boss_arcanist_doan", Sm, "6487"),
        (209, 7271, "boss_zumrah", "ZulFarrak/ZulFarrakInstance.cs", "7271"),
        (230, 8929, "boss_moira_bronzebeard", Brd, "NpcPrincess"), (230, 9018, "boss_high_interrogator_gerstahn", Brd, "9018"),
        (230, 9019, "boss_emperor_dagran_thaurissan", Brd, "NpcEmperor"), (230, 9033, "boss_general_angerforge", Brd, "9033"),
        (230, 9156, "boss_ambassador_flamelash", Brd, "NpcAmbassadorFlamelash"), (230, 9499, "boss_plugger_spazzring", Brd, "NpcPlugger"),
        (230, 9502, "npc_phalanx", Brd, "NpcPhalanx"),
        (349, 13282, "boss_noxxion", "Maraudon/MaraudonInstance.cs", "NpcNoxxion"),
    ];

    private const string Sfk = "ShadowfangKeep/ShadowfangKeepInstance.Events.cs";
    private const string Gnomer = "Gnomeregan/GnomereganInstance.cs";
    private const string Sm = "ScarletMonastery/ScarletMonasteryInstance.cs";
    private const string Brd = "BlackrockDepths/BlackrockDepthsInstance.Events.cs";


    private sealed record Loaded(CreatureContent Content, uint[] DungeonMaps);

    private static Loaded Load(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        var maps = new List<uint>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Entry FROM map_template WHERE MapType = 1 ORDER BY Entry";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) maps.Add((uint)reader.GetInt64(0));
        }

        var spawns = new List<CreatureSpawn>();
        var entries = new HashSet<uint>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT s.Guid, s.Entry, s.MapId FROM creature_spawn s JOIN map_template m ON m.Entry = s.MapId WHERE m.MapType = 1";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                spawns.Add(new CreatureSpawn { Guid = (uint)reader.GetInt64(0), Entry = (uint)reader.GetInt64(1), MapId = (uint)reader.GetInt64(2), X = 0, Y = 0, Z = 0 });
                entries.Add((uint)reader.GetInt64(1));
            }
        }

        var templates = new List<CreatureTemplate>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Entry, Name, AIName, ScriptName FROM creature_template";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                uint entry = (uint)reader.GetInt64(0);
                if (!entries.Contains(entry)) continue;
                templates.Add(new CreatureTemplate
                {
                    Entry = entry,
                    Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    AIName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    ScriptName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                });
            }
        }

        return new Loaded(new CreatureContent(templates, spawns, [], [], []), [.. maps]);
    }

    [RealWorldContentFact]
    public void EveryDungeonScriptName_HasAnImplementation_OrIsInAnExplicitList()
    {
        Loaded world = Load(Environment.GetEnvironmentVariable(RealWorldContentFactAttribute.Variable)!);
        var catalog = new ScriptCatalog(new CreatureAiFactory(), world.Content);
        var unresolved = new List<ScriptCoverageRow>();
        var all = new List<ScriptCoverageRow>();
        foreach (uint map in world.DungeonMaps)
        {
            IReadOnlyList<ScriptCoverageRow> rows = catalog.Coverage(map);
            ScriptCoverageSummary summary = ScriptCoverageSummary.Of(rows);
            all.AddRange(rows);
            unresolved.AddRange(rows.Where(r => r.ScriptName.Length > 0 && !r.Resolution.ScriptNameResolved));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"map {map}: {summary.ScriptNames} ScriptNames, {summary.ScriptNamesResolved} implemented, {summary.ScriptNamesMaybeInstanceRegistered} on an instance-script map without a static implementation, {summary.ScriptNamesMissing} elsewhere without; {summary.AiNames} AINames, {summary.AiNamesUnknown} unknown"));
        }

        ScriptCoverageSummary total = ScriptCoverageSummary.Of(all);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"dungeon maps {world.DungeonMaps.Length}: {total.ScriptNames} ScriptNames, {total.ScriptNamesResolved} implemented statically, {unresolved.Count} unresolved statically (known missing {KnownMissing.Length}, instance-registered {RegisteredByInstanceScript.Length}); {total.AiNames} AINames, {total.AiNamesUnknown} unknown"));
        foreach (ScriptCoverageRow row in unresolved)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  unresolved ({row.MapId}, {row.Entry}, \"{row.ScriptName}\") {row.Name}"));
        }

        Assert.True(total.ScriptNames > 0, "the fixture has no dungeon ScriptNames: the coverage saw nothing");

        var listed = new HashSet<(uint, uint, string)>([.. KnownMissing, .. RegisteredByInstanceScript.Select(r => (r.Map, r.Entry, r.Script))]);
        var actual = new HashSet<(uint, uint, string)>(unresolved.Select(r => (r.MapId, r.Entry, r.ScriptName)));
        Assert.Empty(actual.Except(listed).OrderBy(t => t.Item1).ThenBy(t => t.Item2)); // a new gap: implement it or list it with a reason
        Assert.Empty(listed.Except(actual).OrderBy(t => t.Item1).ThenBy(t => t.Item2)); // a closed gap: remove it from its list
    }

    /// <summary>
    /// The instance-registered rows are not proven by the catalogue (it cannot see a run-time <c>RegisterEntryAi</c>), so each must at least still be
    /// cited by its instance script: the file calls <c>RegisterEntryAi</c> and names the entry's token. A deleted registration fails here.
    /// </summary>
    [Fact]
    public void EveryInstanceRegisteredRow_IsStillCitedByItsInstanceScript()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "ArcaneCore.slnx"))) root = Path.GetDirectoryName(root);
        Assert.True(root is not null, "the repository root (ArcaneCore.slnx) was not found above the test binaries");

        foreach ((uint map, uint entry, string script, string file, string token) in RegisteredByInstanceScript)
        {
            string path = Path.Combine(root!, "src", "ArcaneCore.Game", "Instances", "Scripts", file);
            Assert.True(File.Exists(path), $"{script} ({map}, {entry}): {file} does not exist");
            string text = File.ReadAllText(path);
            Assert.True(text.Contains("RegisterEntryAi", StringComparison.Ordinal), $"{script} ({map}, {entry}): {file} no longer calls RegisterEntryAi");
            Assert.True(Regex.IsMatch(text, @"\b" + Regex.Escape(token) + @"\b"),
                $"{script} ({map}, {entry}): {file} no longer names {token}");
        }
    }
}
