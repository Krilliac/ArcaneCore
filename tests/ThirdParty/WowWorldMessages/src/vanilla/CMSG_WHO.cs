using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_WHO: VanillaClientMessage, IWorldMessage {
    public required uint MinimumLevel { get; set; }
    public required uint MaximumLevel { get; set; }
    public required string PlayerName { get; set; }
    public required string GuildName { get; set; }
    public required uint RaceMask { get; set; }
    public required uint ClassMask { get; set; }
    public required List<uint> Zones { get; set; }
    public required List<string> SearchStrings { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(MinimumLevel, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(MaximumLevel, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(PlayerName, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(GuildName, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(RaceMask, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ClassMask, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Zones.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Zones) {
            await w.WriteUInt(v, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteUInt((uint)SearchStrings.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in SearchStrings) {
            await w.WriteCString(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 98, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 98, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_WHO> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var minimumLevel = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var maximumLevel = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var playerName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var guildName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var raceMask = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var classMask = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfZones = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var zones = new List<uint>();
        for (var i = 0; i < amountOfZones; ++i) {
            zones.Add(await r.ReadUInt(cancellationToken).ConfigureAwait(false));
        }

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfStrings = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var searchStrings = new List<string>();
        for (var i = 0; i < amountOfStrings; ++i) {
            searchStrings.Add(await r.ReadCString(cancellationToken).ConfigureAwait(false));
        }

        return new CMSG_WHO {
            MinimumLevel = minimumLevel,
            MaximumLevel = maximumLevel,
            PlayerName = playerName,
            GuildName = guildName,
            RaceMask = raceMask,
            ClassMask = classMask,
            Zones = zones,
            SearchStrings = searchStrings,
        };
    }

    internal int Size() {
        var size = 0;

        // minimum_level: Generator.Generated.DataTypeLevel32
        size += 4;

        // maximum_level: Generator.Generated.DataTypeLevel32
        size += 4;

        // player_name: Generator.Generated.DataTypeCstring
        size += PlayerName.Length + 1;

        // guild_name: Generator.Generated.DataTypeCstring
        size += GuildName.Length + 1;

        // race_mask: Generator.Generated.DataTypeInteger
        size += 4;

        // class_mask: Generator.Generated.DataTypeInteger
        size += 4;

        // amount_of_zones: Generator.Generated.DataTypeInteger
        size += 4;

        // zones: Generator.Generated.DataTypeArray
        size += Zones.Sum(e => 4);

        // amount_of_strings: Generator.Generated.DataTypeInteger
        size += 4;

        // search_strings: Generator.Generated.DataTypeArray
        size += SearchStrings.Sum(e => e.Length + 1);

        return size;
    }

}

