using System.Buffers.Binary;

namespace ArcaneCore.World.Warden;

/// <summary>
/// vmangos <c>ScanFlags</c> (src/game/Anticheat/WardenAnticheat/WardenScan.hpp), the values stored in <c>warden_checks.flags</c>. Only
/// <see cref="InitialLogin"/> changes what ArcaneCore does; the rest are listed so a row's flags read the same as in vmangos.
/// </summary>
[Flags]
internal enum WardenScanFlags : uint
{
    None = 0x00,
    FromDatabase = 0x01,
    Windows = 0x02,
    Mac = 0x04,

    /// <summary>Sent once when the session's Warden is ready (vmangos Warden::HandlePacket HASH_RESULT: SelectScans(InitialLogin)), never in the rotation.</summary>
    InitialLogin = 0x08,
    Maiev = 0x20,
    OffsetsInitialized = 0x40,
}

/// <summary>What a check's result is evidence of (MaNGOS Zero src/game/Warden/WardenCheckCatalog.h WardenEvidenceClass).</summary>
internal enum WardenEvidenceClass : byte
{
    /// <summary>The transport and client clock are healthy (timing); never punished.</summary>
    ProtocolHealth = 0,

    /// <summary>Client code or data that must not change (memory, MPQ); actionable.</summary>
    IntegrityInvariant = 1,

    /// <summary>A known cheat is present (page, driver, module, API hook); actionable.</summary>
    ThreatSignature = 2,

    /// <summary>Supporting evidence only (Lua, scripted fingerprint scans); logged, never punished under the evidence policy.</summary>
    Corroboration = 3,
}

/// <summary>One scan's classified result (MaNGOS Zero WardenCheckCatalog.h WardenCheckOutcome).</summary>
internal enum WardenCheckOutcome : byte
{
    Match = 0,
    Mismatch = 1,

    /// <summary>The client could not produce the value (a failed read, a missing file or variable).</summary>
    Unavailable = 2,
    Stable = 3,
    Unstable = 4,
}

/// <summary>What a confirmation rescan concludes (MaNGOS Zero WardenEvidence.h WardenConfirmedDisposition).</summary>
internal enum WardenConfirmedDisposition : byte
{
    Invalid,

    /// <summary>The rescan matched: no finding.</summary>
    Cleared,

    /// <summary>Logged once per check and outcome; no action.</summary>
    Audit,

    /// <summary>A confirmed actionable mismatch: the check's action applies.</summary>
    Incident,
}

/// <summary>The MaNGOS Zero evidence rules (src/game/Warden/WardenEvidence.cpp, WardenCheckCatalog.cpp IsActionableEvidenceClass).</summary>
internal static class WardenEvidence
{
    /// <summary>Anything but Match and Stable fails the scan.</summary>
    public static bool IsFailure(WardenCheckOutcome outcome) => outcome is not (WardenCheckOutcome.Match or WardenCheckOutcome.Stable);

    /// <summary>Integrity invariants and threat signatures may lead to an action; health and corroboration evidence never does.</summary>
    public static bool IsActionable(WardenEvidenceClass evidenceClass)
        => evidenceClass is WardenEvidenceClass.IntegrityInvariant or WardenEvidenceClass.ThreatSignature;

    /// <summary>MaNGOS Zero NeedsConfirmation: an eligible (non-timing) check that came back Mismatch or Unavailable is rescanned alone first.</summary>
    public static bool NeedsConfirmation(WardenCheck check, WardenCheckOutcome outcome)
        => check.ConfirmationEligible && (outcome is WardenCheckOutcome.Mismatch or WardenCheckOutcome.Unavailable);

    /// <summary>
    /// MaNGOS Zero ClassifyConfirmedEvidence for the confirmation rescan's outcome: Match clears, Unavailable is an audit, Mismatch is an
    /// incident only for an actionable class (an audit otherwise). The Observe mode of MaNGOS Zero is a check action of Log here.
    /// </summary>
    public static WardenConfirmedDisposition ClassifyConfirmed(WardenEvidenceClass evidenceClass, WardenCheckOutcome outcome) => outcome switch
    {
        WardenCheckOutcome.Match => WardenConfirmedDisposition.Cleared,
        WardenCheckOutcome.Unavailable => WardenConfirmedDisposition.Audit,
        WardenCheckOutcome.Mismatch => IsActionable(evidenceClass) ? WardenConfirmedDisposition.Incident : WardenConfirmedDisposition.Audit,
        _ => WardenConfirmedDisposition.Invalid,
    };
}

