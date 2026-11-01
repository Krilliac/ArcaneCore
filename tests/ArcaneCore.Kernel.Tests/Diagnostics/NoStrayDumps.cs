using System.Reflection;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using Xunit.Sdk;

[assembly: ArcaneCore.Kernel.Tests.Diagnostics.NoStrayDumps]

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>
/// Fails any test after which a <c>.dmp</c> file sits where the in-process crash dump would put one for this test host (the default
/// <c>dumps</c> folder beside the test assembly, or beside <see cref="CrashDialog.LogFilePath"/>). A test that installs a <see cref="CrashHandler"/>
/// and reaches a crash path must use <see cref="DumpFree.Options"/> (Mode Off) or a stub <see cref="CrashHandler.DumpService"/>; the default Mode is
/// Full and would write a dump of the test host. Applied to the whole assembly. The file is deleted so one stray dump fails one test, not all.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class NoStrayDumpsAttribute : BeforeAfterTestAttribute
{
    public override void After(MethodInfo methodUnderTest)
    {
        List<string> stray = [];
        foreach (string directory in Directories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(directory, "*.dmp"))
            {
                stray.Add(file);
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Reported below; a locked file is removed by the next run's clean.
                }
            }
        }

        if (stray.Count > 0)
        {
            throw new InvalidOperationException("the test " + methodUnderTest.DeclaringType?.Name + "." + methodUnderTest.Name
                + " left a crash dump of the test host: " + string.Join(", ", stray) + ". Use DumpFree.Options (Diagnostics:CrashDump:Mode = Off) for any CrashHandler a test installs.");
        }
    }

    private static IEnumerable<string> Directories()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "dumps");
        yield return CrashDumpService.ResolveDirectory(new CrashDumpOptions(), CrashDialog.LogFilePath);
    }
}

/// <summary>Diagnostics options for tests that install a crash handler: the in-process crash dump is Off, so no test writes a dump of the test host.</summary>
public static class DumpFree
{
    public static DiagnosticsOptions Options(Action<DiagnosticsOptions>? configure = null)
    {
        var options = new DiagnosticsOptions();
        options.CrashDump.Mode = CrashDumpMode.Off;
        configure?.Invoke(options);
        return options;
    }
}
