using ArcaneCore.Data.World.Creatures;
using Xunit;

namespace ArcaneCore.Data.Tests.World;

/// <summary>vmangos <c>creature_template.immunity_flags</c> TAUNT bit becomes creature_immunities rows (Effect 114, State 11); hand-written rows.</summary>
public sealed class CreatureDumpImporterImmunityTests
{
    private const string Dump = """
        INSERT INTO `creature_template` (`entry`,`patch`,`name`,`subname`,`level_min`,`level_max`,`display_id1`,`immunity_flags`) VALUES
        (930401,0,'Taunt Proof','',60,60,1,2),(930402,0,'Other Bit','',60,60,1,1),(930403,0,'Plain','',60,60,1,0),
        (930404,0,'Upgraded','',60,60,1,2),(930404,1,'Upgraded','',60,60,1,0);
        """;

    [Fact]
    public void VMangosImmunityFlagsTaunt_BecomesAttackMeEffectAndModTauntStateRows()
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(Dump));

        Assert.Equal([(930401u, 0u, (byte)0, 114u), (930401u, 0u, (byte)1, 11u)],
            importer.ImmunitySnapshot().OrderBy(r => r.Entry).ThenBy(r => r.Type).Select(r => (r.Entry, r.SetId, r.Type, r.Value)));
        Assert.Equal(2, importer.BuildReport().Immunities);
    }
}