/// <summary>Judges a scripted read: the failure detail, or null when it passes. Must not change state (the reply is not validated yet).</summary>
internal delegate string? WardenScriptedEvaluate(bool readOk, ReadOnlySpan<byte> data);

/// <summary>Acts on a scripted read once its whole reply is valid: store what was read, queue the next stage, log a finding.</summary>
internal delegate void WardenScriptedApply(bool readOk, ReadOnlySpan<byte> data);

/// <summary>The checker half of a scripted scan (vmangos Scan::CheckT), split into a pure judgement and a deferred follow-up.</summary>
internal sealed class WardenScriptedRead(WardenScriptedEvaluate evaluate, WardenScriptedApply apply)
{
    public string? Evaluate(bool readOk, ReadOnlySpan<byte> data) => evaluate(readOk, data);

    public void Apply(bool readOk, ReadOnlySpan<byte> data) => apply(readOk, data);
}

/// <summary>
/// The fixed build 5875 client addresses the scripted scans read (vmangos WardenWin.cpp, the 5875 row of the <c>Offsets</c> table:
/// WardenModule, OfsWardenSysInfo, OfsWardenWinSysInfo, g_theGxDevicePtr, g_gxDevicePtr_OfsApiKind, OfsDevice2, OfsDevice3, OfsDevice4).
/// </summary>
internal static class WardenClientOffsets
{
    /// <summary>The client's pointer to the loaded Warden module (s_moduleInterface).</summary>
    public const uint WardenModule = 0x00CE897C;

    /// <summary>Offset in the module of its pointer to the WardenSysInfo record.</summary>
    public const uint OfsWardenSysInfo = 0x228;

    /// <summary>Offset in WardenSysInfo of the Windows SYSTEM_INFO copy.</summary>
    public const uint OfsWardenWinSysInfo = 0x08;

    /// <summary>g_theGxDevicePtr, the start of the pointer chain to the Direct3D EndScene.</summary>
    public const uint GxDevicePtr = 0x00C0ED38;

    /// <summary>Offset in the GX device of its rendering API kind (0 OpenGL, 1 Direct3D).</summary>
    public const uint OfsApiKind = 0x1FC;

    public const uint OfsDevice2 = 0x38A8;

    public const uint OfsDevice3 = 0x0;

    public const uint OfsDevice4 = 0xA8;
}

/// <summary>The renderer the client reports (vmangos WardenWin.hpp ClientRenderingApi).</summary>
internal enum WardenRenderingApi
{
    OpenGL,

    /// <summary>DirectX 9.0c in the 1.12 client.</summary>
    Direct3D,
}

/// <summary>
/// The 32-bit Windows SYSTEM_INFO the client's Warden module copies (vmangos WardenWin.hpp WIN_SYSTEM_INFO, packed, 0x24 bytes):
/// architecture and reserved word, page size, minimum and maximum application address, active processor mask, processor count,
/// processor type, allocation granularity, processor level and revision.
/// </summary>
internal readonly record struct WardenSystemInfo(
    ushort ProcessorArchitecture,
    uint PageSize,
    uint MinimumApplicationAddress,
    uint MaximumApplicationAddress,
    uint ActiveProcessorMask,
    uint NumberOfProcessors,
    uint ProcessorType,
    uint AllocationGranularity,
    ushort ProcessorLevel,
    ushort ProcessorRevision)
{
    public const int Size = 0x24;

    public static WardenSystemInfo Read(ReadOnlySpan<byte> data) => new(
        BinaryPrimitives.ReadUInt16LittleEndian(data),
        BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
        BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
        BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
        BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
        BinaryPrimitives.ReadUInt32LittleEndian(data[24..]),
        BinaryPrimitives.ReadUInt32LittleEndian(data[28..]),
        BinaryPrimitives.ReadUInt16LittleEndian(data[32..]),
        BinaryPrimitives.ReadUInt16LittleEndian(data[34..]));

    /// <summary>
    /// vmangos "Sysinfo locate" checker: a classic client only ever runs as x86 (architecture 0) on an i386, i486 or Pentium (586) processor
    /// type; anything else is a spoofed or emulated report.
    /// </summary>
    public string? Problem => ProcessorArchitecture != 0
        ? $"incorrect architecture reported ({ProcessorArchitecture})"
        : (ProcessorType is not (386 or 486 or 586)) ? $"incorrect processor type {ProcessorType}" : null;
}

