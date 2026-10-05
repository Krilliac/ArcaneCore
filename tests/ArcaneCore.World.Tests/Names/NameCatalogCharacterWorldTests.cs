using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Protocol;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Names;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Names;

public sealed class NameCatalogCharacterWorldTests
{
    [Fact]
    public async Task ConfiguredProfanityCatalog_RejectsCreateWithoutPersisting_AndAllowsNormalizedName()
    {
        string dir = Directory.CreateTempSubdirectory("arcane-character-names-").FullName;
        try
        {
            string profanity = Path.Combine(dir, "NamesProfanity.dbc");
            string reserved = Path.Combine(dir, "NamesReserved.dbc");
            File.WriteAllBytes(profanity, Image(["\\^bad\\$"]));
            File.WriteAllBytes(reserved, Image(["\\^root\\$"]));
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Names:NamesProfanityDbcPath"] = profanity,
                ["Names:NamesReservedDbcPath"] = reserved,
            }).Build();
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton<IConfiguration>(config);
                // The Administrator bypass must not override a NamesReserved.dbc match when
                // the same name is also present in the SQL exact snapshot.
                services.AddSingleton<IReservedNameStore>(new InlineReservedNameStore(["root"]));
            });
            byte[] key = await host.AddAccountAsync("CATALOG", AccountSecurity.Administrator);
            await using WorldTestClient client = await host.ConnectAsync();
            await client.AuthenticateAsync("CATALOG", key);
            Assert.Equal((byte)CharResult.CharNameProfane, await client.TryCreateCharacterAsync("bad"));
            Assert.Equal((byte)CharResult.CharNameReserved, await client.TryCreateCharacterAsync("root"));
            Assert.Equal((byte)CharResult.CharCreateSuccess, await client.TryCreateCharacterAsync("gOoD"));
            await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
            byte[] list = (await client.ReadAsync()).Payload;
            Assert.Equal(1, list[0]);
            Assert.Contains("Good", Encoding.UTF8.GetString(list));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static byte[] Image(string[] patterns)
    {
        var strings = new List<byte> { 0 };
        var offsets = new List<uint>();
        foreach (string pattern in patterns)
        {
            offsets.Add((uint)strings.Count);
            strings.AddRange(Encoding.UTF8.GetBytes(pattern));
            strings.Add(0);
        }
        byte[] image = new byte[20 + patterns.Length * 8 + strings.Count];
        Encoding.ASCII.GetBytes("WDBC").CopyTo(image, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)patterns.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)strings.Count);
        for (int i = 0; i < patterns.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (i * 8) + 4), offsets[i]);
        strings.ToArray().CopyTo(image, 20 + patterns.Length * 8);
        return image;
    }

    private sealed class InlineReservedNameStore(IEnumerable<string> names) : IReservedNameStore
    {
        private readonly IReadOnlySet<string> _names = names.ToHashSet(StringComparer.Ordinal);
        public Task<IReadOnlySet<string>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_names);
    }
}
