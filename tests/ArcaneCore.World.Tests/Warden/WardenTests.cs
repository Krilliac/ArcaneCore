using System.Buffers.Binary;
using ArcaneCore.World.Warden;
using Xunit;

namespace ArcaneCore.World.Tests.Warden;

/// <summary>
/// Warden for build 5875 against a simulated client that holds the same RC4 streams (MaNGOS Zero WardenCryptoContext / WardenServer;
/// vmangos WardenScan.cpp scan formats). Expected key bytes come from an independent SHA-1 expansion of the key 00..27.
/// </summary>
public sealed class WardenTests
{
    private static readonly byte[] SessionKey = [.. Enumerable.Range(0, 40).Select(i => (byte)i)];

    private sealed class Client
    {
        public WardenRc4 ToServer;
        public WardenRc4 FromServer;

        public Client()
        {
            WardenCryptoContext.DeriveInitialKeys(SessionKey, out byte[] c, out byte[] s);
            ToServer = new WardenRc4(c);
            FromServer = new WardenRc4(s);
        }

        public byte[] Read(byte[] body)
        {
            byte[] copy = [.. body];
            FromServer.Transform(copy);
            return copy;
        }

        public byte[] Write(params byte[] plain)
        {
            byte[] copy = [.. plain];
            ToServer.Transform(copy);
            return copy;
        }

        public void SwitchToModuleKeys()
        {
            ToServer = new WardenRc4(WardenModuleProfile.ClientKeySeed);
            FromServer = new WardenRc4(WardenModuleProfile.ServerKeySeed);
        }
    }

    private sealed class Rig
    {
        public readonly List<byte[]> Sent = [];
        public readonly List<WardenVerdict> Verdicts = [];
        public readonly Client Client = new();
        public readonly WardenSession Warden;

        public Rig(WardenOptions? options = null)
            => Warden = new WardenSession(SessionKey, options ?? new WardenOptions { Enabled = true }, Sent.Add, Verdicts.Add, new Random(7));

        public byte[] Next() => Client.Read(Sent[^1]);

        public void Handshake()
        {
            Warden.Start();
            Warden.Handle(Client.Write((byte)WardenClientCommand.ModuleOk));
            Warden.Handle(Client.Write([(byte)WardenClientCommand.HashResult, .. WardenModuleProfile.ClientKeySeedHash]));
            Client.SwitchToModuleKeys();
            Next(); // MODULE_INITIALIZE
        }

