# Attaches a Win32 debugger to the live Realm and World and logs what the managed log cannot see: native exceptions, breakpoints,
# process exits with their exit code, and a count of CLR first-chance exceptions (0xE0434352; the first 10 and then every 500th are
# logged). It never breaks into the daemons: every event is continued, and KillOnExit is off, so closing this console detaches.
#   pwsh -NoExit -File tools/ops/diag/attach-debugger.ps1 [-RealmPid <pid>] [-WorldPid <pid>] [-LogPath <file>] [-LiveHome <dir>]
#        [-SupervisorPid <pid> [-PollSeconds <n>]] [-WindowTitle <text>]
# The PIDs default to the one running ArcaneCore.Realm / ArcaneCore.World; the log to <LiveHome>\logs\win32-debugger.log
# (ARCANECORE_LIVE_HOME). The debugger sees only the exception code: name the CLR types with tools/ops/diag/exception-probe.
# Watch mode (-SupervisorPid, used by tools/ops/live/start-cluster.ps1): attaches to the cluster host supervisor itself and then follows
# it. Every -PollSeconds it lists the ArcaneCore.* processes whose parent is the supervisor and attaches to each one it has not attached
# to yet, so a child the supervisor relaunches after a crash, a hang kill or a restart request is picked up with its new PID. No default
# PID lookup happens in this mode (Get-LiveDaemon would find the supervisor itself, which is a World executable). It ends when the
# supervisor and every attached process are gone. -WindowTitle replaces the console title.
# WARNING: while attached, a debug event stops the whole target until this script continues it (docs/ops/tooling.md). Under the cluster
# supervisor a child that sends no heartbeat for Cluster:Supervisor:Processes:<name>:HangTimeoutSeconds (default 120 s) is drained and killed:
# if this console stops continuing events (a selection in it, the machine paging) the child is killed as hung.
param(
    [int]$RealmPid,
    [int]$WorldPid,
    [string]$LogPath,
    [string]$LiveHome,
    [int]$SupervisorPid,
    [ValidateRange(1, 60)][int]$PollSeconds = 2,
    [string]$WindowTitle
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\live\LiveEnv.ps1')
if (-not $LogPath) { $LogPath = Join-Path (Resolve-LiveHome $LiveHome) 'logs\win32-debugger.log' }
if (-not $SupervisorPid) {
    if (-not $RealmPid) { $RealmPid = (Get-LiveDaemon Realm)?.Id ?? 0 }
    if (-not $WorldPid) { $WorldPid = (Get-LiveDaemon World)?.Id ?? 0 }
    if (-not $RealmPid -and -not $WorldPid) { throw 'Neither ArcaneCore.Realm nor ArcaneCore.World is running; pass -RealmPid/-WorldPid.' }
} elseif (-not (Get-Process -Id $SupervisorPid -ErrorAction SilentlyContinue)) {
    throw "The supervisor (pid $SupervisorPid) is not running."
}

$target = if ($SupervisorPid) { "Supervisor PID $SupervisorPid and its children" } else { "World PID $WorldPid + Realm PID $RealmPid" }
try { $Host.UI.RawUI.WindowTitle = if ($WindowTitle) { $WindowTitle } else { "ArcaneCore Live Win32 Debugger [$target]" } } catch {}
Write-Host "=== ArcaneCore Live Win32 Debugger (Attached to $target) ===" -ForegroundColor Cyan
Write-Host "Log file: $LogPath" -ForegroundColor Gray
Write-Host "--------------------------------------------------------------------------------" -ForegroundColor DarkGray

$code = @"
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class LiveWin32Debugger
{
    private const uint DBG_CONTINUE = 0x00010002;
    private const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;

    private const uint EXCEPTION_DEBUG_EVENT = 1;
    private const uint CREATE_THREAD_DEBUG_EVENT = 2;
    private const uint CREATE_PROCESS_DEBUG_EVENT = 3;
    private const uint EXIT_THREAD_DEBUG_EVENT = 4;
    private const uint EXIT_PROCESS_DEBUG_EVENT = 5;
    private const uint LOAD_DLL_DEBUG_EVENT = 6;
    private const uint UNLOAD_DLL_DEBUG_EVENT = 7;
    private const uint OUTPUT_DEBUG_STRING_EVENT = 8;
    private const uint RIP_EVENT = 9;

    private const uint EXCEPTION_BREAKPOINT = 0x80000003;
    private const uint EXCEPTION_SINGLE_STEP = 0x80000004;
    private const uint MS_VC_EXCEPTION = 0x406D1388;
    private const uint DBG_PRINTEXCEPTION_C = 0x40010006;
    private const uint DBG_PRINTEXCEPTION_WIDE_C = 0x4001000A;
    private const uint CLR_EXCEPTION = 0xE0434352;

    [StructLayout(LayoutKind.Explicit, Size = 176)]
    private struct DEBUG_EVENT
    {
        [FieldOffset(0)] public uint dwDebugEventCode;
        [FieldOffset(4)] public uint dwProcessId;
        [FieldOffset(8)] public uint dwThreadId;

        [FieldOffset(16)] public uint ExceptionCode;
        [FieldOffset(20)] public uint ExceptionFlags;
        [FieldOffset(24)] public IntPtr ExceptionRecord;
        [FieldOffset(32)] public IntPtr ExceptionAddress;
        [FieldOffset(40)] public uint NumberParameters;
        [FieldOffset(48)] public ulong ExceptionInfo0;
        [FieldOffset(56)] public ulong ExceptionInfo1;
        [FieldOffset(168)] public uint dwFirstChance;

        [FieldOffset(16)] public IntPtr CreateProcess_hFile;
        [FieldOffset(24)] public IntPtr CreateProcess_hProcess;
        [FieldOffset(32)] public IntPtr CreateProcess_hThread;
        [FieldOffset(40)] public IntPtr CreateProcess_lpBaseOfImage;

        [FieldOffset(16)] public IntPtr LoadDll_hFile;
        [FieldOffset(24)] public IntPtr LoadDll_lpBaseOfDll;

        [FieldOffset(16)] public uint ExitProcess_dwExitCode;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugActiveProcess(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugSetProcessKillOnExit(bool KillOnExit);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WaitForDebugEvent(out DEBUG_EVENT lpDebugEvent, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ContinueDebugEvent(uint dwProcessId, uint dwThreadId, uint dwContinueStatus);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static readonly object LogLock = new object();

    public static void DisableQuickEdit()
    {
        try
        {
            IntPtr h = GetStdHandle(-10);
            if (GetConsoleMode(h, out uint mode))
            {
                SetConsoleMode(h, (mode & ~0x0040u) | 0x0080u);
            }
        }
        catch { }
    }

    private static void Log(string logPath, string label, string message)
    {
        if (string.IsNullOrEmpty(logPath)) return;
        string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}", DateTime.UtcNow, label, message, Environment.NewLine);
        lock (LogLock)
        {
            try { File.AppendAllText(logPath, line, Encoding.UTF8); } catch { }
            try { Console.Write(line); } catch { }
        }
    }

    public static Thread AttachAsync(int pid, string label, string logPath)
    {
        var thread = new Thread(() => RunDebugLoop((uint)pid, label, logPath))
        {
            IsBackground = true,
            Name = "Win32Debugger-" + label
        };
        thread.Start();
        return thread;
    }

    private static void RunDebugLoop(uint pid, string label, string logPath)
    {
        if (!DebugActiveProcess(pid))
        {
            int err = Marshal.GetLastWin32Error();
            Log(logPath, label, string.Format("Failed to attach to PID {0} (Win32 error {1})", pid, err));
            return;
        }
        DebugSetProcessKillOnExit(false);
        Log(logPath, label, string.Format("Successfully attached Win32 debugger to PID {0} (KillOnExit=false)", pid));

        long clrFirstChanceCount = 0;
        while (true)
        {
            if (!WaitForDebugEvent(out DEBUG_EVENT evt, 1000))
                continue;

            uint status = DBG_CONTINUE;
            string pendingMessage = null;
            bool exitProcess = false;

            switch (evt.dwDebugEventCode)
            {
                case CREATE_PROCESS_DEBUG_EVENT:
                    if (evt.CreateProcess_hFile != IntPtr.Zero)
                        CloseHandle(evt.CreateProcess_hFile);
                    pendingMessage = string.Format("CREATE_PROCESS_DEBUG_EVENT PID={0} BaseOfImage=0x{1:X16}", evt.dwProcessId, evt.CreateProcess_lpBaseOfImage.ToInt64());
                    break;

                case LOAD_DLL_DEBUG_EVENT:
                    if (evt.LoadDll_hFile != IntPtr.Zero)
                        CloseHandle(evt.LoadDll_hFile);
                    break;

                case EXCEPTION_DEBUG_EVENT:
                    uint code = evt.ExceptionCode;
                    bool firstChance = evt.dwFirstChance != 0;
                    long addr = evt.ExceptionAddress.ToInt64();

                    if (code == EXCEPTION_BREAKPOINT || code == EXCEPTION_SINGLE_STEP)
                    {
                        pendingMessage = string.Format("BREAKPOINT/STEP (0x{0:X8}) TID={1} Addr=0x{2:X16} -> DBG_CONTINUE", code, evt.dwThreadId, addr);
                        status = DBG_CONTINUE;
                    }
                    else if (code == MS_VC_EXCEPTION || code == DBG_PRINTEXCEPTION_C || code == DBG_PRINTEXCEPTION_WIDE_C)
                    {
                        status = DBG_EXCEPTION_NOT_HANDLED;
                    }
                    else if (code == CLR_EXCEPTION && firstChance)
                    {
                        long count = ++clrFirstChanceCount;
                        if (count <= 10 || count % 500 == 0)
                        {
                            pendingMessage = string.Format("CLR first-chance exception #{0} (0xE0434352) TID={1} Addr=0x{2:X16}", count, evt.dwThreadId, addr);
                        }
                        status = DBG_EXCEPTION_NOT_HANDLED;
                    }
                    else
                    {
                        pendingMessage = string.Format("EXCEPTION 0x{0:X8} (FirstChance={1}) TID={2} Addr=0x{3:X16} Info0=0x{4:X16}", code, firstChance, evt.dwThreadId, addr, evt.ExceptionInfo0);
                        status = DBG_EXCEPTION_NOT_HANDLED;
                    }
                    break;

                case EXIT_PROCESS_DEBUG_EVENT:
                    pendingMessage = string.Format("EXIT_PROCESS_DEBUG_EVENT PID={0} ExitCode={1}", evt.dwProcessId, evt.ExitProcess_dwExitCode);
                    exitProcess = true;
                    break;
            }

            ContinueDebugEvent(evt.dwProcessId, evt.dwThreadId, status);
            if (pendingMessage != null)
            {
                Log(logPath, label, pendingMessage);
            }
            if (exitProcess)
            {
                return;
            }
        }
    }
}
"@

Add-Type -TypeDefinition $code -Language CSharp
[LiveWin32Debugger]::DisableQuickEdit()
$threads = @()
if ($RealmPid) { $threads += [LiveWin32Debugger]::AttachAsync($RealmPid, "Realm:$RealmPid", $LogPath) }
if ($WorldPid) { $threads += [LiveWin32Debugger]::AttachAsync($WorldPid, "World:$WorldPid", $LogPath) }
if ($SupervisorPid) {
    # Watch mode. $attached maps a pid to its debug thread; a thread that ended while its process still runs (the attach was refused) is
    # retried a few times, then left alone so a refusal does not spam the log every poll.
    $attached = @{}
    $attempts = @{}
    $attached[$SupervisorPid] = [LiveWin32Debugger]::AttachAsync($SupervisorPid, "Supervisor:$SupervisorPid", $LogPath)
    while ($true) {
        $supervisorUp = $null -ne (Get-Process -Id $SupervisorPid -ErrorAction SilentlyContinue)
        if ($supervisorUp) {
            $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$SupervisorPid" -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'ArcaneCore.*' })
            foreach ($child in $children) {
                $childPid = [int]$child.ProcessId
                $known = $attached.ContainsKey($childPid)
                if ($known -and $attached[$childPid].IsAlive) { continue }
                if ($known -and -not (Get-Process -Id $childPid -ErrorAction SilentlyContinue)) { $attached.Remove($childPid); $known = $false }
                $tries = if ($attempts.ContainsKey($childPid)) { $attempts[$childPid] } else { 0 }
                if ($known -and $tries -ge 3) { continue }
                $attempts[$childPid] = $tries + 1
                $label = ($child.Name -replace '^ArcaneCore\.', '' -replace '\.exe$', '') + ":$childPid"
                $attached[$childPid] = [LiveWin32Debugger]::AttachAsync($childPid, $label, $LogPath)
                Write-Host "[watch] supervisor child $($child.Name) pid ${childPid}: attaching" -ForegroundColor Cyan
            }
        }
        $live = @($attached.Values | Where-Object IsAlive)
        if (-not $supervisorUp -and $live.Count -eq 0) { break }
        Start-Sleep -Seconds $PollSeconds
    }
    Write-Host '[watch] the supervisor and every attached process are gone; detached.' -ForegroundColor Cyan
} else {
    while (@($threads | Where-Object IsAlive).Count -gt 0) {
        Start-Sleep -Seconds 5
    }
}
