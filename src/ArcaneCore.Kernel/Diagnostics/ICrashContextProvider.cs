using System.Text;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// A daemon-specific contributor to the crash report (the world daemon adds its tick number, uptime and
/// online count). <see cref="Describe"/> runs on whatever thread is crashing, possibly with locks held
/// elsewhere, so it must read volatile state without blocking and without allocating more than the text
/// it appends; an exception it throws is caught and noted in the report.
/// </summary>
public interface ICrashContextProvider
{
    /// <summary>The report heading of this contributor.</summary>
    string Name { get; }

    /// <summary>Append one line per fact to <paramref name="report"/>.</summary>
    void Describe(StringBuilder report);
}
