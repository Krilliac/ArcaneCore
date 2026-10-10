using System.Buffers.Binary;
using ArcaneCore.Data.Content.Pets;
using ArcaneCore.Game.Pets;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Tests.Characters.Creation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Pets;

/// <summary>PetsFeature wiring of <see cref="PetTraining"/>: CreatureFamily.dbc skill lines plus a 15-field SkillLineAbility.dbc turn it on.</summary>
public sealed class PetsFeatureTrainingTests
{
    private const uint WolfFamily = 1;
    private const uint WolfSkillLine = 208;
    private const uint Bite = 17253;

    private static Action<IServiceCollection> Configure(Dictionary<string, string?> values, LogCapture logs) => services =>
    {
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        logs.Register(services);
    };

    private static string WriteFamilies()
    {
        uint[] wolf = new uint[CreatureFamilyDbcReader.FieldCount];
        wolf[0] = WolfFamily;
        wolf[CreatureFamilyDbcReader.SkillLineField] = WolfSkillLine;
        wolf[CreatureFamilyDbcReader.SkillLineField + 1] = 270;
        string path = Path.Combine(Path.GetTempPath(), $"CreatureFamily-{Guid.NewGuid():N}.dbc");
        File.WriteAllBytes(path, Image(CreatureFamilyDbcReader.FieldCount, wolf));
        return path;
    }

    private static string WriteAbilities(int fields)
    {
        // id, skill, spell, racemask, classmask, (2 unused), req value, forward spell, learn on get, max, min, (2 unused), reqtrainpoints
        uint[] bite = new uint[fields];
        bite[0] = 1;
        bite[1] = WolfSkillLine;
        bite[2] = Bite;
        if (fields == 15)
        {
            bite[14] = 1;
        }

        string path = Path.Combine(Path.GetTempPath(), $"SkillLineAbility-{Guid.NewGuid():N}.dbc");
        File.WriteAllBytes(path, Image(fields, bite));
        return path;
    }

    [Fact]
    public async Task Attach_WiresPetTraining_FromTheFamilyAndSkillLineAbilityDbcs()
    {
        string families = WriteFamilies();
        string abilities = WriteAbilities(15);
        var logs = new LogCapture();
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: Configure(new()
            {
                ["Pets:CreatureFamilyDbcPath"] = families,
                ["NpcServices:SkillLineAbilityDbcPath"] = abilities,
            }, logs));

            PetTraining training = Assert.IsType<PetTraining>(host.WorldServices.GetRequiredService<PetsFeature>().Service.Training);
            Assert.True(training.Abilities.HasTrainingPoints);
            Assert.Equal(1u, training.Abilities.TrainingPoints(Bite));
            Assert.DoesNotContain(logs.Lines, line => line.Contains("training-point costs and family checks are off", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(families);
            File.Delete(abilities);
        }
    }

    [Fact]
    public async Task Attach_WithoutTrainingPointData_LeavesTrainingOff()
    {
        string families = WriteFamilies();
        string abilities = WriteAbilities(14);
        var logs = new LogCapture();
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: Configure(new()
            {
                ["Pets:CreatureFamilyDbcPath"] = families,
                ["NpcServices:SkillLineAbilityDbcPath"] = abilities,
            }, logs));

            Assert.Null(host.WorldServices.GetRequiredService<PetsFeature>().Service.Training);
            Assert.Single(logs.Lines, line => line.Contains("training-point costs and family checks are off", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(families);
            File.Delete(abilities);
        }
    }

    private static byte[] Image(int fields, params uint[][] records)
    {
        byte[] image = new byte[20 + (records.Length * fields * 4) + 1];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 1);
        for (int record = 0; record < records.Length; record++)
        {
            for (int field = 0; field < fields; field++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (((record * fields) + field) * 4)), records[record][field]);
            }
        }

        return image;
    }
}
