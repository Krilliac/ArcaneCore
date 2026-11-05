using System.Buffers.Binary;
using ArcaneCore.Data.World.Warden;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Auxiliary;
using ArcaneCore.World.Warden;
using Xunit;

namespace ArcaneCore.World.Tests.Warden;

/// <summary>
/// The scans ported from vmangos src/game/Anticheat/WardenAnticheat (WardenScan.cpp WindowsHookScan; WardenWin.cpp LoadScriptedScans:
/// Warden / SYSTEM_INFO locate and the EndScene chain; ScanFlags::InitialLogin) and the MaNGOS Zero evidence classes and confirmation
/// rescans (src/game/Warden/WardenEvidence.cpp, WardenEnforcementPolicy.cpp). Each scan builds the vmangos request bytes (golden plaintext
/// CHEAT_CHECKS_REQUEST bodies: command 2, string table, 0, scans, terminator 0x7F; the xor byte is 0x7F, so READ_MEMORY 0xF3 is 0x8C on
/// the wire) and evaluates a canned reply.
/// </summary>
public sealed class WardenScanTests
{
    private static readonly byte[] SessionKey = [.. Enumerable.Range(0, 40).Select(i => (byte)i)];
    private static readonly byte[] Hash20 = [.. Enumerable.Range(1, 20).Select(i => (byte)i)];

    private static byte[] Request(params WardenCheck[] checks) => WardenCodec.CheckRequest(checks, static () => 0x11223344u);

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    private static WardenCheck Create(WardenCheckOptions options)
    {
        Assert.Null(WardenCheck.TryCreate(options, out WardenCheck? check));
        return check!;
    }

    private static WardenCheckOutcome Respond(WardenCheck check, params byte[] reply)
    {
        var reader = new PacketReader(reply);
        WardenCheckOutcome outcome = check.Evaluate(ref reader);
        Assert.Equal(0, reader.Remaining);
        check.ApplyScript();
        return outcome;
    }

