<#
.SYNOPSIS
  Dot-sourced by the dev-runner window scripts: run a command in this console, show its output live and also append
  it to a log file, and SURVIVE a Ctrl+C / Ctrl+Break aimed at the console.

.DESCRIPTION
  A PowerShell pipeline (`cmd | Tee-Object`) stops itself when it receives Ctrl+C / Ctrl+Break, which can cut the
  daemon's output off (or take it down) before it has finished shutting down and saving. This wrapper ignores the
  event itself and only waits for the child, which receives the same event from the console and shuts down
  gracefully; the shutdown lines therefore still reach the window and the log.
#>
if (-not ('DevRunnerTee' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Diagnostics;
using System.IO;

public static class DevRunnerTee
{
    public static int Run(string exe, string arguments, string logPath)
    {
        Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; };
        ProcessStartInfo info = new ProcessStartInfo(exe, arguments);
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using (Process process = Process.Start(info))
        using (StreamWriter log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)))
        {
            log.AutoFlush = true;
            object gate = new object();
            DataReceivedEventHandler handler = delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null) { return; }
                lock (gate)
                {
                    Console.WriteLine(e.Data);
                    log.WriteLine(e.Data);
                }
            };
            process.OutputDataReceived += handler;
            process.ErrorDataReceived += handler;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            process.WaitForExit(); // the second call returns once the redirected output has been fully read
            return process.ExitCode;
        }
    }
}
"@
}

function Invoke-DevTee([string]$Exe, [string]$Arguments, [string]$LogPath) {
    return [DevRunnerTee]::Run($Exe, $Arguments, $LogPath)
}
