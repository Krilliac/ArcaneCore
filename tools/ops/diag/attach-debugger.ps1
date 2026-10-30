# Attaches a Win32 debugger to the live Realm and World and logs what the managed log cannot see: native exceptions, breakpoints,
# process exits with their exit code, and a count of CLR first-chance exceptions (0xE0434352; the first 10 and then every 500th are
# logged). It never breaks into the daemons: every event is continued, and KillOnExit is off, so closing this console detaches.
#   pwsh -NoExit -File tools/ops/diag/attach-debugger.ps1 [-RealmPid <pid>] [-WorldPid <pid>] [-LogPath <file>] [-LiveHome <dir>]
#        [-SupervisorPid <pid> [-PollSeconds <n>]] [-WindowTitle <text>]
# The PIDs default to the one running ArcaneCore.Realm / ArcaneCore.World; the log to <LiveHome>\logs\win32-debugger.log
# (ARCANECORE_LIVE_HOME). The debugger sees only the exception code: name the CLR types with tools/ops/diag/exception-probe.
# Watch mode (-SupervisorPid, used by tools/ops/live/start-cluster.ps1): follows the cluster host supervisor but NEVER attaches to it (a
# stopped supervisor stops reading its Merged children's stdout, which blocks every child). Every -PollSeconds it lists the ArcaneCore.*
# processes whose parent is the supervisor and attaches to each one it has not attached to yet, so a child the supervisor relaunches
# after a crash, a hang kill or a restart request is picked up with its new PID. No default PID lookup happens in this mode
# (Get-LiveDaemon would find the supervisor itself, which is a World executable). It ends when the supervisor and every attached process
# are gone. -WindowTitle replaces the console title.
# WARNING: while attached, a debug event stops the whole target until this script continues it (docs/ops/tooling.md). Each debug loop
# continues its event at once and only enqueues its log line; the console and the log file are written by separate threads from bounded
# queues, and lines are dropped (and counted: "N lines dropped") when a queue is full, so a stalled console (text selected in it, the
# machine paging) does not hold any target stopped. The remaining risk: an exception-heavy target is slowed by one whole-process stop per
# exception (about 0.2 ms each); under the cluster supervisor with HangCapture=Dump, a dump request for a child that is stopped by this
# debugger can wait up to Cluster:Supervisor:HangCaptureTimeoutSeconds before the supervisor kills it.
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

$target = if ($SupervisorPid) { "the children of supervisor PID $SupervisorPid (not the supervisor itself)" } else { "World PID $WorldPid + Realm PID $RealmPid" }
try { $Host.UI.RawUI.WindowTitle = if ($WindowTitle) { $WindowTitle } else { "ArcaneCore Live Win32 Debugger [$target]" } } catch {}
Write-Host "=== ArcaneCore Live Win32 Debugger (Attached to $target) ===" -ForegroundColor Cyan
Write-Host "Log file: $LogPath" -ForegroundColor Gray
Write-Host "--------------------------------------------------------------------------------" -ForegroundColor DarkGray

