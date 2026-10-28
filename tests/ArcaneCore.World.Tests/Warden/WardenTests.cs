using System.Buffers.Binary;
using ArcaneCore.Data.World.Warden;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Ops.Announce;
using ArcaneCore.World.Tests.HotCode;
using ArcaneCore.World.Tests.Ops;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Warden;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

        public Client(byte[]? sessionKey = null)
        {
            WardenCryptoContext.DeriveInitialKeys(sessionKey ?? SessionKey, out byte[] c, out byte[] s);
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

    /// <summary>
    /// A seeded Random that pins only the rotation start to the first check, so a test reads the checks in table order.
    /// The rotation start is the first <c>Next(int)</c> call, made in the WardenSession constructor. Every later call goes to the
    /// seeded base. That matters because a derived Random routes <c>NextInt64</c> through <c>Next(int)</c>, so pinning every call
    /// would also pin the scan gap and the first-scan offset. (The scan seeds come from the cryptographic generator, not this Random.)
    /// </summary>
    private sealed class RotationStartsAtFirst(int seed) : Random(seed)
    {
        private bool _rotationDrawn;

        public override int Next(int maxValue)
        {
            if (_rotationDrawn)
            {
                return base.Next(maxValue);
            }

            _rotationDrawn = true;
            return 0;
        }
    }

    private sealed class Rig
    {
        public readonly List<byte[]> Sent = [];
        public readonly List<WardenVerdict> Verdicts = [];
        public readonly Client Client = new();
        public readonly WardenSession Warden;

        public Rig(WardenOptions? options = null, Random? random = null)
            => Warden = new WardenSession(SessionKey, options ?? new WardenOptions { Enabled = true }, Sent.Add, Verdicts.Add, random ?? new RotationStartsAtFirst(7));

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
        public byte[] Result(params byte[] results) => Client.Write(CheckResultBody(results));
    }

    /// <summary>A plain CHECK_RESULT with its length and folded checksum.</summary>
    private static byte[] CheckResultBody(params byte[] results)
    {
        byte[] body = new byte[7 + results.Length];
        body[0] = (byte)WardenClientCommand.CheckResult;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), (ushort)results.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(3), WardenCodec.Checksum(results));
        results.CopyTo(body, 7);
        return body;
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
    public void AWrongHash_CapsTheConfiguredBanActionAtKick()
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
    public void AWrongHash_UnderLog_RetriesTheChallengeAndReachesScans()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Log,
            ScanIntervalMinSeconds = 10, ScanIntervalMaxSeconds = 10 });
        rig.Warden.Start();
        rig.Next(); // MODULE_USE
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.ModuleOk));
        rig.Next(); // first HASH_REQUEST
        rig.Warden.Handle(rig.Client.Write([(byte)WardenClientCommand.HashResult, .. new byte[20]]));

        Assert.Equal(WardenAction.Log, Assert.Single(rig.Verdicts).Action);
        Assert.Equal(WardenState.AwaitingHash, rig.Warden.State);
        Assert.Equal(2, rig.Sent.Count); // keep the in-flight challenge; no RC4-desynchronizing resend
        rig.Warden.Handle(rig.Client.Write([(byte)WardenClientCommand.HashResult, .. WardenModuleProfile.ClientKeySeedHash]));
        rig.Client.SwitchToModuleKeys();
        Assert.Equal((byte)WardenServerCommand.ModuleInitialize, rig.Next()[0]);
        rig.Warden.Update(10_000);
        Assert.Equal((byte)WardenServerCommand.CheatChecksRequest, rig.Next()[0]);
    }

    [Fact]
    public void RepeatedMissingModule_UnderLog_StopsAfterTheBoundedAttempts()
    {
        var rig = new Rig();
        rig.Warden.Start();
        rig.Next(); // consume MODULE_USE before reading the streamed chunks
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.ModuleMissing));
        foreach (byte[] chunk in rig.Sent.Skip(1).ToArray())
        {
            rig.Client.Read(chunk);
        }

        for (int i = 0; i < 4 && rig.Warden.State != WardenState.Failed; i++)
        {
            rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.ModuleMissing));
        }

        Assert.Equal(WardenState.Failed, rig.Warden.State);
        Assert.Equal(WardenAction.Kick, rig.Verdicts[^1].Action);
        Assert.True(rig.Sent.Count <= 40, "repeated misses must not cause one MODULE_USE per reply");
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
    public void TheScanSeeds_DoNotFollowTheSessionsRandom()
    {
        // Two sessions on identically seeded Randoms: everything Random drives (rotation, gaps) matches, but the page scan's seed must
        // not, or the seeds already sent would predict the next ones. One 32-bit collision in 1 in 2^32 is the only false failure.
        WardenOptions options = Scans(new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.PageA, Address = 0x20, Pattern = "DEADBEEF" });
        uint SeedOf(Rig rig)
        {
            rig.Handshake();
            rig.Warden.Update(10_000);
            byte[] request = rig.Next();
            Assert.Equal((byte)WardenServerCommand.CheatChecksRequest, request[0]);
            Assert.Equal(0, request[1]);
            Assert.Equal(WardenModuleProfile.OpPageA ^ WardenModuleProfile.XorByte, request[2]);
            return BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(3));
        }

        Assert.NotEqual(SeedOf(new Rig(options, new Random(7))), SeedOf(new Rig(options, new Random(7))));
    }

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

    /// <summary>
    /// vmangos type 2 is FIND_MEM_IMAGE_CODE_BY_HASH (executable images only) and type 3 is FIND_CODE_BY_HASH (every committed
    /// page). Trinity's table for this module names those the other way around: 0xBF (PAGE_CHECK_B) is the MZ+PE image scan and
    /// 0xB2 (PAGE_CHECK_A) is every page. A type-2 row must not go out as the all-pages opcode.
    /// </summary>
    [Fact]
    public void AnImageScan_UsesTheMzPeOpcode_AndAnAllPagesScan_UsesTheOther()
    {
        WardenCheckRow[] rows =
        [
            new() { Id = 2, Type = 2, Data = "AA", Address = 1, Result = "0", BuildMin = 5875, BuildMax = 5875 },
            new() { Id = 3, Type = 3, Data = "BB", Address = 2, Result = "1", BuildMin = 5875, BuildMax = 5875 },
        ];
        List<WardenCheckOptions> checks = WardenCheckRows.Merge(rows, [], 5875);
        Assert.Equal([WardenCheckKind.PageA, WardenCheckKind.PageB], checks.Select(c => c.Kind));

        var rig = new Rig(Scans([.. checks]));
        rig.Handshake();
        rig.Warden.Update(10_000);
        byte[] request = rig.Next();
        byte x = WardenModuleProfile.XorByte;

        // No string table. Each page scan is opcode, seed, HMAC-SHA1, address, length (30 bytes).
        Assert.Equal(63, request.Length);
        Assert.Equal((byte)WardenServerCommand.CheatChecksRequest, request[0]);
        Assert.Equal(0, request[1]);
        Assert.Equal((byte)(0xBF ^ x), request[2]);
        Assert.Equal((byte)(0xB2 ^ x), request[32]);
        Assert.Equal(x, request[^1]);
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
    public void AnUnsolicitedReply_UnderLog_DoesNotStopFutureScans()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Log,
            ScanIntervalMinSeconds = 10, ScanIntervalMaxSeconds = 10 });
        rig.Handshake();
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult));

        Assert.Equal(WardenAction.Log, Assert.Single(rig.Verdicts).Action);
        Assert.Equal(WardenState.Ready, rig.Warden.State);
        rig.Warden.Update(10_000);
        Assert.Equal((byte)WardenServerCommand.CheatChecksRequest, rig.Next()[0]);
        rig.Warden.Handle(rig.Result(1, 0, 0, 0, 0));
        Assert.Equal(WardenState.Ready, rig.Warden.State);
        Assert.Single(rig.Verdicts);
    }

    [Fact]
    public void RepeatedUnsolicitedReplies_UnderLog_DoNotPostponeTheScheduledScan()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Log,
            ScanIntervalMinSeconds = 10, ScanIntervalMaxSeconds = 10 });
        rig.Handshake();
        rig.Warden.Update(10_000); // the first scan is at a random offset, at most one gap; answer it so the next gap is the full 10 s
        rig.Next();
        rig.Warden.Handle(rig.Result(0));
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult));
        rig.Warden.Update(9_000);
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult));

        rig.Warden.Update(1_000);
        Assert.Equal(WardenState.AwaitingCheckResult, rig.Warden.State);
        Assert.Equal((byte)WardenServerCommand.CheatChecksRequest, rig.Next()[0]);
        Assert.Equal(2, rig.Verdicts.Count);
    }

    [Fact]
    public void RepeatedUnsolicitedReplies_UnderLog_AggregateVerdictsWithoutSendingPackets()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Log,
            ScanIntervalMinSeconds = 120, ScanIntervalMaxSeconds = 120 });
        rig.Handshake();
        int sent = rig.Sent.Count;
        for (int i = 0; i < 100; i++)
        {
            rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult));
        }

        Assert.Equal(sent, rig.Sent.Count);
        Assert.Single(rig.Verdicts);
        rig.Warden.Update(60_000);
        Assert.Equal(2, rig.Verdicts.Count);
        Assert.Contains("99", rig.Verdicts[^1].Reason);
    }

    [Fact]
    public void AMalformedScanReply_UnderLog_DoesNotStopFutureScans()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Log,
            ScanIntervalMinSeconds = 10, ScanIntervalMaxSeconds = 10 });
        rig.Handshake();
        rig.Warden.Update(10_000);
        Assert.Equal((byte)WardenServerCommand.CheatChecksRequest, rig.Next()[0]);
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult));

        Assert.Equal(WardenAction.Log, Assert.Single(rig.Verdicts).Action);
        Assert.Equal(WardenState.Ready, rig.Warden.State);
        rig.Warden.Update(10_000);
        Assert.Equal((byte)WardenServerCommand.CheatChecksRequest, rig.Next()[0]);
        rig.Warden.Handle(rig.Result(1, 0, 0, 0, 0));
        Assert.Equal(WardenState.Ready, rig.Warden.State);
        Assert.Single(rig.Verdicts);
    }

    [Fact]
    public void AMalformedScanReply_UnderLog_RetriesTheSameCheck()
    {
        WardenOptions options = Scans(
            new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Timing },
            new WardenCheckOptions { Id = 2, Kind = WardenCheckKind.Memory, Address = 0x00401000, Expected = "90" });
        options.ProtocolAction = WardenAction.Log;
        options.ScansPerRequest = 1;
        var rig = new Rig(options);
        rig.Handshake();
        rig.Warden.Update(10_000);
        byte[] first = rig.Next();
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult));

        rig.Warden.Update(10_000);
        Assert.Equal(first, rig.Next());
        rig.Warden.Handle(rig.Result(1, 0, 0, 0, 0));
        Assert.Equal(WardenState.Ready, rig.Warden.State);
        Assert.Single(rig.Verdicts);
    }

    [Fact]
    public void RepeatedMalformedScanReplies_UnderLog_EndInKick()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Log,
            ScanIntervalMinSeconds = 10, ScanIntervalMaxSeconds = 10 });
        rig.Handshake();
        for (int i = 0; i < 3 && rig.Warden.State != WardenState.Failed; i++)
        {
            rig.Warden.Update(10_000);
            Assert.Equal((byte)WardenServerCommand.CheatChecksRequest, rig.Next()[0]);
            rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult));
        }

        Assert.Equal(WardenState.Failed, rig.Warden.State);
        Assert.Equal(WardenAction.Kick, rig.Verdicts[^1].Action);
    }

    [Fact]
    public void RepeatedScanTimeouts_UnderLog_EndInKick()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Log,
            ResponseTimeoutSeconds = 2, ScanIntervalMinSeconds = 10, ScanIntervalMaxSeconds = 10 });
        rig.Handshake();
        for (int i = 0; i < 3 && rig.Warden.State != WardenState.Failed; i++)
        {
            rig.Warden.Update(10_000);
            Assert.Equal((byte)WardenServerCommand.CheatChecksRequest, rig.Next()[0]);
            rig.Warden.Update(2_000);
        }

        Assert.Equal(WardenState.Failed, rig.Warden.State);
        Assert.Equal(WardenAction.Kick, rig.Verdicts[^1].Action);
    }

    [Fact]
    public void AFailedCheckBatch_IsRetriedOnce_AndAFailedRetryKicks()
    {
        WardenOptions options = Scans(
            new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Timing },
            new WardenCheckOptions { Id = 2, Kind = WardenCheckKind.Memory, Address = 0x00401000, Expected = "90" });
        options.ProtocolAction = WardenAction.Log;
        options.ScansPerRequest = 1;
        options.MaxProtocolFailures = 100;
        var rig = new Rig(options);
        rig.Handshake();
        rig.Warden.Update(10_000);
        byte[] first = rig.Next();
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult));
        rig.Warden.Update(10_000);
        Assert.Equal(first, rig.Next());
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult));

        Assert.Equal(WardenState.Failed, rig.Warden.State);
        Assert.Equal(WardenAction.Kick, rig.Verdicts[^1].Action);
    }

    [Fact]
    public void AClientCorruptingEveryReplyToOneCheck_IsKickedBeforeThatCheckCanBeSkipped()
    {
        // The target check's request carries its address; every other batch is answered validly, so strikes are forgiven
        // between the targeted batches ("corrupt, corrupt, valid, valid" per cycle).
        WardenOptions options = Scans(
            new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Timing },
            new WardenCheckOptions { Id = 2, Kind = WardenCheckKind.Memory, Address = 0x00402000, Expected = "90" },
            new WardenCheckOptions { Id = 3, Kind = WardenCheckKind.Timing });
        options.ProtocolAction = WardenAction.Log;
        options.ScansPerRequest = 1;
        var rig = new Rig(options);
        rig.Handshake();
        byte[] target = [0x00, 0x20, 0x40, 0x00];
        int targeted = 0;
        for (int i = 0; i < 60 && rig.Warden.State != WardenState.Failed; i++)
        {
            rig.Warden.Update(10_000);
            byte[] request = rig.Next();
            if (request.AsSpan().IndexOf(target) >= 0)
            {
                targeted++;
                rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult));
            }
            else
            {
                rig.Warden.Handle(rig.Result(1, 0, 0, 0, 0));
            }
        }

        Assert.True(targeted >= 2, "the targeted check was requested");
        Assert.Equal(WardenState.Failed, rig.Warden.State);
        Assert.Equal(WardenAction.Kick, rig.Verdicts[^1].Action);
        Assert.DoesNotContain(rig.Verdicts, v => v.CheckId == 2);
    }

    [Fact]
    public void AValidReply_ForgivesAStrike()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Log, MaxProtocolFailures = 2,
            ScanIntervalMinSeconds = 10, ScanIntervalMaxSeconds = 10 });
        rig.Handshake();
        for (int i = 0; i < 5; i++)
        {
            rig.Warden.Update(10_000);
            rig.Next();
            rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult));
            rig.Warden.Update(10_000);
            rig.Next();
            rig.Warden.Handle(rig.Result(1, 0, 0, 0, 0));
        }

        Assert.Equal(WardenState.Ready, rig.Warden.State);
        Assert.All(rig.Verdicts, v => Assert.Equal(WardenAction.Log, v.Action));
    }

    [Fact]
    public void RepeatedModuleStatusTimeouts_UnderLog_KickWithoutResending()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Log, ResponseTimeoutSeconds = 30 });
        rig.Warden.Start();
        for (int i = 0; i < 3; i++)
        {
            rig.Warden.Update(30_000);
        }

        Assert.Equal(WardenState.Failed, rig.Warden.State);
        Assert.Equal(WardenAction.Kick, rig.Verdicts[^1].Action);
        Assert.Single(rig.Sent); // the MODULE_USE, never re-sent
    }

    [Fact]
    public void RepeatedHashTimeouts_UnderLog_KickWithoutResending()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Log, ResponseTimeoutSeconds = 30 });
        rig.Warden.Start();
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.ModuleOk));
        for (int i = 0; i < 3; i++)
        {
            rig.Warden.Update(30_000);
        }

        Assert.Equal(WardenState.Failed, rig.Warden.State);
        Assert.Equal(WardenAction.Kick, rig.Verdicts[^1].Action);
        Assert.Equal(2, rig.Sent.Count); // MODULE_USE and one HASH_REQUEST
    }

    [Fact]
    public void HeldVerdicts_AreReportedBeforeTheKick()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Log, ProtocolReportIntervalSeconds = 3600,
            ScanIntervalMinSeconds = 10, ScanIntervalMaxSeconds = 10 });
        rig.Handshake();
        for (int i = 0; i < 5; i++)
        {
            rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult)); // unsolicited: 1 reported, 4 held
        }

        rig.Warden.Update(10_000);
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult)); // malformed scan reply: held (5)
        rig.Warden.Update(10_000);
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult)); // malformed retry: kick

        Assert.Equal(3, rig.Verdicts.Count);
        Assert.StartsWith("5 more", rig.Verdicts[1].Reason);
        Assert.Equal(WardenAction.Kick, rig.Verdicts[2].Action);
    }

    [Fact]
    public void Flush_ReportsTheHeldCount()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Log, ProtocolReportIntervalSeconds = 3600 });
        rig.Handshake();
        for (int i = 0; i < 10; i++)
        {
            rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult));
        }

        Assert.Equal(9, rig.Warden.HeldVerdicts);
        rig.Warden.Flush();
        Assert.Equal(2, rig.Verdicts.Count);
        Assert.StartsWith("9 more", rig.Verdicts[1].Reason);
        rig.Warden.Flush();
        Assert.Equal(2, rig.Verdicts.Count);
    }

    [Fact]
    public void AValidScanReply_ReportsTheHeldCountAheadOfItsOwnVerdicts()
    {
        WardenOptions options = Scans(new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Memory, Address = 0x00401000, Expected = "9090" });
        options.ProtocolAction = WardenAction.Log;
        options.ProtocolReportIntervalSeconds = 3600;
        var rig = new Rig(options);
        rig.Handshake();
        for (int i = 0; i < 4; i++)
        {
            rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult)); // unsolicited: 1 reported, 3 held
        }

        Assert.Equal(3, rig.Warden.HeldVerdicts);
        rig.Warden.Update(10_000);
        rig.Next();
        rig.Warden.Handle(rig.Result(0, 0x90, 0x91)); // valid reply, but the scan failed

        Assert.Equal(0, rig.Warden.HeldVerdicts);
        Assert.Equal(3, rig.Verdicts.Count);
        Assert.StartsWith("3 more", rig.Verdicts[1].Reason);
        Assert.False(rig.Verdicts[2].IsProtocolFailure);
    }

    [Fact]
    public void TheFirstScan_StartsAtARandomOffset()
    {
        var firstScanSeconds = new HashSet<int>();
        for (int seed = 1; seed <= 24; seed++)
        {
            var sent = new List<byte[]>();
            var session = new WardenSession(SessionKey, Scans(new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Timing }), sent.Add, _ => { }, new Random(seed));
            var client = new Client();
            session.Start();
            session.Handle(client.Write((byte)WardenClientCommand.ModuleOk));
            session.Handle(client.Write([(byte)WardenClientCommand.HashResult, .. WardenModuleProfile.ClientKeySeedHash]));
            int ready = sent.Count;
            int second = 0;
            while (sent.Count == ready && second < 20)
            {
                session.Update(1_000);
                second++;
            }

            Assert.InRange(second, 1, 10);
            firstScanSeconds.Add(second);
            Assert.Equal(WardenState.AwaitingCheckResult, session.State);
        }

        Assert.True(firstScanSeconds.Count > 3, $"first scans were not spread: {string.Join(",", firstScanSeconds)}");
    }

    [Fact]
    public void TheScansAfterTheFirst_KeepTheFullConfiguredGap()
    {
        var rig = new Rig(Scans(new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Timing }));
        rig.Handshake();
        rig.Warden.Update(10_000);
        rig.Next();
        rig.Warden.Handle(rig.Result(1, 0, 0, 0, 0)); // a valid Timing reply, so the gap comes from the scheduled scan, not the retry path
        Assert.All(rig.Verdicts, v => Assert.False(v.IsProtocolFailure));
        int sent = rig.Sent.Count;
        rig.Warden.Update(9_999);
        Assert.Equal(sent, rig.Sent.Count);
        rig.Warden.Update(1);
        Assert.Equal(sent + 1, rig.Sent.Count);
    }

    [Fact]
    public void TheScanRotation_StartsAtADifferentCheckPerSession_AndStillWrapsInOrder()
    {
        const int Count = 6;
        WardenCheckOptions[] checks = [.. Enumerable.Range(0, Count).Select(i => new WardenCheckOptions
            { Id = (uint)(i + 1), Kind = WardenCheckKind.Memory, Address = (uint)(0x00401000 + i), Expected = "90" })];
        var starts = new HashSet<int>();
        for (int seed = 1; seed <= 24; seed++)
        {
            WardenOptions options = Scans(checks);
            options.ScansPerRequest = 1;
            var sent = new List<byte[]>();
            var session = new WardenSession(SessionKey, options, sent.Add, _ => { }, new Random(seed));
            var client = new Client();
            session.Start();
            session.Handle(client.Write((byte)WardenClientCommand.ModuleOk));
            session.Handle(client.Write([(byte)WardenClientCommand.HashResult, .. WardenModuleProfile.ClientKeySeedHash]));
            client.SwitchToModuleKeys();
            client.Read(sent[^1]); // MODULE_INITIALIZE
            int first = -1;
            for (int scan = 0; scan < Count + 1; scan++)
            {
                session.Update(10_000);
                // No string table: command, 0, opcode, module index 0, then the u32 address.
                int index = (int)BinaryPrimitives.ReadUInt32LittleEndian(client.Read(sent[^1]).AsSpan(4)) - 0x00401000;
                first = scan == 0 ? index : first;
                Assert.Equal((first + scan) % Count, index);
                session.Handle(client.Write(CheckResultBody(0, 0x90)));
            }

            starts.Add(first);
        }

        Assert.True(starts.Count > 2, $"rotations did not start at different checks: {string.Join(",", starts)}");
    }

    [Fact]
    public void ARetriedBatch_RunsTheSameCheckAgain_FromARandomStart()
    {
        // Real seeded starts, not the pinned one: the retry rewinds the rotation from wherever it began.
        var firstChecks = new HashSet<uint>();
        for (int seed = 1; seed <= 16; seed++)
        {
            WardenOptions options = Scans([.. Enumerable.Range(0, 5).Select(i => new WardenCheckOptions
                { Id = (uint)(i + 1), Kind = WardenCheckKind.Memory, Address = (uint)(0x00401000 + i), Expected = "90" })]);
            options.ScansPerRequest = 1;
            options.ProtocolAction = WardenAction.Log;
            var rig = new Rig(options, new Random(seed));
            rig.Handshake();
            rig.Warden.Update(10_000);
            uint first = BinaryPrimitives.ReadUInt32LittleEndian(rig.Next().AsSpan(4));
            firstChecks.Add(first);
            rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult)); // malformed: the batch is retried
            rig.Warden.Update(10_000);
            Assert.Equal(first, BinaryPrimitives.ReadUInt32LittleEndian(rig.Next().AsSpan(4)));
        }

        Assert.True(firstChecks.Count > 2, $"the retried batches did not start at different checks: {string.Join(",", firstChecks)}");
    }

    [Fact]
    public async Task APacketAfterTheSessionClosed_StillReportsTheHeldCount()
    {
        // A tick interval far beyond the test keeps the world tick from removing the closed session first: the packet below is the
        // one that raced the tick's flush.
        await using var host = WorldTestHost.Start(configure: o => o.TickIntervalMs = 600_000, configureServices: services => services.AddSingleton<IOptions<WardenOptions>>(
            Options.Create(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Log, LoadFromDatabase = false,
                MaxProtocolFailures = 100, ProtocolReportIntervalSeconds = 3600 })));
        WardenFeature feature = host.WorldServices.GetRequiredService<WardenFeature>();
        var verdicts = new System.Collections.Concurrent.ConcurrentQueue<WardenVerdict>();
        feature.Verdict += (_, verdict) => verdicts.Enqueue(verdict);
        byte[] key = await host.AddAccountAsync("wardenlate");
        WorldTestClient connection = await host.ConnectAsync();
        await connection.AuthenticateAsync("wardenlate", key);
        WorldSession session = Assert.Single(host.Registry.Sessions);
        var warden = new Client(key);
        await connection.SendAsync(WorldOpcode.CmsgWardenData, warden.Write(0xFF)); // reported at once
        await WorldTestHost.WaitForAsync(() => feature.Find(session) is not null, "Warden attached to the session");
        WardenSession scanner = feature.Find(session)!;
        await WorldTestHost.WaitForAsync(() => !verdicts.IsEmpty, "the first failure to be reported");
        await connection.DisposeAsync();
        await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "the session to close");

        feature.Handle(session, warden.Write(0xFF)); // throttled: held, then reported because the session is closed

        Assert.Equal(0, scanner.HeldVerdicts);
        Assert.Contains(verdicts, v => v.Reason.StartsWith("1 more", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 10u, false)]
    [InlineData(101, 10u, false)]
    [InlineData(1, 0u, false)]
    [InlineData(1, 3601u, false)]
    [InlineData(1, 1u, true)]
    [InlineData(100, 3600u, true)]
    public void TheProtocolLimits_AreValidated(int maxFailures, uint intervalSeconds, bool valid)
    {
        var options = new WardenOptions { MaxProtocolFailures = maxFailures, ProtocolReportIntervalSeconds = intervalSeconds };
        Assert.Equal(valid, options.Validate().Count == 0);
    }

    [Fact]
    public void AnUnsolicitedReply_UnderKick_RequiresDisconnect()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Kick });
        rig.Handshake();
        rig.Warden.Handle(rig.Client.Write((byte)WardenClientCommand.CheckResult));

        WardenVerdict verdict = Assert.Single(rig.Verdicts);
        Assert.True(verdict.IsProtocolFailure);
        Assert.Equal(WardenAction.Kick, verdict.Action);
        Assert.Equal(WardenState.Failed, rig.Warden.State);
    }

    [Fact]
    public async Task AProtocolFailure_UnderKick_ClosesTheAuthenticatedSession()
    {
        await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IOptions<WardenOptions>>(
            Options.Create(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Kick, LoadFromDatabase = false })));
        byte[] key = await host.AddAccountAsync("wardenkick");
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("wardenkick", key);
        WorldSession session = Assert.Single(host.Registry.Sessions);
        WardenFeature feature = host.WorldServices.GetRequiredService<WardenFeature>();
        // OnAuthenticated runs on the session task after AUTH_OK is sent, so the client can see AUTH_OK before Warden is attached.
        await WorldTestHost.WaitForAsync(() => feature.Find(session) is not null, "Warden attached to the session");
        Assert.Equal(WardenState.AwaitingModuleStatus, feature.Find(session)!.State);

        await client.SendAsync(WorldOpcode.CmsgWardenData, new Client(key).Write(0xFF));
        await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "Warden kick closed the session");
    }

    private static async Task<List<string>> ChatLinesAsync(WorldTestClient client)
    {
        var lines = new List<string>();
        foreach ((WorldOpcode opcode, byte[] payload) in await client.CollectAsync(TimeSpan.FromMilliseconds(500)))
        {
            if (opcode == WorldOpcode.SmsgMessagechat)
            {
                lines.Add(ChatMessage.Parse(payload).Text);
            }
        }

        return lines;
    }

    private static WorldTestHost WardenHost(Action<AnnounceOptions> configure, RecordingSink sink, Action<IServiceCollection>? more = null, Action<WardenOptions>? warden = null)
    {
        var announce = new AnnounceOptions();
        configure(announce);
        var announcer = new OpsAnnouncer(announce, sink, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        return WardenHostWith(services => services.AddSingleton<IOpsAnnouncer>(announcer), more, warden);
    }

    private static WorldTestHost WardenHostWith(Action<IServiceCollection> announcer, Action<IServiceCollection>? more = null, Action<WardenOptions>? warden = null)
    {
        // Staff are exempt from Warden (they would be sent module data the test client does not answer).
        var options = new WardenOptions
        {
            Enabled = true,
            ProtocolAction = WardenAction.Kick,
            LoadFromDatabase = false,
            ExemptSecurity = AccountSecurity.Moderator,
        };
        warden?.Invoke(options);
        return WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IOptions<WardenOptions>>(Options.Create(options));
            announcer(services);
            more?.Invoke(services);
        });
    }

    private static async Task<(WorldTestClient Cheater, string Reason)> KickedAsync(WorldTestHost host, string account)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient cheater = await host.ConnectAsync();
        await cheater.AuthenticateAsync(account, key);
        WorldSession session = host.Registry.Sessions.Single(s => s.AccountName.Equals(account, StringComparison.OrdinalIgnoreCase));
        WardenFeature feature = host.WorldServices.GetRequiredService<WardenFeature>();
        var verdicts = new System.Collections.Concurrent.ConcurrentQueue<WardenVerdict>();
        feature.Verdict += (_, verdict) => verdicts.Enqueue(verdict);

        await cheater.SendAsync(WorldOpcode.CmsgWardenData, new Client(key).Write(0xFF));
        await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "Warden kick closed the session");
        return (cheater, Assert.Single(verdicts).Reason);
    }

    [Fact]
    public async Task AKickVerdict_ReachesStaffInFull_EvenWhenTheAnnouncerWouldDropIt()
    {
        // The announcer would send nothing for a Warning (floor Critical, staff line off): the staff line must not depend on it.
        var sink = new RecordingSink();
        await using WorldTestHost host = WardenHost(o =>
        {
            o.AntiCheat.GmNotify = false;
            o.AntiCheat.IncludeDetails = false;
            o.AntiCheat.MinimumSeverity = AnnounceSeverity.Critical;
        }, sink);
        await using WorldTestClient staff = await host.EnterWorldAsync("WARDSTAFF", "Wardstaff", AccountSecurity.Moderator);
        await staff.CollectAsync();

        (WorldTestClient cheater, string reason) = await KickedAsync(host, "wardencheater");
        await using (cheater)
        {
            string staffLine = Assert.Single(await ChatLinesAsync(staff));
            Assert.Contains("[Warden]", staffLine, StringComparison.Ordinal);
            Assert.Contains("wardencheater", staffLine, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(reason, staffLine, StringComparison.Ordinal);
            Assert.Empty(sink.Sent);
        }
    }

    [Fact]
    public async Task AKickVerdict_AccountNameAndReason_AppearOnlyInTheStaffLine_NeverInTheWorldLine()
    {
        var sink = new RecordingSink();
        await using WorldTestHost host = WardenHost(o => o.AntiCheat.WorldMessage = true, sink);
        await using WorldTestClient staff = await host.EnterWorldAsync("WARDSTAFF2", "Wardstafftwo", AccountSecurity.Moderator);
        await staff.CollectAsync();

        (WorldTestClient cheater, string reason) = await KickedAsync(host, "wardencheater2");
        await using (cheater)
        {
            // The feature's own direct line. The sink here only records, so this does not show that staff miss the world line; that rule
            // lives in WorldAnnounceSink and is proven against the real sink in WithTheRealSink_StaffGetOnlyTheDirectLine_AndPlayersOnlyTheWorldLine.
            string staffLine = Assert.Single(await ChatLinesAsync(staff));
            Assert.Contains(reason, staffLine, StringComparison.Ordinal);
            Assert.Contains("wardencheater2", staffLine, StringComparison.OrdinalIgnoreCase);

            AnnounceDelivery delivery = Assert.Single(sink.Sent);
            Assert.Null(delivery.StaffChat);
            Assert.Null(delivery.StaffNotify);
            Assert.True(delivery.StaffHandled);
            Assert.Contains("Warden disconnected a client", delivery.WorldChat, StringComparison.Ordinal);
            Assert.DoesNotContain(reason, delivery.WorldChat, StringComparison.Ordinal);
            Assert.DoesNotContain("wardencheater2", delivery.WorldChat, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task WithTheRealSink_StaffGetOnlyTheDirectLine_AndPlayersOnlyTheWorldLine()
    {
        // The real WorldAnnounceSink: the announcer's world line goes to every online player except staff, because the staff line was
        // already sent directly (staffHandled). Without that rule a staff member would get the world line as a second line.
        var announce = new AnnounceOptions();
        announce.AntiCheat.WorldMessage = true;
        WorldTestHost? self = null;
        await using WorldTestHost host = WardenHostWith(
            services => services.AddSingleton<IOpsAnnouncer>(_ => new OpsAnnouncer(
                announce, new WorldAnnounceSink(() => self!.World), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)),
            warden: o => o.ResponseTimeoutSeconds = 3600); // the bystander never answers its MODULE_USE; it must not be kicked meanwhile
        self = host;
        await using WorldTestClient staff = await host.EnterWorldAsync("WSINKSTAFF", "Wsinkstaff", AccountSecurity.Moderator);

        // An ordinary player is scanned, so its MODULE_USE arrives right after AUTH_OK: read it, then enter the world as usual.
        byte[] playerKey = await host.AddAccountAsync("WSINKPLAYER");
        await using WorldTestClient player = await host.ConnectAsync();
        await player.AuthenticateAsync("WSINKPLAYER", playerKey);
        await player.ReadUntilAsync(WorldOpcode.SmsgWardenData);
        await player.CreateCharacterAsync("Wsinkplayer", 1);
        int playerAccount = (await host.Accounts.FindByUsernameAsync("WSINKPLAYER"))!.Id;
        await player.LoginAsync((ulong)(await host.Characters.GetByAccountAsync(playerAccount)).Single().Id);
        await staff.CollectAsync();
        await player.CollectAsync();

        (WorldTestClient cheater, string reason) = await KickedAsync(host, "wsinkcheater");
        await using (cheater)
        {
            string staffLine = Assert.Single(await ChatLinesAsync(staff)); // exactly one: the direct full line, not the world line as well
            Assert.Contains("[Warden]", staffLine, StringComparison.Ordinal);
            Assert.Contains("wsinkcheater", staffLine, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(reason, staffLine, StringComparison.Ordinal);

            string worldLine = Assert.Single(await ChatLinesAsync(player)); // only the world line
            Assert.Contains("[Server]", worldLine, StringComparison.Ordinal);
            Assert.Contains("Warden disconnected a client", worldLine, StringComparison.Ordinal);
            Assert.DoesNotContain("wsinkcheater", worldLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(reason, worldLine, StringComparison.Ordinal);
            Assert.DoesNotContain("[Warden]", worldLine, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ASessionLoopOfOneAccount_GivesStaffOneLineThenOneSuppressedCount_AndAnotherAccountIsNotSuppressed()
    {
        // Sessions are cheap: one account reconnecting and sending a bad CMSG_WARDEN_DATA each time. Every verdict is still logged and
        // raised, but staff get one line per account and action per Warden:StaffNoticeCooldownSeconds (default 30), then one count.
        var clock = new ManualTimestamp();
        var sink = new RecordingSink();
        await using WorldTestHost host = WardenHost(_ => { }, sink, services => services.AddSingleton<TimeProvider>(clock));
        await using WorldTestClient staff = await host.EnterWorldAsync("WLOOPSTAFF", "Wloopstaff", AccountSecurity.Moderator);
        await staff.CollectAsync();
        WardenFeature feature = host.WorldServices.GetRequiredService<WardenFeature>();
        var kicks = new System.Collections.Concurrent.ConcurrentQueue<WardenVerdict>();
        feature.Verdict += (_, verdict) => kicks.Enqueue(verdict);

        const int Sessions = 6;
        byte[] key = await host.AddAccountAsync("wardenloop");
        int loopId = (await host.Accounts.FindByUsernameAsync("wardenloop"))!.Id;
        for (int i = 0; i < Sessions; i++)
        {
            await using WorldTestClient client = await host.ConnectAsync();
            await client.AuthenticateAsync("wardenloop", key);
            WorldSession session = host.Registry.Sessions.Single(s => s.AccountId == loopId && s.State != SessionState.Closed);
            await client.SendAsync(WorldOpcode.CmsgWardenData, new Client(key).Write(0xFF));
            await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, $"Warden kick {i + 1} closed the session");
        }

        (WorldTestClient other, _) = await KickedAsync(host, "wardenother");
        await using (other)
        {
            int otherId = (await host.Accounts.FindByUsernameAsync("wardenother"))!.Id;
            Assert.Equal(Sessions + 1, kicks.Count(v => v.Action == WardenAction.Kick)); // every verdict is still acted on and raised

            List<string> lines = await ChatLinesAsync(staff);
            Assert.Single(lines, l => l.Contains($"({loopId})", StringComparison.Ordinal)); // one line for the whole loop
            Assert.Single(lines, l => l.Contains($"({otherId})", StringComparison.Ordinal)); // another account is not suppressed
            Assert.Equal(2, lines.Count);

            clock.Advance(TimeSpan.FromSeconds(31)); // the window ends: the next world tick sends the held count, once
            List<string> summary = await ChatLinesAsync(staff);
            string count = Assert.Single(summary);
            Assert.Contains("[Warden]", count, StringComparison.Ordinal);
            Assert.Contains($"({loopId}): kick: {Sessions - 1} more suppressed in the last 30 s", count, StringComparison.Ordinal);
            Assert.Empty(await ChatLinesAsync(staff)); // reported once, and nothing for the account that held nothing back
        }
    }

    [Fact]
    public async Task AScanReplyWithSeveralFailedScans_SendsOneStaffLine_AndBansOnce()
    {
        var sink = new RecordingSink();
        await using WorldTestHost host = WardenHost(_ => { }, sink, warden: o =>
        {
            o.ScanIntervalMinSeconds = 1;
            o.ScanIntervalMaxSeconds = 1;
            o.ScansPerRequest = 3;
            o.BanSeconds = 0;
            o.Checks =
            [
                new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Memory, Address = 0x00401000, Expected = "90", Action = WardenAction.Ban },
                new WardenCheckOptions { Id = 2, Kind = WardenCheckKind.Memory, Address = 0x00402000, Expected = "90", Action = WardenAction.Kick },
                new WardenCheckOptions { Id = 3, Kind = WardenCheckKind.Memory, Address = 0x00403000, Expected = "90", Action = WardenAction.Ban },
            ];
        });
        await using WorldTestClient staff = await host.EnterWorldAsync("WMULTISTAFF", "Wmultistaff", AccountSecurity.Moderator);
        await staff.CollectAsync();
        WardenFeature feature = host.WorldServices.GetRequiredService<WardenFeature>();
        var verdicts = new System.Collections.Concurrent.ConcurrentQueue<WardenVerdict>();
        feature.Verdict += (_, verdict) => verdicts.Enqueue(verdict);

        byte[] key = await host.AddAccountAsync("wardenmulti");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("wardenmulti", key);
        WorldSession session = host.Registry.Sessions.Single(s => s.AccountName.Equals("wardenmulti", StringComparison.OrdinalIgnoreCase));
        var warden = new Client(key);
        Assert.Equal((byte)WardenServerCommand.ModuleUse, warden.Read(await client.ReadUntilAsync(WorldOpcode.SmsgWardenData))[0]);
        await client.SendAsync(WorldOpcode.CmsgWardenData, warden.Write((byte)WardenClientCommand.ModuleOk));
        Assert.Equal((byte)WardenServerCommand.HashRequest, warden.Read(await client.ReadUntilAsync(WorldOpcode.SmsgWardenData))[0]);
        await client.SendAsync(WorldOpcode.CmsgWardenData, warden.Write([(byte)WardenClientCommand.HashResult, .. WardenModuleProfile.ClientKeySeedHash]));
        warden.SwitchToModuleKeys();
        Assert.Equal((byte)WardenServerCommand.ModuleInitialize, warden.Read(await client.ReadUntilAsync(WorldOpcode.SmsgWardenData))[0]);
        Assert.Equal((byte)WardenServerCommand.CheatChecksRequest, warden.Read(await client.ReadUntilAsync(WorldOpcode.SmsgWardenData))[0]);

        await client.SendAsync(WorldOpcode.CmsgWardenData, warden.Write(CheckResultBody(0, 0x91, 0, 0x91, 0, 0x91))); // all three scans fail
        await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "the failed scans closed the session");

        Assert.Equal(3, verdicts.Count); // each failed scan is still its own verdict (logged, raised)
        List<string> lines = [.. (await ChatLinesAsync(staff)).Where(l => l.Contains("[Warden]", StringComparison.Ordinal))];

        // The kick is immediate and the ban write follows off the world thread, so StopAsync (which waits for the writes in flight) comes first:
        // every ban the batch started is a row by then. One batch: one staff line and one ban (both counts checked together).
        await feature.StopAsync();
        IReadOnlyList<AccountBanRecord> bans = await host.Bans.GetHistoryAsync(session.AccountId);
        Assert.Equal((Lines: 1, Bans: 1), (Lines: lines.Count, Bans: bans.Count));
        Assert.Contains("Warden banned an account", lines[0], StringComparison.Ordinal);
        Assert.Contains("3 failures", lines[0], StringComparison.Ordinal);
        AccountBanRecord ban = bans[0];
        Assert.Equal(WardenFeature.BanAuthor, ban.BannedBy);
        Assert.True(ban.IsPermanent);
        Assert.Contains("3 failures", ban.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProtocolFailure_UnderBan_ClosesTheSessionWithoutBanning()
    {
        await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IOptions<WardenOptions>>(
            Options.Create(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Ban, LoadFromDatabase = false })));
        byte[] key = await host.AddAccountAsync("wardenlag");
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("wardenlag", key);
        WorldSession session = Assert.Single(host.Registry.Sessions);
        WardenFeature feature = host.WorldServices.GetRequiredService<WardenFeature>();
        var verdicts = new System.Collections.Concurrent.ConcurrentQueue<WardenVerdict>();
        feature.Verdict += (_, verdict) => verdicts.Enqueue(verdict);

        await client.SendAsync(WorldOpcode.CmsgWardenData, new Client(key).Write(0xFF));
        await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "the protocol failure closed the session");

        // Deterministic: a Ban verdict kicks and then starts its write, and StopAsync waits for every write in flight, so a ban, had one
        // been issued, is stored by the time it returns.
        Assert.Equal(WardenAction.Kick, Assert.Single(verdicts).Action);
        await feature.StopAsync();
        Assert.Null(await host.Bans.GetActiveAccountBanAsync(session.AccountId));
    }

    [Fact]
    public async Task HeldVerdicts_AreReportedWhenTheSessionCloses_AndOnStop()
    {
        await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IOptions<WardenOptions>>(
            Options.Create(new WardenOptions { Enabled = true, ProtocolAction = WardenAction.Log, LoadFromDatabase = false,
                MaxProtocolFailures = 100, ProtocolReportIntervalSeconds = 3600 })));
        WardenFeature feature = host.WorldServices.GetRequiredService<WardenFeature>();
        var verdicts = new System.Collections.Concurrent.ConcurrentQueue<(WorldSession Session, WardenVerdict Verdict)>();
        feature.Verdict += (session, verdict) => verdicts.Enqueue((session, verdict));

        async Task<(WorldTestClient Client, WorldSession Session)> FloodAsync(string account)
        {
            byte[] key = await host.AddAccountAsync(account);
            WorldTestClient client = await host.ConnectAsync();
            await client.AuthenticateAsync(account, key);
            WorldSession session = host.Registry.Sessions.Single(s => s.AccountName.Equals(account, StringComparison.OrdinalIgnoreCase));
            var warden = new Client(key);
            for (int i = 0; i < 20; i++)
            {
                await client.SendAsync(WorldOpcode.CmsgWardenData, warden.Write(0xFF)); // an unknown command while the module status is due
            }

            await WorldTestHost.WaitForAsync(() => feature.Find(session) is not null, "Warden attached to the session");

            WardenSession scanner = feature.Find(session)!;
            await WorldTestHost.WaitForAsync(() => scanner.HeldVerdicts == 19, "the flood to be held");
            return (client, session);
        }

        (WorldTestClient closing, WorldSession closed) = await FloodAsync("wardenheld");
        await closing.DisposeAsync();
        await WorldTestHost.WaitForAsync(() => verdicts.Any(v => v.Session == closed && v.Verdict.Reason.StartsWith("19 more", StringComparison.Ordinal)),
            "the held count reported when the session closed");

        (_, WorldSession open) = await FloodAsync("wardenstop");
        await feature.StopAsync();
        Assert.Contains(verdicts, v => v.Session == open && v.Verdict.Reason.StartsWith("19 more", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedScan_UnderBan_BansTheAccountAndClosesTheSession()
    {
        await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IOptions<WardenOptions>>(
            Options.Create(new WardenOptions
            {
                Enabled = true,
                LoadFromDatabase = false,
                ScanIntervalMinSeconds = 1,
                ScanIntervalMaxSeconds = 1,
                BanSeconds = 0,
                Checks = [new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Memory, Address = 0x00401000, Expected = "90", Action = WardenAction.Ban }],
            })));
        byte[] key = await host.AddAccountAsync("wardenban");
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("wardenban", key);
        WorldSession session = Assert.Single(host.Registry.Sessions);
        var warden = new Client(key);

        Assert.Equal((byte)WardenServerCommand.ModuleUse, warden.Read(await client.ReadUntilAsync(WorldOpcode.SmsgWardenData))[0]);
        await client.SendAsync(WorldOpcode.CmsgWardenData, warden.Write((byte)WardenClientCommand.ModuleOk));
        Assert.Equal((byte)WardenServerCommand.HashRequest, warden.Read(await client.ReadUntilAsync(WorldOpcode.SmsgWardenData))[0]);
        await client.SendAsync(WorldOpcode.CmsgWardenData, warden.Write([(byte)WardenClientCommand.HashResult, .. WardenModuleProfile.ClientKeySeedHash]));
        warden.SwitchToModuleKeys();
        Assert.Equal((byte)WardenServerCommand.ModuleInitialize, warden.Read(await client.ReadUntilAsync(WorldOpcode.SmsgWardenData))[0]);
        Assert.Equal((byte)WardenServerCommand.CheatChecksRequest, warden.Read(await client.ReadUntilAsync(WorldOpcode.SmsgWardenData))[0]);

        await client.SendAsync(WorldOpcode.CmsgWardenData, warden.Write(CheckResultBody(0, 0x91))); // the byte at 0x401000 is not 90
        await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "the failed scan closed the session");

        ArcaneCore.Kernel.Accounts.AccountBanRecord? ban = null;
        for (int attempt = 0; attempt < 100 && ban is null; attempt++)
        {
            ban = await host.Bans.GetActiveAccountBanAsync(session.AccountId);
            if (ban is null)
            {
                await Task.Delay(20);
            }
        }

        Assert.NotNull(ban);
        Assert.Equal(WardenFeature.BanAuthor, ban.BannedBy);
        Assert.True(ban.IsPermanent);
        Assert.Contains("failed scan 1", ban.Reason);
    }

    /// <summary>An in-memory ban store whose ban write waits until the test releases it, then commits or fails (a stalled database).</summary>
    private sealed class GatedBanStore : IBanStore
    {
        private readonly ArcaneCore.World.Tests.Bans.InMemoryBanStore _inner = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When set, the released write throws this instead of committing.</summary>
        public Exception? FailOnRelease { get; init; }

        /// <summary>When set, the write blocks its calling thread until released (a provider that completes its async calls synchronously, as SQLite does).</summary>
        public bool BlockSynchronously { get; init; }

        /// <summary>Completes when the Warden's ban write has reached the store (and is waiting).</summary>
        public Task WriteEntered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public async Task BanAccountAsync(BanRequest request, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            if (BlockSynchronously)
            {
                _release.Task.Wait();
            }
            else
            {
                await _release.Task.ConfigureAwait(false);
            }

            if (FailOnRelease is not null)
            {
                throw FailOnRelease;
            }

            await _inner.BanAccountAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public Task<AccountBanRecord?> GetActiveAccountBanAsync(int accountId, CancellationToken cancellationToken = default) => _inner.GetActiveAccountBanAsync(accountId, cancellationToken);
        public Task<IpBanRecord?> GetActiveIpBanAsync(string ip, CancellationToken cancellationToken = default) => _inner.GetActiveIpBanAsync(ip, cancellationToken);
        public Task<IReadOnlySet<int>> FindBannedAccountsAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default) => _inner.FindBannedAccountsAsync(accountIds, cancellationToken);
        public Task<IReadOnlySet<string>> FindBannedIpsAsync(IReadOnlyCollection<string> ips, CancellationToken cancellationToken = default) => _inner.FindBannedIpsAsync(ips, cancellationToken);
        public Task<bool> BanIpAsync(IpBanRequest request, CancellationToken cancellationToken = default) => _inner.BanIpAsync(request, cancellationToken);
        public Task<bool> UnbanAccountAsync(int accountId, string source, string message, CancellationToken cancellationToken = default) => _inner.UnbanAccountAsync(accountId, source, message, cancellationToken);
        public Task<bool> UnbanIpAsync(string ip, CancellationToken cancellationToken = default) => _inner.UnbanIpAsync(ip, cancellationToken);
        public Task<IReadOnlyList<AccountBanRecord>> GetHistoryAsync(int accountId, CancellationToken cancellationToken = default) => _inner.GetHistoryAsync(accountId, cancellationToken);
        public Task<IReadOnlySet<int>> FindAccountsWithHistoryAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default) => _inner.FindAccountsWithHistoryAsync(accountIds, cancellationToken);
        public Task<IReadOnlyList<AccountBanRecord>> ListActiveAccountBansAsync(CancellationToken cancellationToken = default) => _inner.ListActiveAccountBansAsync(cancellationToken);
        public Task<IReadOnlyList<IpBanRecord>> ListIpBansAsync(string prefix, CancellationToken cancellationToken = default) => _inner.ListIpBansAsync(prefix, cancellationToken);
        public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default) => _inner.PurgeExpiredAsync(cancellationToken);
    }

    private sealed class BanStoreDownException(string message) : Exception(message);

    /// <summary>Runs the handshake and one failed Ban-action scan over a host whose ban store is <paramref name="store"/>; the session is not yet awaited.</summary>
    private static async Task<(WorldTestHost Host, WorldTestClient Client, WorldSession Session)> FailedBanScanAsync(GatedBanStore store, string account)
    {
        WorldTestHost host = WardenHostWith(_ => { }, services => services.AddSingleton<IBanStore>(store), o =>
        {
            o.ScanIntervalMinSeconds = 1;
            o.ScanIntervalMaxSeconds = 1;
            o.BanSeconds = 0;
            o.Checks = [new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Memory, Address = 0x00401000, Expected = "90", Action = WardenAction.Ban }];
        });
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        WorldSession session = host.Registry.Sessions.Single(s => s.AccountName.Equals(account, StringComparison.OrdinalIgnoreCase));
        var warden = new Client(key);
        Assert.Equal((byte)WardenServerCommand.ModuleUse, warden.Read(await client.ReadUntilAsync(WorldOpcode.SmsgWardenData))[0]);
        await client.SendAsync(WorldOpcode.CmsgWardenData, warden.Write((byte)WardenClientCommand.ModuleOk));
        Assert.Equal((byte)WardenServerCommand.HashRequest, warden.Read(await client.ReadUntilAsync(WorldOpcode.SmsgWardenData))[0]);
        await client.SendAsync(WorldOpcode.CmsgWardenData, warden.Write([(byte)WardenClientCommand.HashResult, .. WardenModuleProfile.ClientKeySeedHash]));
        warden.SwitchToModuleKeys();
        Assert.Equal((byte)WardenServerCommand.ModuleInitialize, warden.Read(await client.ReadUntilAsync(WorldOpcode.SmsgWardenData))[0]);
        Assert.Equal((byte)WardenServerCommand.CheatChecksRequest, warden.Read(await client.ReadUntilAsync(WorldOpcode.SmsgWardenData))[0]);
        await client.SendAsync(WorldOpcode.CmsgWardenData, warden.Write(CheckResultBody(0, 0x91)));
        return (host, client, session);
    }

    [Fact]
    public async Task ABanVerdict_ClosesTheSessionBeforeTheBanWriteCompletes()
    {
        var store = new GatedBanStore();
        (WorldTestHost host, WorldTestClient client, WorldSession session) = await FailedBanScanAsync(store, "wardengate");
        await using (host)
        await using (client)
        {
            try
            {
                await store.WriteEntered.WaitAsync(TimeSpan.FromSeconds(10));
                await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "the ban verdict closed the session while the ban write was still blocked");
                Assert.Null(await store.GetActiveAccountBanAsync(session.AccountId)); // not written yet
            }
            finally
            {
                store.Release();
            }

            await host.WorldServices.GetRequiredService<WardenFeature>().StopAsync(); // waits for the write in flight
            AccountBanRecord ban = Assert.IsType<AccountBanRecord>(await store.GetActiveAccountBanAsync(session.AccountId));
            Assert.Equal(WardenFeature.BanAuthor, ban.BannedBy);
            Assert.True(ban.IsPermanent);
        }
    }

    [Fact]
    public async Task StartBan_ReturnsAtOnce_EvenWhenTheStoreBlocksItsCallingThread()
    {
        // A provider that completes its async calls synchronously (SQLite) blocks the thread that runs the write. The write must not run on the caller.
        var store = new GatedBanStore { BlockSynchronously = true };
        await using WorldTestHost host = WardenHostWith(_ => { }, services => services.AddSingleton<IBanStore>(store));
        WardenFeature feature = host.WorldServices.GetRequiredService<WardenFeature>();
        try
        {
            await Task.Run(() => feature.StartBan(4242, "probe")).WaitAsync(TimeSpan.FromSeconds(10)); // inline, this would not return until released
            await store.WriteEntered.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            store.Release();
        }

        await feature.StopAsync();
        Assert.NotNull(await store.GetActiveAccountBanAsync(4242));
    }

    [Fact]
    public async Task StopAsync_RecordsABanWriteThatIsStillPendingAtItsBound()
    {
        var store = new GatedBanStore();
        (WorldTestHost host, WorldTestClient client, WorldSession session) = await FailedBanScanAsync(store, "wardenstop");
        await using (host)
        await using (client)
        {
            WardenFeature feature = host.WorldServices.GetRequiredService<WardenFeature>();
            feature.PendingBanStopWait = TimeSpan.FromMilliseconds(200);
            // The stop bound's message is built by Warden ("... (account(s) N) ..."): count the one that names this test's account.
            var probe = new ArcaneCore.World.Tests.Playerbots.FaultCountProbe("warden.ban-stop", $"account(s) {session.AccountId}");
            try
            {
                await store.WriteEntered.WaitAsync(TimeSpan.FromSeconds(10));
                await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "the session was kicked");
                await feature.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); // returns at its bound although the write never finishes
            }
            finally
            {
                store.Release();
            }

            probe.AssertCountedOnce("the pending ban write at the stop bound"); // LogFault records it: no Record of its own beside it
            Assert.Contains(ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Snapshot(),
                s => s.Site == "warden.ban-stop" && s.SampleMessage.Contains($"account(s) {session.AccountId}", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ABanVerdict_StillKicks_WhenTheBanWriteFails_AndTheFailureIsRecorded()
    {
        var store = new GatedBanStore { FailOnRelease = new BanStoreDownException("ban store down (wardenfail)") };
        (WorldTestHost host, WorldTestClient client, WorldSession session) = await FailedBanScanAsync(store, "wardenfail");
        await using (host)
        await using (client)
        {
            try
            {
                await store.WriteEntered.WaitAsync(TimeSpan.FromSeconds(10));
                await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "the session was kicked although the ban write has not failed yet");
            }
            finally
            {
                store.Release(); // the write now fails
            }

            await host.WorldServices.GetRequiredService<WardenFeature>().StopAsync();
            Assert.Contains(ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Snapshot(),
                s => s.Site == "warden.ban" && s.ExceptionType == typeof(BanStoreDownException).FullName);
            Assert.Equal(SessionState.Closed, session.State);
        }
    }

    [Fact]
    public async Task AFailedBanWrite_IsCountedOnce_UnderWardenBan()
    {
        var probe = new ArcaneCore.World.Tests.Playerbots.FaultCountProbe("warden.ban");
        var store = new GatedBanStore { FailOnRelease = probe.Fault() };
        (WorldTestHost host, WorldTestClient client, WorldSession session) = await FailedBanScanAsync(store, "wardenbancnt");
        await using (host)
        await using (client)
        {
            try
            {
                await store.WriteEntered.WaitAsync(TimeSpan.FromSeconds(10));
                await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "the session was kicked");
            }
            finally
            {
                store.Release(); // the write now fails
            }

            await host.WorldServices.GetRequiredService<WardenFeature>().StopAsync(); // waits for the write in flight
            probe.AssertCountedOnce("the ban write catch");
        }
    }

    [Fact]
    public async Task AThrowingVerdictSubscriber_IsCountedOnce_AndTheSessionIsStillKicked()
    {
        var probe = new ArcaneCore.World.Tests.Playerbots.FaultCountProbe("warden.verdict");
        await using WorldTestHost host = WardenHostWith(_ => { });
        host.WorldServices.GetRequiredService<WardenFeature>().Verdict += (_, _) => throw probe.Fault();
        byte[] key = await host.AddAccountAsync("wardenvcnt");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("wardenvcnt", key);
        WorldSession session = host.Registry.Sessions.Single(s => s.AccountName.Equals("wardenvcnt", StringComparison.OrdinalIgnoreCase));

        await client.SendAsync(WorldOpcode.CmsgWardenData, new Client(key).Write(0xFF)); // an unknown command: one Kick verdict
        await WorldTestHost.WaitForAsync(() => session.State == SessionState.Closed, "the verdict kicked the session after the subscriber threw");

        probe.AssertCountedOnce("the verdict subscriber catch");
    }

    private sealed class ThrowingWorldDbFactory(Exception failure) : Microsoft.EntityFrameworkCore.IDbContextFactory<ArcaneCore.Data.Content.WorldDbContext>
    {
        public ArcaneCore.Data.Content.WorldDbContext CreateDbContext() => throw failure;
    }

    [Fact]
    public async Task AFailedWardenChecksLoad_IsCountedOnce_UnderWardenLoadChecks()
    {
        var probe = new ArcaneCore.World.Tests.Playerbots.FaultCountProbe("warden.load-checks");
        var signal = new ArcaneCore.World.Tests.Playerbots.FaultSignalLogger<WardenFeature>(probe.Marker);
        await using WorldTestHost host = WardenHostWith(_ => { }, services =>
        {
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<WardenFeature>>(signal);
            services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<ArcaneCore.Data.Content.WorldDbContext>>(new ThrowingWorldDbFactory(probe.Fault()));
        }, o => o.LoadFromDatabase = true);

        await signal.Faulted.WaitAsync(TimeSpan.FromSeconds(30)); // the load runs in the background from Attach

        probe.AssertCountedOnce("the warden_checks load catch");
    }

    [Fact]
    public void InvalidChecks_AreReported()
    {
        WardenOptions options = Scans(
            new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Memory, Expected = "zz" },
            new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Driver });
        Assert.Equal(3, options.Validate().Count);
    }

    [Fact]
    public void TheSeed_IsTheVmangos5875Set_AndEveryRunnableRowParses()
    {
        Assert.Equal(94, WardenCheckSeed.Rows.Count);
        var skipped = new List<uint>();
        List<WardenCheckOptions> checks = WardenCheckRows.Merge(WardenCheckSeed.Rows, [], 5875, (row, _) => skipped.Add(row.Id));
        Assert.Empty(skipped);
        Assert.Equal(94, checks.Count);
        Assert.Equal(80, checks.Count(c => c.Kind == WardenCheckKind.Memory));
        Assert.Equal(6, checks.Count(c => c.Kind == WardenCheckKind.Mpq));
        Assert.Equal(4, checks.Count(c => c.Kind == WardenCheckKind.PageA));
        Assert.Equal(4, checks.Count(c => c.Kind == WardenCheckKind.ModuleByName));
        WardenCheckOptions gravity = checks.Single(c => c.Id == 8);
        Assert.Equal((8151666u, "D893FEC0"), (gravity.Address, gravity.Expected));
    }

    [Fact]
    public void Rows_AreFilteredByBuild_AndAConfiguredIdReplacesTheRow()
    {
        WardenCheckRow[] rows =
        [
            new() { Id = 1, Type = 0, Address = 16, Length = 2, Result = "9090", Penalty = 2 },
            new() { Id = 2, Type = 0, Address = 16, Length = 1, Result = "90", BuildMin = 6141, BuildMax = 6141 },
            new() { Id = 3, Type = 6, Result = "" },
            new() { Id = 4, Type = 0, Address = 16, Length = 3, Result = "90" },
        ];
        var skipped = new List<uint>();
        List<WardenCheckOptions> merged = WardenCheckRows.Merge(rows, [new WardenCheckOptions { Id = 9, Kind = WardenCheckKind.Timing }], 5875, (r, _) => skipped.Add(r.Id));
        Assert.Equal([1u, 9u], merged.Select(c => c.Id));
        Assert.Equal(WardenAction.Ban, merged[0].Action);
        Assert.Equal([3u, 4u], skipped);

        List<WardenCheckOptions> replaced = WardenCheckRows.Merge(rows, [new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Timing }], 5875);
        Assert.Equal(WardenCheckKind.Timing, Assert.Single(replaced).Kind);
    }

    [Fact]
    public void MpqLuaAndModuleScans_UseTheModulesFormat_AndTheirRepliesAreChecked()
    {
        byte[] hash = [.. Enumerable.Range(1, 20).Select(i => (byte)i)];
        WardenOptions options = Scans(
            new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Mpq, Path = "Interface\\x.lua", Expected = Convert.ToHexString(hash) },
            new WardenCheckOptions { Id = 2, Kind = WardenCheckKind.Mpq, Path = "bad.m2" },
            new WardenCheckOptions { Id = 3, Kind = WardenCheckKind.Lua, Path = "CheatVar", Wanted = false },
            new WardenCheckOptions { Id = 4, Kind = WardenCheckKind.Lua, Path = "GetLocale", Expected = "enUS" },
            new WardenCheckOptions { Id = 5, Kind = WardenCheckKind.ModuleByName, Module = "tamia.dll", Wanted = false });
        var rig = new Rig(options);
        rig.Handshake();
        rig.Warden.Update(10_000);
        byte[] request = rig.Next();
        byte x = WardenModuleProfile.XorByte;
        int p = 1;
        Assert.Equal("Interface\\x.lua", ReadString(request, ref p));
        Assert.Equal("bad.m2", ReadString(request, ref p));
        Assert.Equal("CheatVar", ReadString(request, ref p));
        Assert.Equal("GetLocale", ReadString(request, ref p));
        Assert.Equal(0, request[p++]);
        Assert.Equal([(byte)(WardenModuleProfile.OpMpq ^ x), 1, (byte)(WardenModuleProfile.OpMpq ^ x), 2, (byte)(WardenModuleProfile.OpLua ^ x), 3, (byte)(WardenModuleProfile.OpLua ^ x), 4],
            request[p..(p + 8)]);
        p += 8;
        Assert.Equal(WardenModuleProfile.OpModule ^ x, request[p++]);
        uint seed = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(p)); p += 4;
        Assert.Equal(WardenCheck.Hmac(seed, "TAMIA.DLL"u8), request[p..(p + 20)]); p += 20;
        Assert.Equal(x, request[p++]);
        Assert.Equal(request.Length, p);

        // clean: the file hash matches, the bad file is absent, the cheat variable is absent, the locale is enUS, no tamia.dll
        rig.Warden.Handle(rig.Result([0, .. hash, 1, 1, 0, 4, .. "enUS"u8, 0]));
        Assert.Empty(rig.Verdicts);

        rig.Warden.Update(10_000);
        rig.Next();
        byte[] other = [.. hash];
        other[0] ^= 0xFF;
        rig.Warden.Handle(rig.Result([0, .. other, 0, .. hash, 0, 1, (byte)'1', 0, 4, .. "deDE"u8, WardenModuleProfile.Found]));
        Assert.Equal([1u, 2u, 3u, 4u, 5u], rig.Verdicts.Select(v => v.CheckId!.Value));
    }

    private static string ReadString(byte[] data, ref int p)
    {
        int length = data[p++];
        string value = System.Text.Encoding.ASCII.GetString(data, p, length);
        p += length;
        return value;
    }
}
