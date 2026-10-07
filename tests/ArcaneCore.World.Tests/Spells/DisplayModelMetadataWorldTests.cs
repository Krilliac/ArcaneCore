using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class DisplayModelMetadataWorldTests
{
    [Fact]
    public async Task NativeMetadata_IsAppliedToTheFirstPlayerCreatePacket()
    {
        string directory = Directory.CreateTempSubdirectory("arcane-native-create-").FullName;
        try
        {
            string displays = Path.Combine(directory, "CreatureDisplayInfo.dbc");
            string models = Path.Combine(directory, "CreatureModelData.dbc");
            File.WriteAllBytes(displays, Image(12, Row(12, (0, 49u), (1, 11u), (4, 1.5f))));
            File.WriteAllBytes(models, Image(16, Row(16, (0, 11u), (4, 2f), (15, 4f))));
            CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([], [], [],
                [new(49, 0.4f, 1.2f, 0, 0)], []));
            WorldTestHost host;
            try { host = WorldTestHost.Start(configureServices: services => services.AddSingleton(Configuration(displays, models))); }
            finally { CreatureTestStore.Current.Value = null; }
            await using (host)
            await using (WorldTestClient client = await host.EnterWorldAsync("NATIVECREATE", "Nativecreate"))
            {
                Dictionary<int, uint> fields = Talents.TalentWorldTests.ReadSelfFields(client.LoginPacket(WorldOpcode.SmsgUpdateObject));
                Assert.Equal(49u, fields[UpdateFields.UnitFieldDisplayid]);
                Assert.Equal(3f, BitConverter.UInt32BitsToSingle(fields[UpdateFields.ObjectFieldScaleX]), 4);
                Assert.Equal(0.4f, BitConverter.UInt32BitsToSingle(fields[UpdateFields.UnitFieldBoundingradius]), 4);
                Assert.Equal(1.2f, BitConverter.UInt32BitsToSingle(fields[UpdateFields.UnitFieldCombatreach]), 4);
                Assert.Equal(2f, await host.PlayerStateAsync("Nativecreate", p => p.Locomotion.CollisionHeight), 4);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ConfiguredMetadata_SocketTransformAndScaleUpdateGeometry_ThenRestoreNative()
    {
        string directory = Directory.CreateTempSubdirectory("arcane-model-metadata-").FullName;
        try
        {
            string displays = Path.Combine(directory, "CreatureDisplayInfo.dbc");
            string models = Path.Combine(directory, "CreatureModelData.dbc");
            File.WriteAllBytes(displays, Image(12, Row(12, (0, 700u), (1, 10u), (4, 1.5f)),
                Row(12, (0, 49u), (1, 11u), (4, 1f))));
            File.WriteAllBytes(models, Image(16, Row(16, (0, 10u), (4, 2f), (15, 4f)),
                Row(16, (0, 11u), (4, 1f), (15, 2f))));
            IConfiguration configuration = Configuration(displays, models);
            var template = new CreatureTemplate { Entry = 56700, Name = "Synthetic transform", DisplayIds = [700], Scale = 1 };
            CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [], [],
                [new(700, 0.5f, 1.5f, 0, 0), new(49, 0.4f, 1.2f, 0, 0)], []));
            WorldTestHost host;
            try { host = WorldTestHost.Start(configureServices: services => services.AddSingleton(configuration)); }
            finally { CreatureTestStore.Current.Value = null; }
            await using (host)
            await using (WorldTestClient client = await host.EnterWorldAsync("MODEL", "Model"))
            {
                await host.OnWorldAsync(() =>
                {
                    SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
                    feature.System.Store = new SpellStore([.. feature.System.Store.All,
                        Aura(998301, AuraType.Transform, 0, 56700), Aura(998302, AuraType.ModScale, 50, 0)], [], []);
                    Player player = host.World.FindOnlinePlayer("Model")!;
                    player.NativeDisplayId = 49;
                    player.DisplayId = 49;
                    player.SetFloat(UpdateFields.ObjectFieldScaleX, 1);
                    feature.Spellbook.LearnSpell(player, 998301);
                    feature.Spellbook.LearnSpell(player, 998302);
                });
                await client.SendAsync(WorldOpcode.CmsgCastSpell, Cast(998301));
                await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
                await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Model")!.DisplayId == 700, "metadata transform display");
                await AssertGeometry(host, 3f, 0.5f, 1.5f, 2f);
                await client.SendAsync(WorldOpcode.CmsgCastSpell, Cast(998302));
                await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
                await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Model")!.GetFloat(UpdateFields.ObjectFieldScaleX) > 4, "metadata scale aura");
                await AssertGeometry(host, 4.5f, 0.75f, 2.25f, 3f);
                await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.RemoveAuras(
                    host.World.FindOnlinePlayer("Model")!, 998301));
                Assert.Equal(49u, await host.PlayerStateAsync("Model", p => p.DisplayId));
                await AssertGeometry(host, 1.5f, 0.6f, 1.8f, 3f);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void PartialConfigurationAndMalformedFiles_FailStartup()
    {
        Assert.Throws<InvalidOperationException>(() => WorldTestHost.Start(configureServices: services =>
            services.AddSingleton(Configuration("configured", null))));
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Throws<InvalidDataException>(() => WorldTestHost.Start(configureServices: services =>
                services.AddSingleton(Configuration(path, path))));
        }
        finally { File.Delete(path); }
    }

    private static async Task AssertGeometry(WorldTestHost host, float scale, float radius, float reach, float height)
    {
        var actual = await host.PlayerStateAsync("Model", p => (p.GetFloat(UpdateFields.ObjectFieldScaleX),
            p.BoundingRadius, p.GetFloat(UpdateFields.UnitFieldCombatreach), p.Locomotion.CollisionHeight));
        Assert.Equal(scale, actual.Item1, 3);
        Assert.Equal(radius, actual.Item2, 3);
        Assert.Equal(reach, actual.Item3, 3);
        Assert.Equal(height, actual.Item4, 3);
    }

    private static IConfiguration Configuration(string displays, string? models) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> {
            ["Creatures:CreatureDisplayInfoDbcPath"] = displays, ["Creatures:CreatureModelDataDbcPath"] = models }).Build();
    private static SpellInfo Aura(uint id, AuraType type, int amount, int misc) => new()
    {
        Id = id, Name = "Synthetic metadata aura", Duration = new SpellDuration(60_000, 0, 60_000),
        Effects = [new() { Effect = SpellEffectName.ApplyAura, AuraType = type, BasePoints = amount - 1,
            BaseDice = 1, DieSides = 1, MiscValue = misc, TargetA = SpellImplicitTarget.UnitCaster }],
    };
    private static byte[] Cast(uint id)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt32(id);
        SpellCastTargets.ForSelf().Write(writer);
        return writer.ToArray();
    }
    private static byte[] Image(int fields, params byte[][] rows)
    {
        byte[] bytes = new byte[20 + rows.Length * fields * 4 + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x43424457);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)rows.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1);
        for (int i = 0; i < rows.Length; i++) rows[i].CopyTo(bytes, 20 + i * fields * 4);
        return bytes;
    }
    private static byte[] Row(int fields, params (int Field, object Value)[] values)
    {
        byte[] row = new byte[fields * 4];
        foreach (var (field, value) in values)
            if (value is uint number) BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(field * 4), number);
            else BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan(field * 4), (float)value);
        return row;
    }
}