        /// <summary>Encrypt a CHECK_RESULT with its length and folded checksum.</summary>
        public byte[] Result(params byte[] results)
        {
            byte[] body = new byte[7 + results.Length];
            body[0] = (byte)WardenClientCommand.CheckResult;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), (ushort)results.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(3), WardenCodec.Checksum(results));
            results.CopyTo(body, 7);
            return Client.Write(body);
        }
    }

    [Fact]
    public void TheInitialKeys_FollowTheClassicGenerator()
    {
        WardenCryptoContext.DeriveInitialKeys(SessionKey, out byte[] client, out byte[] server);
        Assert.Equal("E711C90E3921B9725E3CCC8673099955", Convert.ToHexString(client));
        Assert.Equal("96D79BEC6044480918EE080579D8901A", Convert.ToHexString(server));
    }

    [Fact]
    public void TheEmbeddedModule_MatchesItsPinnedDigests()
    {
        byte[]? module = WardenModuleProfile.Module;
        Assert.NotNull(module);
        Assert.Equal(18756, module.Length);
        module[0] ^= 1;
        Assert.False(WardenModuleProfile.Validate(module));
        module[0] ^= 1;
    }

    [Fact]
    public void WardenIsOffByDefault()
    {
        var options = new WardenOptions();
        Assert.False(options.Enabled);
        Assert.Equal(WardenAction.Log, options.Action);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void AMissingModule_IsSentInChunks_ThenTheHashUnlocksTheModuleKeys()
    {
        var rig = new Rig();
        rig.Warden.Start();
        byte[] use = rig.Next();
        Assert.Equal((byte)WardenServerCommand.ModuleUse, use[0]);
        Assert.Equal(WardenModuleProfile.ModuleId, use[1..17]);
        Assert.Equal(WardenModuleProfile.ModuleKey, use[17..33]);
        Assert.Equal(18756u, BinaryPrimitives.ReadUInt32LittleEndian(use.AsSpan(33)));

        int before = rig.Sent.Count;
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.ModuleMissing));
        var module = new List<byte>();
        foreach (byte[] chunk in rig.Sent.Skip(before).Select(rig.Client.Read))
        {
            Assert.Equal((byte)WardenServerCommand.ModuleCache, chunk[0]);
            Assert.Equal(chunk.Length - 3, BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(1)));
            module.AddRange(chunk[3..]);
        }

        Assert.Equal(38, rig.Sent.Count - before); // 18 756 bytes in 500-byte chunks
        Assert.Equal(WardenModuleProfile.Module, module.ToArray());
        Assert.Equal(WardenState.AwaitingTransferResult, rig.Warden.State);

        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.ModuleOk));
        byte[] hash = rig.Next();
        Assert.Equal([(byte)WardenServerCommand.HashRequest, .. WardenModuleProfile.HashSeed], hash);

        rig.Warden.Handle(rig.Client.Write([(byte)WardenClientCommand.HashResult, .. WardenModuleProfile.ClientKeySeedHash]));
        rig.Client.SwitchToModuleKeys();
        byte[] init = rig.Next(); // readable only with the module's server key
        Assert.Equal(WardenState.Ready, rig.Warden.State);
        int offset = 0;
        for (int record = 0; record < 3; record++)
        {
            Assert.Equal((byte)WardenServerCommand.ModuleInitialize, init[offset]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(init.AsSpan(offset + 1));
            uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(init.AsSpan(offset + 3));
            Assert.Equal(WardenCodec.Checksum(init.AsSpan(offset + 7, length)), checksum);
            offset += 7 + length;
        }

        Assert.Equal(init.Length, offset);
        Assert.Empty(rig.Verdicts);
    }

    [Fact]
    public void AWrongHash_IsAProtocolFailure_CappedAtKick()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Ban });
        rig.Warden.Start();
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.ModuleOk));
        rig.Warden.Handle(rig.Client.Write([(byte)WardenClientCommand.HashResult, .. new byte[20]]));

        WardenVerdict verdict = Assert.Single(rig.Verdicts);
        Assert.True(verdict.IsProtocolFailure);
        Assert.Equal(WardenAction.Kick, verdict.Action);
        Assert.Equal(WardenState.Failed, rig.Warden.State);
    }

    [Fact]
    public void ASecondMissingModule_FailsTheLoad()
    {
        var rig = new Rig();
        rig.Warden.Start();
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.ModuleMissing));
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.ModuleMissing));
        Assert.Equal(WardenState.Failed, rig.Warden.State);
    }

    [Fact]
    public void ALateReply_FailsAtTheDeadline()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, ResponseTimeoutSeconds = 30 });
        rig.Warden.Start();
        rig.Warden.Update(29_999);
        Assert.Empty(rig.Verdicts);
        rig.Warden.Update(1);
        Assert.Contains("no reply", Assert.Single(rig.Verdicts).Reason);
    }

    private static WardenOptions Scans(params WardenCheckOptions[] checks) => new()
    {
        Enabled = true,
        Action = WardenAction.Kick,
        ScanIntervalMinSeconds = 10,
        ScanIntervalMaxSeconds = 10,
        ScansPerRequest = 8,
        Checks = [.. checks],
    };

    [Fact]
    public void TheScanRequest_CarriesMemoryPageAndDriverScans_InTheModulesFormat()
    {
        WardenOptions options = Scans(
            new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Memory, Address = 0x00401000, Expected = "90 90" },
            new WardenCheckOptions { Id = 2, Kind = WardenCheckKind.Memory, Module = "kernel32.dll", Address = 0x10, Expected = "AA" },
            new WardenCheckOptions { Id = 3, Kind = WardenCheckKind.PageA, Address = 0x20, Pattern = "DEADBEEF" },
            new WardenCheckOptions { Id = 4, Kind = WardenCheckKind.Driver, DriverName = "evil", DriverPath = "\\Device\\Evil" });
        var rig = new Rig(options);
        rig.Handshake();
        rig.Warden.Update(10_000);
        byte[] request = rig.Next();
        byte x = WardenModuleProfile.XorByte;

        int p = 0;
        Assert.Equal((byte)WardenServerCommand.CheatChecksRequest, request[p++]);
        Assert.Equal("KERNEL32.DLL", ReadString(request, ref p));
        Assert.Equal("evil", ReadString(request, ref p));
        Assert.Equal(0, request[p++]);
        // memory, main image
        Assert.Equal(WardenModuleProfile.OpMemory ^ x, request[p++]);
        Assert.Equal(0, request[p++]);
        Assert.Equal(0x00401000u, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(p))); p += 4;
        Assert.Equal(2, request[p++]);
        // memory, in a module (string 1)
        Assert.Equal(WardenModuleProfile.OpMemory ^ x, request[p++]);
        Assert.Equal(1, request[p++]);
        p += 5;
        // page A: seed, HMAC-SHA1(seed, pattern), offset, length
        Assert.Equal(WardenModuleProfile.OpPageA ^ x, request[p++]);
        uint seed = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(p)); p += 4;
        Assert.Equal(WardenCheck.Hmac(seed, Convert.FromHexString("DEADBEEF")), request[p..(p + 20)]); p += 20;
        Assert.Equal(0x20u, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(p))); p += 4;
        Assert.Equal(4, request[p++]);
        // driver: seed, HMAC-SHA1(seed, path), string 2
        Assert.Equal(WardenModuleProfile.OpDriver ^ x, request[p++]);
        uint driverSeed = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(p)); p += 4;
        Assert.Equal(WardenCheck.Hmac(driverSeed, "\\Device\\Evil"u8), request[p..(p + 20)]); p += 20;
        Assert.Equal(2, request[p++]);
        Assert.Equal(x, request[p++]);
        Assert.Equal(request.Length, p);
    }

    [Fact]
    public void AMatchingReply_PassesAndSchedulesTheNextScan_AMismatchCallsTheConfiguredAction()
    {
        WardenOptions options = Scans(
            new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Memory, Address = 0x00401000, Expected = "9090" },
            new WardenCheckOptions { Id = 2, Kind = WardenCheckKind.PageB, Pattern = "CC", Wanted = false },
            new WardenCheckOptions { Id = 3, Kind = WardenCheckKind.Driver, DriverName = "d", DriverPath = "p", Wanted = true, Action = WardenAction.Ban });
        var rig = new Rig(options);
        rig.Handshake();

        rig.Warden.Update(10_000);
        rig.Next();
        rig.Warden.Handle(rig.Result(0, 0x90, 0x90, 0x00, WardenModuleProfile.Found));
        Assert.Empty(rig.Verdicts);
        Assert.Equal(WardenState.Ready, rig.Warden.State);

        rig.Warden.Update(10_000);
        rig.Next();
        rig.Warden.Handle(rig.Result(0, 0x90, 0x91, WardenModuleProfile.Found, 0x00));
        Assert.Equal([1u, 2u, 3u], rig.Verdicts.Select(v => v.CheckId!.Value));
        Assert.Equal([WardenAction.Kick, WardenAction.Kick, WardenAction.Ban], rig.Verdicts.Select(v => v.Action));
        Assert.All(rig.Verdicts, v => Assert.False(v.IsProtocolFailure));
    }

    [Fact]
    public void ABadChecksum_IsAProtocolFailure()
    {
        var rig = new Rig(Scans(new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Timing }));
        rig.Handshake();
        rig.Warden.Update(10_000);
        rig.Next();
        byte[] body = [(byte)WardenClientCommand.CheckResult, 5, 0, 1, 2, 3, 4, 1, 0, 0, 0, 0];
        rig.Warden.Handle(rig.Client.Write(body));
        Assert.True(Assert.Single(rig.Verdicts).IsProtocolFailure);
    }

    [Fact]
    public void InvalidChecks_AreReported()
    {
        WardenOptions options = Scans(
            new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Memory, Expected = "zz" },
            new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Driver });
        Assert.Equal(3, options.Validate().Count);
    }

    private static string ReadString(byte[] data, ref int p)
    {
        int length = data[p++];
        string value = System.Text.Encoding.ASCII.GetString(data, p, length);
        p += length;
        return value;
    }
}
