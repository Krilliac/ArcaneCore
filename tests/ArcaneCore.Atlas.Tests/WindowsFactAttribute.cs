using Xunit;

namespace ArcaneCore.Atlas.Tests;

/// <summary>
/// A fact that runs only on Windows; skipped, visibly, elsewhere. The one definition for the whole test project (every test
/// namespace sits under this one, so it is found without a using): the cases need something only Windows has, such as its path
/// forms, junctions made by <c>mklink /J</c>, a file held open against deletion, a case-insensitive file system, or named pipes
/// with access lists.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only: needs Windows path forms, junctions, file locks, a case-insensitive file system or named pipes with access lists.";
        }
    }
}