    private static byte[] Read32(uint value)
    {
        byte[] reply = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(1), value);
        return reply;
    }

    private static byte[] SystemInfoBytes(ushort architecture, uint processorType)
    {
        byte[] info = new byte[WardenSystemInfo.Size];
        BinaryPrimitives.WriteUInt16LittleEndian(info, architecture);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), 0x1000);       // page size
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(12), 0x7FFEFFFF); // maximum application address
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(20), 4);          // processors
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(24), processorType);
        return info;
    }

    private sealed class Scripted
    {
        public readonly List<WardenCheck> Queued = [];
        public readonly List<string> Logged = [];
        public readonly WardenScriptedScans Scans;

        public Scripted() => Scans = new WardenScriptedScans(Queued.Add, Logged.Add);

        public WardenCheck TakeSingle()
        {
            WardenCheck next = Assert.Single(Queued);
            Queued.Clear();
            return next;
        }
    }

    // ---- API-hook scan (vmangos WindowsHookScan, type 6) ----

    [Fact]
    public void ApiHookScan_BuildsTheVmangosRequest_AndADetourFails()
    {
        WardenCheck hook = Create(new WardenCheckOptions
        {
            Id = 1, Kind = WardenCheckKind.ApiHook, Module = "kernel32.dll", Function = "GetTickCount", Expected = Convert.ToHexString(Hash20), Address = 0x10, Length = 5,
        });

        // opcode 0x7E ^ 0x7F, seed, the plain SHA-1, module index 1, proc index 2, offset, length
        Assert.Equal(
            "020C6B65726E656C33322E646C6C0C4765745469636B436F756E740001443322110102030405060708090A0B0C0D0E0F1011121314010210000000057F",
            Hex(Request(hook)));
        Assert.Equal(WardenCheckOutcome.Mismatch, Respond(hook, WardenModuleProfile.Found));
        Assert.Equal(WardenCheckOutcome.Match, Respond(hook, 0x00));
        Assert.Equal(1, hook.MaxReplySize);
        Assert.Equal(WardenEvidenceClass.ThreatSignature, hook.EvidenceClass);
    }

    [Fact]
    public void ApiHookScan_RequiresAHashModuleAndFunction()
    {
        Assert.NotNull(WardenCheck.TryCreate(new WardenCheckOptions { Kind = WardenCheckKind.ApiHook, Module = "a.dll", Function = "f", Expected = "00" }, out _));
        Assert.NotNull(WardenCheck.TryCreate(new WardenCheckOptions { Kind = WardenCheckKind.ApiHook, Function = "f", Expected = Convert.ToHexString(Hash20) }, out _));
        Assert.NotNull(WardenCheck.TryCreate(new WardenCheckOptions { Kind = WardenCheckKind.ApiHook, Module = "a.dll", Expected = Convert.ToHexString(Hash20) }, out _));
        Assert.NotNull(WardenCheck.TryCreate(new WardenCheckOptions { Kind = WardenCheckKind.ApiHook, Module = "a.dll", Function = "f", Expected = Convert.ToHexString(Hash20), Length = 256 }, out _));
    }

    [Fact]
    public void ApiHookRow_MapsTheVmangosColumns()
    {
        var row = new WardenCheckRow { Id = 7, Type = 6, Str = "user32.dll", Data = "PeekMessageA", Address = 4, Length = 8, Result = Convert.ToHexString(Hash20), Penalty = 1 };
        WardenCheckOptions? check = WardenCheckRows.ToOptions(row, 5875, out string? problem);
        Assert.Null(problem);
        Assert.NotNull(check);
        Assert.Equal(WardenCheckKind.ApiHook, check.Kind);
        Assert.Equal(("user32.dll", "PeekMessageA", 4u, 8), (check.Module, check.Function, check.Address, check.Length));
        Assert.Equal(WardenAction.Kick, check.Action);
        Assert.False(check.InitialLogin);
    }

    // ---- InitialLogin flag ----

    [Fact]
    public void InitialLoginFlag_ComesFromRowFlag8()
    {
        var row = new WardenCheckRow { Id = 1, Type = 0, Address = 16, Length = 1, Result = "90", Flags = 0x08 | 0x02 };
        Assert.True(WardenCheckRows.ToOptions(row, 5875, out _)!.InitialLogin);
        row.Flags = 0x40;
        Assert.False(WardenCheckRows.ToOptions(row, 5875, out _)!.InitialLogin);
    }

    [Fact]
    public void InitialLoginScan_IsSentRightAfterTheHandshake_AndNotInTheRotation()
    {
        var options = new WardenOptions
        {
            Enabled = true,
            Action = WardenAction.Kick,
            Checks =
            [
                new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Memory, Address = 0x00400000, Expected = "558BEC", InitialLogin = true },
                new WardenCheckOptions { Id = 2, Kind = WardenCheckKind.Memory, Address = 0x00401000, Expected = "90" },
            ],
        };
        var rig = new Rig(options);
        rig.Handshake();

        // READ_MEMORY, main image (string index 0), 0x00400000, 3 bytes
        Assert.Equal("02008C0000004000037F", Hex(rig.Next()));
        Assert.Equal(WardenState.AwaitingCheckResult, rig.Warden.State);
        rig.Reply(0x00, 0x55, 0x8B, 0xEC);
        Assert.Empty(rig.Verdicts);
        Assert.False(rig.HasUnread);

        // the rotation holds only the other scan, and the login scan never comes back
        for (int i = 0; i < 3; i++)
        {
            rig.Warden.Update(61_000);
            Assert.Equal("02008C0000104000017F", Hex(rig.Next()));
            rig.Reply(0x00, 0x90);
        }

        Assert.Empty(rig.Verdicts);
    }

    // ---- scripted scans: Warden and SYSTEM_INFO locate ----

    [Fact]
    public void WardenLocate_ReadsTheModulePointer_ThenTheSysInfoPointer_ThenSystemInfo()
    {
        var s = new Scripted();
        WardenCheck locate = s.Scans.WardenLocate();
        Assert.Equal("02008C007C89CE00047F", Hex(Request(locate))); // 0x00CE897C, 4 bytes
        Assert.Equal(WardenCheckOutcome.Match, Respond(locate, Read32(0x01000000)));
        Assert.Equal(0x01000000u, s.Scans.WardenAddress);

        WardenCheck pointer = s.TakeSingle();
        Assert.Equal("02008C0028020001047F", Hex(Request(pointer))); // module + 0x228
        Assert.Equal(WardenCheckOutcome.Match, Respond(pointer, Read32(0x02000000)));

        WardenCheck info = s.TakeSingle();
        Assert.Equal("02008C0008000002247F", Hex(Request(info))); // pointer + 0x08, 0x24 bytes
        Assert.Equal(WardenCheckOutcome.Match, Respond(info, [0x00, .. SystemInfoBytes(0, 586)]));
        Assert.Equal((ushort)0, s.Scans.SystemInfo!.Value.ProcessorArchitecture);
        Assert.Equal(586u, s.Scans.SystemInfo!.Value.ProcessorType);
        Assert.Equal(4u, s.Scans.SystemInfo!.Value.NumberOfProcessors);
        Assert.Empty(s.Queued);
        Assert.Empty(s.Logged);
    }

    [Theory]
    [InlineData((ushort)9, 586u, "incorrect architecture reported (9)")]
    [InlineData((ushort)0, 8664u, "incorrect processor type 8664")]
    public void SystemInfo_NotX86OrAnUnknownProcessorType_FailsTheScan(ushort architecture, uint processorType, string detail)
    {
        var s = new Scripted();
        WardenCheck info = s.Scans.SysInfo(0x02000008);
        Assert.Equal(WardenCheckOutcome.Mismatch, Respond(info, [0x00, .. SystemInfoBytes(architecture, processorType)]));
        Assert.Equal(detail, info.FailureDetail);
        Assert.Equal(architecture, s.Scans.SystemInfo!.Value.ProcessorArchitecture); // vmangos keeps the copy even when it fails
    }

    [Fact]
    public void ScriptedRead_ThatFails_IsUnavailable_AndQueuesNothing()
    {
        var s = new Scripted();
        WardenCheck locate = s.Scans.WardenLocate();
        Assert.Equal(WardenCheckOutcome.Unavailable, Respond(locate, 0x01));
        Assert.Equal("failed to read s_moduleInterface", locate.FailureDetail);
        Assert.Empty(s.Queued);
        Assert.Equal(0u, s.Scans.WardenAddress);
        Assert.Equal(WardenEvidenceClass.Corroboration, locate.EvidenceClass);
        Assert.False(locate.ConfirmationEligible);
    }

    // ---- scripted scans: the EndScene chain ----

    [Fact]
    public void EndSceneChain_FollowsTheDirect3DPointers_ToTheHookCheck()
    {
        var s = new Scripted();
        WardenCheck stage1 = s.Scans.EndSceneLocate1();
        Assert.Equal("02008C0038EDC000047F", Hex(Request(stage1))); // g_theGxDevicePtr 0x00C0ED38
        Assert.Equal(WardenCheckOutcome.Match, Respond(stage1, Read32(0x05000000)));

        WardenCheck api = s.TakeSingle();
        Assert.Equal("02008C00FC010005047F", Hex(Request(api))); // device + 0x1FC
        Assert.Equal(WardenCheckOutcome.Match, Respond(api, Read32(1)));
        Assert.Equal(WardenRenderingApi.Direct3D, s.Scans.RenderingApi);

        WardenCheck stage2 = s.TakeSingle();
        Assert.Equal("02008C00A8380005047F", Hex(Request(stage2))); // device + 0x38A8
        Assert.Equal(WardenCheckOutcome.Match, Respond(stage2, Read32(0x06000000)));

        WardenCheck stage3 = s.TakeSingle();
        Assert.Equal("02008C0000000006047F", Hex(Request(stage3))); // + 0x0
        Assert.Equal(WardenCheckOutcome.Match, Respond(stage3, Read32(0x07000000)));

        WardenCheck stage4 = s.TakeSingle();
        Assert.Equal("02008C00A8000007047F", Hex(Request(stage4))); // + 0xA8
        Assert.False(s.Scans.EndSceneFound);
        Assert.Equal(WardenCheckOutcome.Match, Respond(stage4, Read32(0x08000000)));
        Assert.True(s.Scans.EndSceneFound);
        Assert.Equal(0x08000000u, s.Scans.EndSceneAddress);

        WardenCheck hookCheck = s.TakeSingle();
        Assert.Same(s.Scans.HookCheck, hookCheck);
        Assert.Equal("02008C0000000008107F", Hex(Request(hookCheck))); // EndScene, 16 bytes

        // a clean EndScene (mov edi, edi; push ebp; mov ebp, esp; ...) logs nothing and queues nothing
        Assert.Equal(WardenCheckOutcome.Match, Respond(hookCheck, [0x00, 0x8B, 0xFF, 0x55, 0x8B, 0xEC, .. new byte[11]]));
        Assert.Empty(s.Logged);
        Assert.Empty(s.Queued);
    }

    [Fact]
    public void EndSceneChain_StopsForOpenGL_AndFailsOnANullDeviceOrUnknownApi()
    {
        var s = new Scripted();
        WardenCheck stage1 = s.Scans.EndSceneLocate1();
        Assert.Equal(WardenCheckOutcome.Mismatch, Respond(stage1, Read32(0)));
        Assert.Equal("g_theGxDevicePtr is null", stage1.FailureDetail);
        Assert.Empty(s.Queued);

        WardenCheck api = s.Scans.EndSceneLocateApi(0x05000000);
        Assert.Equal(WardenCheckOutcome.Match, Respond(api, Read32(0)));
        Assert.Equal(WardenRenderingApi.OpenGL, s.Scans.RenderingApi);
        Assert.Equal(0u, s.Scans.EndSceneAddress);
        Assert.Empty(s.Queued);

        WardenCheck unknown = s.Scans.EndSceneLocateApi(0x05000000);
        Assert.Equal(WardenCheckOutcome.Mismatch, Respond(unknown, Read32(2)));
        Assert.Equal("unknown rendering API kind 2", unknown.FailureDetail);
        Assert.Empty(s.Queued);
    }

    [Fact]
    public void EndSceneHookCheck_LogsAnInt3Hook()
    {
        var s = new Scripted();
        WardenCheck check = s.Scans.EndSceneHookCheck(0x08000000);
        Assert.Equal(WardenCheckOutcome.Match, Respond(check, [0x00, 0x90, 0xCC, .. new byte[14]])); // a hook is logged, never a failed scan
        Assert.Equal("INT3 EndScene hook detected (NOP count 1)", Assert.Single(s.Logged));
        Assert.Empty(s.Queued);
    }

    [Fact]
    public void EndSceneHookCheck_FollowsAJmpHook_AndFlagsWRobotCode()
    {
        var s = new Scripted();
        WardenCheck check = s.Scans.EndSceneHookCheck(0x08000000);

        // two NOPs, then JMP rel32 0x1000: target = 0x08000000 + 2 + 0x1000 + 5
        Assert.Equal(WardenCheckOutcome.Match, Respond(check, [0x00, 0x90, 0x90, 0xE9, 0x00, 0x10, 0x00, 0x00, .. new byte[9]]));
        Assert.Equal("JMP EndScene hook detected (NOP count 2, target 0x08001007)", Assert.Single(s.Logged));

        WardenCheck validate = s.TakeSingle();
        Assert.Equal("02008C0007100008C87F", Hex(Request(validate))); // 200 bytes at the target

        // junk (nop, jmp over two bytes), then pushfd, pushad: WRobot's hook
        byte[] code = new byte[WardenScriptedScans.HookCodeReadSize];
        byte[] prefix = [0x90, 0xEB, 0x02, 0xAA, 0xBB, 0x9C, 0x60, 0x50];
        prefix.CopyTo(code, 0);
        Assert.Equal(WardenCheckOutcome.Match, Respond(validate, [0x00, .. code]));
        Assert.Equal("suspicious EndScene hook code: probable bot", s.Logged[^1]);
    }

    [Fact]
    public void Deobfuscate_StripsTheWRobotJunk()
    {
        Assert.Equal(new byte[] { 0x9C, 0x60, 0x50 }, WardenEndScene.Deobfuscate([0x90, 0x87, 0xC9, 0x89, 0xFF, 0x93, 0xEB, 0x02, 0xAA, 0xBB, 0x9C, 0x60, 0x50]));

        // jz +5 paired with a jmp +3 at offset 2 (2 + 3 + 2 = 7): both skipped
        Assert.Equal(new byte[] { 0x50 }, WardenEndScene.Deobfuscate([0x74, 0x05, 0xEB, 0x03, 0xAA, 0xBB, 0xCC, 0x50]));

        // a jump past the end leaves nothing; real code stops the pass
        Assert.Empty(WardenEndScene.Deobfuscate([0xEB, 0x10, 0x00]));
        Assert.Equal(new byte[] { 0x55, 0x90 }, WardenEndScene.Deobfuscate([0x55, 0x90]));

        // a full read that is all junk is suspicious; plain code is not
        byte[] junk = new byte[WardenScriptedScans.HookCodeReadSize];
        junk[0] = 0xEB;
        junk[1] = 0xC6; // jumps over the remaining 198 bytes
        Assert.True(WardenEndScene.IsSuspiciousHook(junk));
        Assert.False(WardenEndScene.IsSuspiciousHook(Enumerable.Repeat((byte)0x55, WardenScriptedScans.HookCodeReadSize).ToArray()));
    }

    [Fact]
    public void ScriptedScans_RunInTheSession_AndAWrongSystemInfoTakesTheScanAction()
    {
        var rig = new Rig(new WardenOptions { Enabled = true, Action = WardenAction.Kick, ScriptedScans = true });
        rig.Handshake();

        // login: Warden locate and EndScene locate stage 1 together
        Assert.Equal("02008C007C89CE00048C0038EDC000047F", Hex(rig.Next()));
        rig.Reply([.. Read32(0x01000000), .. Read32(0x05000000)]);

        // their follow-ups go out at once
        Assert.Equal("02008C0028020001048C00FC010005047F", Hex(rig.Next()));
        rig.Reply([.. Read32(0x02000000), .. Read32(1)]);

        Assert.Equal("02008C0008000002248C00A8380005047F", Hex(rig.Next()));
        Assert.Empty(rig.Verdicts);
        rig.Reply([0x00, .. SystemInfoBytes(9, 586), .. Read32(0x06000000)]);

        WardenVerdict verdict = Assert.Single(rig.Verdicts);
        Assert.Equal(WardenAction.Kick, verdict.Action);
        Assert.False(verdict.IsProtocolFailure);
        Assert.Equal("failed scan 0 (Sysinfo locate): incorrect architecture reported (9)", verdict.Reason);
        Assert.Equal((ushort)9, rig.Warden.Scripted!.SystemInfo!.Value.ProcessorArchitecture);
        Assert.Equal("02008C0000000006047F", Hex(rig.Next())); // EndScene stage 3 continues
    }

    // ---- MaNGOS Zero evidence classes and confirmation rescans ----

    [Fact]
    public void EvidenceClasses_FollowMaNGOSZero()
    {
        Assert.Equal(WardenEvidenceClass.ProtocolHealth, WardenCheck.Timing.EvidenceClass);
        Assert.Equal(WardenEvidenceClass.IntegrityInvariant, Create(new WardenCheckOptions { Kind = WardenCheckKind.Memory, Expected = "90" }).EvidenceClass);
        Assert.Equal(WardenEvidenceClass.IntegrityInvariant, Create(new WardenCheckOptions { Kind = WardenCheckKind.Mpq, Path = "a" }).EvidenceClass);
        Assert.Equal(WardenEvidenceClass.Corroboration, Create(new WardenCheckOptions { Kind = WardenCheckKind.Lua, Path = "a" }).EvidenceClass);
        Assert.Equal(WardenEvidenceClass.ThreatSignature, Create(new WardenCheckOptions { Kind = WardenCheckKind.ModuleByName, Module = "a.dll" }).EvidenceClass);
        Assert.False(WardenCheck.Timing.ConfirmationEligible);

        Assert.Equal(WardenConfirmedDisposition.Cleared, WardenEvidence.ClassifyConfirmed(WardenEvidenceClass.IntegrityInvariant, WardenCheckOutcome.Match));
        Assert.Equal(WardenConfirmedDisposition.Audit, WardenEvidence.ClassifyConfirmed(WardenEvidenceClass.IntegrityInvariant, WardenCheckOutcome.Unavailable));
        Assert.Equal(WardenConfirmedDisposition.Incident, WardenEvidence.ClassifyConfirmed(WardenEvidenceClass.ThreatSignature, WardenCheckOutcome.Mismatch));
        Assert.Equal(WardenConfirmedDisposition.Audit, WardenEvidence.ClassifyConfirmed(WardenEvidenceClass.Corroboration, WardenCheckOutcome.Mismatch));
        Assert.Equal(WardenConfirmedDisposition.Invalid, WardenEvidence.ClassifyConfirmed(WardenEvidenceClass.ProtocolHealth, WardenCheckOutcome.Unstable));
    }

    private static WardenOptions Confirming(params WardenCheckOptions[] checks)
        => new() { Enabled = true, Action = WardenAction.Kick, ConfirmFailedScans = true, Checks = [.. checks] };

    [Fact]
    public void ConfirmFailedScans_AMismatchIsRescannedAlone_AndActsOnlyWhenConfirmed()
    {
        var rig = new Rig(Confirming(new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Memory, Address = 0x00400000, Expected = "558BEC" }));
        rig.Handshake();
        rig.Warden.Update(61_000);
        Assert.Equal("02008C0000004000037F", Hex(rig.Next()));
        rig.Reply(0x00, 0x00, 0x00, 0x00);
        Assert.Empty(rig.Verdicts);

        // the isolated confirmation goes out at once, with the same bytes
        Assert.Equal("02008C0000004000037F", Hex(rig.Next()));
        rig.Reply(0x00, 0x00, 0x00, 0x00);
        WardenVerdict verdict = Assert.Single(rig.Verdicts);
        Assert.Equal(WardenAction.Kick, verdict.Action);
        Assert.Equal(1u, verdict.CheckId);
        Assert.EndsWith("confirmed by an isolated rescan", verdict.Reason, StringComparison.Ordinal);
        Assert.False(rig.HasUnread);
    }

    [Fact]
    public void ConfirmFailedScans_AMatchingRescanClearsTheFinding()
    {
        var rig = new Rig(Confirming(new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Memory, Address = 0x00400000, Expected = "558BEC" }));
        rig.Handshake();
        rig.Warden.Update(61_000);
        rig.Next();
        rig.Reply(0x01); // unreadable
        rig.Next();
        rig.Reply(0x00, 0x55, 0x8B, 0xEC);
        WardenVerdict verdict = Assert.Single(rig.Verdicts);
        Assert.Equal(WardenAction.Log, verdict.Action);
        Assert.Contains("passed its confirmation rescan", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmFailedScans_ACorroborationMismatchIsAuditedOnce()
    {
        var rig = new Rig(Confirming(new WardenCheckOptions { Id = 2, Kind = WardenCheckKind.Lua, Path = "GetLocale", Expected = "enUS" }));
        rig.Handshake();
        rig.Warden.Update(61_000);
        Assert.Equal("02094765744C6F63616C6500F4017F", Hex(rig.Next())); // GET_LUA_VARIABLE 0x8B ^ 0x7F, string 1
        byte[] deDE = [0x00, 0x04, .. "deDE"u8];
        rig.Reply(deDE);
        rig.Next();
        rig.Reply(deDE);
        WardenVerdict audit = Assert.Single(rig.Verdicts);
        Assert.Equal(WardenAction.Log, audit.Action);
        Assert.Contains("confirmed Mismatch", audit.Reason, StringComparison.Ordinal);
        Assert.False(rig.HasUnread);

        // the same outcome again is not rescanned or reported again
        rig.Warden.Update(61_000);
        rig.Next();
        rig.Reply(deDE);
        Assert.Single(rig.Verdicts);
        Assert.Equal(0, rig.Warden.QueuedScans);
        Assert.False(rig.HasUnread);
    }

    [Fact]
    public void ConfirmFailedScans_AnUnstableTimingScanIsOnlyLogged()
    {
        var rig = new Rig(Confirming());
        rig.Handshake();
        rig.Warden.Update(61_000);
        Assert.Equal("0200287F", Hex(rig.Next())); // CHECK_TIMING_VALUES 0x57 ^ 0x7F
        rig.Reply(0x00, 0x01, 0x02, 0x03, 0x04);
        WardenVerdict verdict = Assert.Single(rig.Verdicts);
        Assert.Equal(WardenAction.Log, verdict.Action);
        Assert.Equal("failed scan 0 (timing)", verdict.Reason);
        Assert.Equal(0, rig.Warden.QueuedScans);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 })] // mismatch
    [InlineData(new byte[] { 0x01 })] // unreadable
    public void ConfirmFailedScans_AnUnnumberedCheckFailure_IsLoggedOnly_NeverActs(byte[] reply)
    {
        // MaNGOS Zero confirms only a non-zero check id and produces no decision for the rest, so id 0 must not Kick or Ban on one unconfirmed reply.
        var check = new WardenCheckOptions { Id = 0, Kind = WardenCheckKind.Memory, Address = 0x00400000, Expected = "558BEC" };
        var rig = new Rig(Confirming(check));
        rig.Handshake();
        rig.Warden.Update(61_000);
        Assert.Equal("02008C0000004000037F", Hex(rig.Next()));
        rig.Reply(reply);
        WardenVerdict verdict = Assert.Single(rig.Verdicts);
        Assert.Equal(WardenAction.Log, verdict.Action);
        Assert.Equal(0u, verdict.CheckId);
        Assert.Equal(0, rig.Warden.QueuedScans);
        Assert.False(rig.HasUnread);
    }

    [Fact]
    public void WithoutConfirmFailedScans_AFailedScanStillActsAtOnce()
    {
        var options = Confirming(new WardenCheckOptions { Id = 1, Kind = WardenCheckKind.Memory, Address = 0x00400000, Expected = "558BEC" });
        options.ConfirmFailedScans = false;
        var rig = new Rig(options);
        rig.Handshake();
        rig.Warden.Update(61_000);
        rig.Next();
        rig.Reply(0x00, 0x00, 0x00, 0x00);
        Assert.Equal(WardenAction.Kick, Assert.Single(rig.Verdicts).Action);
        Assert.Equal(0, rig.Warden.QueuedScans);
    }

    [Fact]
    public void GatewayManifest_IsUnchangedByDefault_AndCarriesTheNewOptionsWhenSet()
    {
        var options = new WardenOptions { Enabled = true };
        string plain = GatewayWardenManifestVerifier.SummarizeOptions(options);
        Assert.DoesNotContain("scripted", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("confirm", plain, StringComparison.Ordinal);
        options.ScriptedScans = true;
        options.ConfirmFailedScans = true;
        Assert.EndsWith(";scripted=1;confirm=1", GatewayWardenManifestVerifier.SummarizeOptions(options), StringComparison.Ordinal);
    }

    /// <summary>A plain CHECK_RESULT with its length and folded checksum.</summary>
    private static byte[] CheckResultBody(byte[] results)
    {
        byte[] body = new byte[7 + results.Length];
        body[0] = (byte)WardenClientCommand.CheckResult;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), (ushort)results.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(3), WardenCodec.Checksum(results));
        results.CopyTo(body, 7);
        return body;
    }

    /// <summary>A session against a simulated client holding the same RC4 streams; <see cref="Next"/> reads the sent bodies in order.</summary>
    private sealed class Rig
    {
        public readonly List<byte[]> Sent = [];
        public readonly List<WardenVerdict> Verdicts = [];
        public readonly WardenSession Warden;
        private WardenRc4 _toServer;
        private WardenRc4 _fromServer;
        private int _read;

        public Rig(WardenOptions options)
        {
            WardenCryptoContext.DeriveInitialKeys(SessionKey, out byte[] client, out byte[] server);
            _toServer = new WardenRc4(client);
            _fromServer = new WardenRc4(server);
            Warden = new WardenSession(SessionKey, options, Sent.Add, Verdicts.Add, new Random(7));
        }

        public bool HasUnread => _read < Sent.Count;

        public byte[] Next()
        {
            Assert.True(HasUnread, "nothing more was sent");
            byte[] copy = [.. Sent[_read++]];
            _fromServer.Transform(copy);
            return copy;
        }

        public void Handshake()
        {
            Warden.Start();
            Next(); // MODULE_USE
            Warden.Handle(Write([(byte)WardenClientCommand.ModuleOk]));
            Next(); // HASH_REQUEST
            Warden.Handle(Write([(byte)WardenClientCommand.HashResult, .. WardenModuleProfile.ClientKeySeedHash]));
            _toServer = new WardenRc4(WardenModuleProfile.ClientKeySeed);
            _fromServer = new WardenRc4(WardenModuleProfile.ServerKeySeed);
            Assert.Equal((byte)WardenServerCommand.ModuleInitialize, Next()[0]);
        }

        public void Reply(params byte[] results) => Warden.Handle(Write(CheckResultBody(results)));

        private byte[] Write(byte[] plain)
        {
            byte[] copy = [.. plain];
            _toServer.Transform(copy);
            return copy;
        }
    }
}
