using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>Destination checks of <c>client.read</c> for the Windows path forms that name something other than a plain file.</summary>
public sealed class ClientOutputPathTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [WindowsTheory]
    [InlineData(@"\.\PHYSICALDRIVE0")]
    [InlineData(@"\?\C:\temp\x.bin")]
    [InlineData(@"\server\share\x.bin")]
    [InlineData("//server/share/x.bin")]
    public void RefusesDeviceAndNetworkPaths(string requested)
    {
        CommandException e = Assert.Throws<CommandException>(() => ClientOutputPath.Check(requested, overwrite: true, []));
        Assert.Equal(CommandException.BadArguments, e.Code);
    }

    [WindowsFact]
    public void RefusesAlternateDataStreams_AndShortNames()
    {
        string stream = Path.Combine(_dir.Path, "x.bin") + ":hidden";
        Assert.Throws<CommandException>(() => ClientOutputPath.Check(stream, overwrite: true, []));
        string shortName = Path.Combine(_dir.Path, "PROGRA~1", "x.bin");
        Assert.Throws<CommandException>(() => ClientOutputPath.Check(shortName, overwrite: true, []));
    }

    [Fact]
    public void AcceptsAnOrdinaryNewFile_AndRefusesAnExistingOneWithoutOverwrite()
    {
        string target = Path.Combine(_dir.Path, "ok.bin");
        Assert.Equal(Path.GetFullPath(target), ClientOutputPath.Check(target, overwrite: false, []));
        File.WriteAllBytes(target, [1]);
        Assert.Throws<CommandException>(() => ClientOutputPath.Check(target, overwrite: false, []));
        Assert.Equal(Path.GetFullPath(target), ClientOutputPath.Check(target, overwrite: true, []));
    }
}

/// <summary>A fact for Windows-only path forms; skipped, visibly, elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows path forms.";
        }
    }
}

/// <summary>A theory for Windows-only path forms; skipped, visibly, elsewhere.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows path forms.";
        }
    }
}