/// <summary>What the first instruction of EndScene is (vmangos WardenWin::ValidateEndScene).</summary>
internal enum WardenEndSceneHook
{
    None,
    Int3,
    Jump,
}

/// <summary>The EndScene inspection: the hook found, the NOPs before it, and for a JMP its absolute target.</summary>
internal readonly record struct WardenEndSceneInspection(WardenEndSceneHook Hook, int NopCount, uint JumpTarget);

/// <summary>
/// EndScene hook analysis, re-expressed from vmangos WardenWin.cpp (ValidateEndScene, DeobfuscateAsm, ValidateEndSceneHook). Every read is
/// bounds-checked: where vmangos reads past the buffer (a NOP run to the end, a truncated jump operand), the code is taken as it is.
/// </summary>
internal static class WardenEndScene
{
    // Two-byte xchg forms that are no-ops or are undone later in the WRobot obfuscation (vmangos DeobfuscateAsm, 0x87 list).
    private static ReadOnlySpan<byte> XchgNoOps =>
    [
        0xC9, 0xCA, 0xCB, 0xCE, 0xCF, 0xE4, 0xED, 0xD1, 0xD2, 0xD3, 0xD6, 0xD7, 0xD9, 0xDA, 0xDB, 0xDE, 0xDF, 0xF1, 0xF2, 0xF3, 0xF7, 0xF9,
        0xFA, 0xFB, 0xFE,
    ];

    // mov reg, same reg (vmangos DeobfuscateAsm, 0x89 list).
    private static ReadOnlySpan<byte> MovNoOps => [0xC9, 0xD2, 0xDB, 0xE4, 0xFF];

    // Short conditional jumps the obfuscation pairs with an unconditional jump to the same place (vmangos DeobfuscateAsm).
    private static ReadOnlySpan<byte> PairedConditionalJumps => [0x70, 0x71, 0x74, 0x75, 0x78, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F];

    /// <summary>
    /// vmangos ValidateEndScene: skip leading NOPs (0x90); an INT3 (0xCC) is a breakpoint hook; a JMP rel32 (0xE9) is a detour whose
    /// absolute target is EndScene + NOPs + rel32 + 5 (32-bit wrap-around, as in the client's address space).
    /// </summary>
    public static WardenEndSceneInspection Inspect(ReadOnlySpan<byte> code, uint endSceneAddress)
    {
        int p = 0;
        while (p < code.Length && code[p] == 0x90)
        {
            p++;
        }

        if (p < code.Length && code[p] == 0xCC)
        {
            return new WardenEndSceneInspection(WardenEndSceneHook.Int3, p, 0);
        }

        if (p + 5 <= code.Length && code[p] == 0xE9)
        {
            uint relative = BinaryPrimitives.ReadUInt32LittleEndian(code[(p + 1)..]);
            return new WardenEndSceneInspection(WardenEndSceneHook.Jump, p, unchecked(endSceneAddress + (uint)p + relative + 5));
        }

        return new WardenEndSceneInspection(WardenEndSceneHook.None, p, 0);
    }

    /// <summary>
    /// vmangos ValidateEndSceneHook: the hook code is suspicious when its deobfuscated form starts with pushfd, pushad (WRobot), or when a
    /// full 200-byte read collapses to under 15 bytes of real code.
    /// </summary>
    public static bool IsSuspiciousHook(ReadOnlySpan<byte> code)
    {
        byte[] clean = Deobfuscate(code);
        if (clean.Length >= 2 && clean[0] == 0x9C && clean[1] == 0x60)
        {
            return true;
        }

        return code.Length == WardenScriptedScans.HookCodeReadSize && clean.Length < 15;
    }