$code = @"
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public sealed class LiveWin32DebuggerV2
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

    // Logging never blocks a debug loop: Log only formats a line and TryAdd()s it to two bounded queues (console, file), each drained by its
    // own writer thread, so a stalled console cannot stop the file log and neither can hold a target stopped. A full queue drops the line and
    // counts it.
    private const int QueueCapacity = 8192;
    // All of this state belongs to one instance, created by one run of the script: the Add-Type'd type outlives the script in a
    // 'pwsh -NoExit' window, so a second run must get its own queues, threads, log file and counters.
    private readonly string _logFile;
    private readonly BlockingCollection<string> _consoleQueue = new BlockingCollection<string>(QueueCapacity);
    private readonly BlockingCollection<string> _fileQueue = new BlockingCollection<string>(QueueCapacity);
    private readonly Thread _consoleThread;
    private readonly Thread _fileThread;
    private long _consolePendingDrops;
    private long _filePendingDrops;
    private long _consoleDropsTotal;
    private long _fileDropsTotal;

    public long ConsoleLinesDropped { get { return Interlocked.Read(ref _consoleDropsTotal); } }
    public long FileLinesDropped { get { return Interlocked.Read(ref _fileDropsTotal); } }

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

    public LiveWin32DebuggerV2(string logPath)
    {
        _logFile = string.IsNullOrEmpty(logPath) ? null : logPath;
        _consoleThread = new Thread(ConsoleWriterLoop) { IsBackground = true, Name = "Win32Debugger-console" };
        _fileThread = new Thread(FileWriterLoop) { IsBackground = true, Name = "Win32Debugger-file" };
        _consoleThread.Start();
        _fileThread.Start();
    }

    // Waits up to timeoutMs for each writer to drain what is queued (a stalled console is abandoned, not waited on), then records the
    // drop totals in the log file.
    public void Shutdown(int timeoutMs)
    {
        try { _fileQueue.CompleteAdding(); } catch { }
        try { _consoleQueue.CompleteAdding(); } catch { }
        _fileThread.Join(timeoutMs);
        _consoleThread.Join(timeoutMs);
        if (_logFile != null && (ConsoleLinesDropped > 0 || FileLinesDropped > 0))
        {
            try
            {
                File.AppendAllText(_logFile, string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [debugger] {1} console lines and {2} file lines dropped in total{3}",
                    DateTime.UtcNow, ConsoleLinesDropped, FileLinesDropped, Environment.NewLine), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    public void Note(string label, string message)
    {
        Log(label, message);
    }

    private void Enqueue(BlockingCollection<string> queue, string line, ref long pendingDrops, ref long dropsTotal)
    {
        try
        {
            if (queue.TryAdd(line)) return;
        }
        catch (InvalidOperationException)
        {
            return; // completed: shutting down
        }
        Interlocked.Increment(ref pendingDrops);
        Interlocked.Increment(ref dropsTotal);
    }

    private void Log(string label, string message)
    {
        string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}", DateTime.UtcNow, label, message, Environment.NewLine);
        if (_logFile != null) Enqueue(_fileQueue, line, ref _filePendingDrops, ref _fileDropsTotal);
        Enqueue(_consoleQueue, line, ref _consolePendingDrops, ref _consoleDropsTotal);
    }

    private static string DropNotice(long dropped, string what)
    {
        return string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [debugger] {1} lines dropped ({2}){3}", DateTime.UtcNow, dropped, what, Environment.NewLine);
    }

    private void ConsoleWriterLoop()
    {
        foreach (string line in _consoleQueue.GetConsumingEnumerable())
        {
            try
            {
                long dropped = Interlocked.Exchange(ref _consolePendingDrops, 0);
                if (dropped > 0) Console.Write(DropNotice(dropped, "the console was not keeping up"));
                Console.Write(line);
            }
            catch { }
        }
    }

    private void FileWriterLoop()
    {
        StreamWriter writer = null;
        if (_logFile != null)
        {
            try
            {
                string dir = Path.GetDirectoryName(_logFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                writer = new StreamWriter(new FileStream(_logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                writer = null;
                string notice = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [debugger] cannot open the log file {1} ({2}); its lines are counted as dropped{3}", DateTime.UtcNow, _logFile, ex.Message, Environment.NewLine);
                try { _consoleQueue.TryAdd(notice); } catch (InvalidOperationException) { }
            }
        }
        long reportedConsoleDrops = 0;
        long lastReportTicks = Environment.TickCount64;
        while (!_fileQueue.IsCompleted)
        {
            string line;
            if (!_fileQueue.TryTake(out line, 500))
            {
                try { if (writer != null) writer.Flush(); } catch { }
                continue;
            }
            try
            {
                if (writer == null)
                {
                    Interlocked.Increment(ref _fileDropsTotal);
                    continue;
                }
                long dropped = Interlocked.Exchange(ref _filePendingDrops, 0);
                if (dropped > 0) writer.Write(DropNotice(dropped, "the log file was not keeping up"));
                writer.Write(line);
                // The console writer cannot report while it is blocked, so the file says how many console lines were lost so far.
                long consoleDrops = ConsoleLinesDropped;
                if (consoleDrops != reportedConsoleDrops && Environment.TickCount64 - lastReportTicks >= 1000)
                {
                    writer.Write(DropNotice(consoleDrops - reportedConsoleDrops, "the console is not draining; not shown there"));
                    reportedConsoleDrops = consoleDrops;
                    lastReportTicks = Environment.TickCount64;
                }
                if (_fileQueue.Count == 0) writer.Flush();
            }
            catch { Interlocked.Increment(ref _fileDropsTotal); }
        }
        try { if (writer != null) { writer.Flush(); writer.Dispose(); } } catch { }
    }

    public Thread AttachAsync(int pid, string label)
    {
        var thread = new Thread(() => RunDebugLoop((uint)pid, label))
        {
            IsBackground = true,
            Name = "Win32Debugger-" + label
        };
        thread.Start();
        return thread;
    }

    private void RunDebugLoop(uint pid, string label)
    {
        if (!DebugActiveProcess(pid))
        {
            int err = Marshal.GetLastWin32Error();
            Log(label, string.Format("Failed to attach to PID {0} (Win32 error {1})", pid, err));
            return;
        }
        DebugSetProcessKillOnExit(false);
        Log(label, string.Format("Successfully attached Win32 debugger to PID {0} (KillOnExit=false)", pid));

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
                Log(label, pendingMessage);
            }
            if (exitProcess)
            {
                return;
            }
        }
    }
}
"@

# The type outlives this script in the session (pwsh -NoExit), and Add-Type of the same name fails; a changed C# body needs a new type name.
if (-not ('LiveWin32DebuggerV2' -as [type])) { Add-Type -TypeDefinition $code -Language CSharp }
[LiveWin32DebuggerV2]::DisableQuickEdit()
$debugLog = [LiveWin32DebuggerV2]::new($LogPath)   # per run: its own queues, writer threads, log file and drop counters
$threads = @()
if ($RealmPid) { $threads += $debugLog.AttachAsync($RealmPid, "Realm:$RealmPid") }
if ($WorldPid) { $threads += $debugLog.AttachAsync($WorldPid, "World:$WorldPid") }
if ($SupervisorPid) {
    # Watch mode. $attached maps a pid to its debug thread; a thread that ended while its process still runs (the attach was refused) is
    # retried a few times, then left alone so a refusal does not spam the log every poll.
    $attached = @{}
    $attempts = @{}
    # The supervisor itself is never attached: a stopped supervisor stops reading its Merged children's stdout, which blocks them all.
    # A reused PID is not the supervisor: remember its start time and treat a different one as gone.
    $supervisorStart = (Get-Process -Id $SupervisorPid -ErrorAction SilentlyContinue).StartTime
    while ($true) {
        $supervisorProcess = Get-Process -Id $SupervisorPid -ErrorAction SilentlyContinue
        $supervisorUp = $null -ne $supervisorProcess -and $supervisorProcess.StartTime -eq $supervisorStart
        foreach ($gonePid in @($attempts.Keys) + @($attached.Keys) | Select-Object -Unique) {
            if (-not $attached.ContainsKey($gonePid) -or -not $attached[$gonePid].IsAlive) {
                if (-not (Get-Process -Id $gonePid -ErrorAction SilentlyContinue)) { $attempts.Remove($gonePid); $attached.Remove($gonePid) }
            }
        }
        if ($supervisorUp) {
            $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$SupervisorPid" -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'ArcaneCore.*' })
            foreach ($child in $children) {
                $childPid = [int]$child.ProcessId
                if ($childPid -eq $SupervisorPid) { continue }
                $known = $attached.ContainsKey($childPid)
                if ($known -and $attached[$childPid].IsAlive) { continue }
                if ($known -and -not (Get-Process -Id $childPid -ErrorAction SilentlyContinue)) { $attached.Remove($childPid); $known = $false }
                $tries = if ($attempts.ContainsKey($childPid)) { $attempts[$childPid] } else { 0 }
                if ($known -and $tries -ge 3) { continue }
                $attempts[$childPid] = $tries + 1
                $label = ($child.Name -replace '^ArcaneCore\.', '' -replace '\.exe$', '') + ":$childPid"
                $attached[$childPid] = $debugLog.AttachAsync($childPid, $label)
                $debugLog.Note('watch', "supervisor child $($child.Name) pid ${childPid}: attaching")
            }
        }
        $live = @($attached.Values | Where-Object IsAlive)
        if (-not $supervisorUp -and $live.Count -eq 0) { break }
        Start-Sleep -Seconds $PollSeconds
    }
    $debugLog.Note('watch', 'the supervisor and every attached process are gone; detached.')
} else {
    while (@($threads | Where-Object IsAlive).Count -gt 0) {
        Start-Sleep -Seconds 5
    }
}
$debugLog.Shutdown(3000)
if ($debugLog.ConsoleLinesDropped -gt 0 -or $debugLog.FileLinesDropped -gt 0) {
    Write-Host "$($debugLog.ConsoleLinesDropped) console lines and $($debugLog.FileLinesDropped) log-file lines dropped in total (a queue was full)." -ForegroundColor Yellow
}
