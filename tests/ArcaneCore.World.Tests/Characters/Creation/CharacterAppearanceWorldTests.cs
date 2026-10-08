using System.Buffers.Binary;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters.Creation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Characters.Creation;

/// <summary>
/// CMSG_CHAR_CREATE over loopback with <c>CharacterCreation:CharSectionsDbcPath</c> and <c>CharacterFacialHairStylesDbcPath</c>: the files
/// load at startup into <see cref="CharacterCreationFeature.Appearance"/>, and a create whose looks the 1.12.1 client could not have offered
/// is answered CHAR_CREATE_FAILED (vmangos CharacterHandler.cpp:239-244).
/// </summary>
public sealed class CharacterAppearanceWorldTests
{
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

    /// <summary>Human male: skin 0, face 0, hair 0 and facial hair 0 in colour 0, beard style 0 (CharSections "diiiiixxxi").</summary>
    private static (string Sections, string Beards) WriteHumanMale()
    {
        string sections = Path.Combine(Path.GetTempPath(), $"charsections-{Guid.NewGuid():N}.dbc");
        string beards = Path.Combine(Path.GetTempPath(), $"facialhair-{Guid.NewGuid():N}.dbc");
        File.WriteAllBytes(sections, Image(10,
            [1, 1, 0, 0, 0, 0, 0, 0, 0, 0], [2, 1, 0, 1, 0, 0, 0, 0, 0, 0], [3, 1, 0, 3, 0, 0, 0, 0, 0, 0], [4, 1, 0, 2, 0, 0, 0, 0, 0, 0]));
        File.WriteAllBytes(beards, Image(9, [1, 0, 0, 0, 0, 0, 0, 0, 0]));
        return (sections, beards);
    }

    internal static byte[] Create(string name, byte race, byte cls, byte gender, CharacterAppearance looks)
    {
        var create = new PacketWriter(32);
        create.WriteCString(name);
        create.WriteByte(race);
        create.WriteByte(cls);
        create.WriteByte(gender);
        create.WriteByte(looks.Skin);
        create.WriteByte(looks.Face);
        create.WriteByte(looks.HairStyle);
        create.WriteByte(looks.HairColor);
        create.WriteByte(looks.FacialHair);
        create.WriteByte(0); // outfit
        return create.ToArray();
    }

    internal static async Task<byte> TryCreateAsync(WorldTestClient client, string name, CharacterAppearance looks, byte race = 1, byte gender = 0)
    {
        await client.SendAsync(WorldOpcode.CmsgCharCreate, Create(name, race, 1, gender, looks));
        return (await client.ReadUntilAsync(WorldOpcode.SmsgCharCreate))[0];
    }

    private static Action<IServiceCollection> Configure(Dictionary<string, string?> values, LogCapture? logs = null) => services =>
    {
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        logs?.Register(services);
    };

    [Fact]
    public async Task ConfiguredFiles_RefuseLooksTheClientCannotOffer_AndAcceptTheDefaultLooks()
    {
        (string sections, string beards) = WriteHumanMale();
        var logs = new LogCapture();
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: Configure(new()
            {
                ["CharacterCreation:CharSectionsDbcPath"] = sections,
                ["CharacterCreation:CharacterFacialHairStylesDbcPath"] = beards,
            }, logs));
            CharacterAppearanceCatalog appearance = host.WorldServices.GetRequiredService<CharacterCreationFeature>().Appearance;
            Assert.Equal((4, 1), (appearance.SectionCount, appearance.FacialHairStyleCount));
            Assert.DoesNotContain(logs.Lines, line => line.Contains("CharSections", StringComparison.Ordinal) && line.Contains("not enforced", StringComparison.Ordinal));
            Assert.Contains(logs.Lines, line => line.Contains("appearance is checked", StringComparison.Ordinal));

            byte[] key = await host.AddAccountAsync("LOOKS");
            await using WorldTestClient client = await host.ConnectAsync();
            await client.AuthenticateAsync("LOOKS", key);

            Assert.Equal((byte)CharResult.CharCreateFailed, await TryCreateAsync(client, "Blueskin", new CharacterAppearance(7, 0, 0, 0, 0)));
            Assert.Equal((byte)CharResult.CharCreateFailed, await TryCreateAsync(client, "Bighair", new CharacterAppearance(0, 0, 9, 0, 0)));
            Assert.Equal((byte)CharResult.CharCreateFailed, await TryCreateAsync(client, "Longbeard", new CharacterAppearance(0, 0, 0, 0, 4)));
            Assert.Equal((byte)CharResult.CharCreateSuccess, await TryCreateAsync(client, "Plainlooks", new CharacterAppearance(0, 0, 0, 0, 0)));
        }
        finally
        {
            File.Delete(sections);
            File.Delete(beards);
        }
    }

    [Fact]
    public async Task WithoutFiles_AppearanceIsNotChecked_AndTheLogSaysSo()
    {
        var logs = new LogCapture();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: Configure([], logs));
        Assert.True(host.WorldServices.GetRequiredService<CharacterCreationFeature>().Appearance.IsEmpty);
        Assert.Contains(logs.Lines, line => line.Contains("CharacterCreation:CharSectionsDbcPath", StringComparison.Ordinal));

        byte[] key = await host.AddAccountAsync("NOLOOKS");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("NOLOOKS", key);
        Assert.Equal((byte)CharResult.CharCreateSuccess, await TryCreateAsync(client, "Anylooks", new CharacterAppearance(7, 7, 9, 9, 4)));
    }

    [Fact]
    public void OnlyOneOfTheTwoFiles_RefusesStartup()
    {
        (string sections, string beards) = WriteHumanMale();
        try
        {
            Assert.ThrowsAny<Exception>(() => WorldTestHost.Start(configureServices: Configure(new() { ["CharacterCreation:CharSectionsDbcPath"] = sections })));
            Assert.ThrowsAny<Exception>(() => WorldTestHost.Start(configureServices: Configure(new() { ["CharacterCreation:CharacterFacialHairStylesDbcPath"] = beards })));
        }
        finally
        {
            File.Delete(sections);
            File.Delete(beards);
        }
    }

    [Fact]
    public void AConfiguredFileThatIsMissing_RefusesStartup()
        => Assert.ThrowsAny<Exception>(() => WorldTestHost.Start(configureServices: Configure(new()
        {
            ["CharacterCreation:CharSectionsDbcPath"] = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dbc"),
            ["CharacterCreation:CharacterFacialHairStylesDbcPath"] = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dbc"),
        })));
}

/// <summary>Collects the formatted log lines of every feature logger of a <see cref="WorldTestHost"/> (the host's default loggers are null).</summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<ILoggerFactory>(new LoggerFactory([this]));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
    }

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Sink(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._lines)
            {
                owner._lines.Add($"{logLevel} {category}: {formatter(state, exception)}");
            }
        }
    }
}