    /// <summary>
    /// vmangos DeobfuscateAsm: strip the junk the WRobot obfuscator puts in front of the real code, one leading instruction at a time:
    /// NOPs, no-op xchg and mov forms, single-byte xchg, short and near jumps over junk, and a short conditional jump whose paired
    /// unconditional jump within the next 12 bytes lands on the same place. pushfd (0x9C) and pushad (0x60) are real code: they are kept and
    /// the rest after them is deobfuscated. The first instruction that is none of these ends the pass. A jump past the end leaves nothing.
    /// </summary>
    public static byte[] Deobfuscate(ReadOnlySpan<byte> code)
    {
        var kept = new List<byte>();
        int i = 0;
        while (i < code.Length)
        {
            int remaining = code.Length - i;
            byte op = code[i];
            long strip = 0;
            if (op == 0x90 || op is >= 0x91 and <= 0x97)
            {
                strip = 1;
            }
            else if (op == 0x87)
            {
                strip = remaining >= 2 && XchgNoOps.Contains(code[i + 1]) ? 2 : 0;
            }
            else if (op == 0x89)
            {
                strip = remaining >= 2 && MovNoOps.Contains(code[i + 1]) ? 2 : 0;
            }
            else if (op == 0xEB)
            {
                strip = remaining < 2 ? long.MaxValue : code[i + 1] + 2L;
            }
            else if (op == 0xE9)
            {
                // vmangos adds 5 in 32-bit unsigned arithmetic; here the length does not wrap, so a huge operand strips everything.
                strip = remaining < 5 ? long.MaxValue : BinaryPrimitives.ReadUInt32LittleEndian(code[(i + 1)..]) + 5L;
            }
            else if (PairedConditionalJumps.Contains(op))
            {
                strip = PairedJumpLength(code[i..]);
            }
            else if (op is 0x9C or 0x60)
            {
                kept.Add(op);
                i++;
                continue;
            }

            if (strip == 0)
            {
                break;
            }

            if (strip >= remaining)
            {
                i = code.Length;
                break;
            }

            i += (int)strip;
        }

        kept.AddRange(code[i..]);
        return [.. kept];
    }

    /// <summary>
    /// The bytes to strip for a short conditional jump (target = rel8 + 2) when the first unconditional jump at offsets 2-13 lands on the
    /// same target, else 0. Like vmangos, a near jump's target is its rel32 + 5 without its own offset added, and a short jump's is
    /// rel8 + 2 + its offset.
    /// </summary>
    private static long PairedJumpLength(ReadOnlySpan<byte> code)
    {
        if (code.Length < 2)
        {
            return 0;
        }

        uint target = code[1] + 2u;
        for (int j = 2; j < 14 && j < code.Length; j++)
        {
            if (code[j] == 0xE9)
            {
                return j + 5 <= code.Length && unchecked(BinaryPrimitives.ReadUInt32LittleEndian(code[(j + 1)..]) + 5u) == target ? target : 0;
            }

            if (code[j] == 0xEB)
            {
                return j + 2 <= code.Length && code[j + 1] + 2u + (uint)j == target ? target : 0;
            }
        }

        return 0;
    }
}

/// <summary>
/// One session's vmangos scripted scans (WardenWin::LoadScriptedScans), for build 5875. Each is a READ_MEMORY whose reply decides the
/// next read, so they keep per-session state:
/// <list type="bullet">
/// <item><b>Warden and SYSTEM_INFO locate</b> (at login): read the module pointer, then its WardenSysInfo pointer, then the 36-byte
/// SYSTEM_INFO, which must report x86 on an i386/i486/Pentium processor type. A failed read or a wrong report fails the scan.</item>
/// <item><b>EndScene locate</b> (at login): g_theGxDevicePtr, its rendering API kind (OpenGL stops the chain: its EndScene is not known),
/// then three more pointers to the Direct3D EndScene. A failed read or a null device fails the scan.</item>
/// <item><b>EndScene hook check</b> (in the rotation once EndScene is known): read its first 16 bytes; an INT3 or JMP hook is logged, and a
/// JMP's target is read (200 bytes) and logged as a probable bot when the deobfuscated code looks like WRobot's. As in vmangos these hook
/// findings are log lines, not failed scans.</item>
/// </list>
/// The scans carry check id 0 and the scan action. Not thread-safe; the owning <see cref="WardenSession"/> calls it under its lock.
/// </summary>
internal sealed class WardenScriptedScans
{
    /// <summary>The EndScene bytes the hook check reads (vmangos endSceneReadSize).</summary>
    public const int EndSceneReadSize = 16;

    /// <summary>The bytes read at a JMP hook's target (vmangos codeRequestLength).</summary>
    public const int HookCodeReadSize = 200;

    private readonly Action<WardenCheck> _enqueue;
    private readonly Action<string> _log;

    /// <param name="enqueue">Queues a follow-up scan to be sent as soon as this reply is handled (vmangos Warden::EnqueueScans).</param>
    /// <param name="log">Reports a finding that is logged, not acted on.</param>
    public WardenScriptedScans(Action<WardenCheck> enqueue, Action<string> log)
    {
        _enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The client address of the loaded Warden module (0 until read).</summary>
    public uint WardenAddress { get; private set; }

    /// <summary>The client's SYSTEM_INFO, once read.</summary>
    public WardenSystemInfo? SystemInfo { get; private set; }

    /// <summary>The renderer the client reported, once read.</summary>
    public WardenRenderingApi? RenderingApi { get; private set; }

    /// <summary>The address the EndScene chain has reached; the EndScene entry once <see cref="EndSceneFound"/>.</summary>
    public uint EndSceneAddress { get; private set; }

    public bool EndSceneFound { get; private set; }

    /// <summary>The EndScene hook check for the rotation, once EndScene is found (vmangos adds it to the pool with no flags).</summary>
    public WardenCheck? HookCheck { get; private set; }

    /// <summary>The login-time scans (vmangos ScanFlags::InitialLogin): Warden locate and EndScene locate stage 1.</summary>
    public IReadOnlyList<WardenCheck> InitialLoginScans() => [WardenLocate(), EndSceneLocate1()];

    /// <summary>vmangos "Warden locate": the module pointer.</summary>
    internal WardenCheck WardenLocate() => Read("Warden locate", WardenClientOffsets.WardenModule, 4,
        (ok, _) => ok ? null : "failed to read s_moduleInterface",
        (ok, data) =>
        {
            if (ok)
            {
                WardenAddress = U32(data);
                _enqueue(SysInfoPointer(unchecked(WardenAddress + WardenClientOffsets.OfsWardenSysInfo)));
            }
        });

    /// <summary>vmangos "Intermediate sysinfo locate": the module's WardenSysInfo pointer.</summary>
    internal WardenCheck SysInfoPointer(uint address) => Read("Intermediate sysinfo locate", address, 4,
        (ok, _) => ok ? null : "failed to read warden->SysInfo",
        (ok, data) =>
        {
            if (ok)
            {
                _enqueue(SysInfo(unchecked(U32(data) + WardenClientOffsets.OfsWardenWinSysInfo)));
            }
        });

    /// <summary>vmangos "Sysinfo locate": SYSTEM_INFO itself, checked for x86 and the processor type.</summary>
    internal WardenCheck SysInfo(uint address) => Read("Sysinfo locate", address, WardenSystemInfo.Size,
        (ok, data) => ok ? WardenSystemInfo.Read(data).Problem : "failed to read SYSTEM_INFO",
        (ok, data) =>
        {
            if (ok)
            {
                SystemInfo = WardenSystemInfo.Read(data); // vmangos keeps the copy even when the check fails
            }
        });

    /// <summary>vmangos "EndScene locate stage 1": g_theGxDevicePtr; restarts the chain.</summary>
    internal WardenCheck EndSceneLocate1() => Read("EndScene locate stage 1", WardenClientOffsets.GxDevicePtr, 4,
        (ok, data) => !ok ? "failed to read g_theGxDevicePtr" : U32(data) == 0 ? "g_theGxDevicePtr is null" : null,
        (ok, data) =>
        {
            if (!ok)
            {
                return;
            }

            EndSceneFound = false;
            HookCheck = null;
            RenderingApi = null;
            EndSceneAddress = U32(data);
            if (EndSceneAddress != 0)
            {
                _enqueue(EndSceneLocateApi(EndSceneAddress));
            }
        });

    /// <summary>vmangos "EndScene locate stage 1.5": the rendering API kind; only Direct3D continues.</summary>
    internal WardenCheck EndSceneLocateApi(uint device) => Read("EndScene locate stage 1.5", unchecked(device + WardenClientOffsets.OfsApiKind), 4,
        (ok, data) => !ok ? "failed to read g_theGxDevicePtr->renderingApiKind" : (U32(data) is 0 or 1) ? null : $"unknown rendering API kind {U32(data)}",
        (ok, data) =>
        {
            if (!ok)
            {
                return;
            }

            switch (U32(data))
            {
                case 1:
                    RenderingApi = WardenRenderingApi.Direct3D;
                    _enqueue(EndSceneLocateStep("EndScene locate stage 2", unchecked(device + WardenClientOffsets.OfsDevice2), 2));
                    break;
                case 0:
                    RenderingApi = WardenRenderingApi.OpenGL; // the OpenGL EndScene is not known, so the chain stops
                    EndSceneAddress = 0;
                    break;
            }
        });

    /// <summary>vmangos "EndScene locate stage 2" to "stage 4": one pointer of the chain each; stage 4 yields EndScene.</summary>
    internal WardenCheck EndSceneLocateStep(string comment, uint address, int stage) => Read(comment, address, 4,
        (ok, _) => ok ? null : $"failed to read EndScene (stage {stage})",
        (ok, data) =>
        {
            if (!ok)
            {
                return;
            }

            EndSceneAddress = U32(data);
            switch (stage)
            {
                case 2:
                    _enqueue(EndSceneLocateStep("EndScene locate stage 3", unchecked(EndSceneAddress + WardenClientOffsets.OfsDevice3), 3));
                    break;
                case 3:
                    _enqueue(EndSceneLocateStep("EndScene locate stage 4", unchecked(EndSceneAddress + WardenClientOffsets.OfsDevice4), 4));
                    break;
                default:
                    EndSceneFound = true;
                    HookCheck = EndSceneHookCheck(EndSceneAddress);
                    _enqueue(HookCheck); // vmangos: immediately request the hook check
                    break;
            }
        });

    /// <summary>vmangos "EndScene hook check stage 1": EndScene's first bytes.</summary>
    internal WardenCheck EndSceneHookCheck(uint endScene) => Read("EndScene hook check stage 1", endScene, EndSceneReadSize,
        (ok, _) => ok ? null : "failed to read EndScene (hook check stage 1)",
        (ok, data) =>
        {
            if (!ok)
            {
                return;
            }

            WardenEndSceneInspection inspection = WardenEndScene.Inspect(data, endScene);
            switch (inspection.Hook)
            {
                case WardenEndSceneHook.Int3:
                    _log($"INT3 EndScene hook detected (NOP count {inspection.NopCount})");
                    break;
                case WardenEndSceneHook.Jump:
                    _log($"JMP EndScene hook detected (NOP count {inspection.NopCount}, target 0x{inspection.JumpTarget:X8})");
                    _enqueue(EndSceneHookValidate(inspection.JumpTarget));
                    break;
            }
        });

    /// <summary>vmangos "EndScene hook validate scan": the code at a JMP hook's target.</summary>
    internal WardenCheck EndSceneHookValidate(uint target) => Read("EndScene hook validate scan", target, HookCodeReadSize,
        (ok, _) => ok ? null : "failed to read EndScene hook code",
        (ok, data) =>
        {
            if (ok && WardenEndScene.IsSuspiciousHook(data))
            {
                _log("suspicious EndScene hook code: probable bot");
            }
        });

    private static WardenCheck Read(string comment, uint address, int length, WardenScriptedEvaluate evaluate, WardenScriptedApply apply)
        => WardenCheck.Scripted(comment, address, length, new WardenScriptedRead(evaluate, apply));

    private static uint U32(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32LittleEndian(data);
}
