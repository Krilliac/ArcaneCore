using ArcaneCore.Kernel.Ops.ServerConsole;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

public sealed class ServerConsoleTitleTests
{
    [Theory]
    [InlineData("worldserver", "8085", "ArcaneCore worldserver :8085")]
    [InlineData("realmserver", " 3724 ", "ArcaneCore realmserver :3724")]
    [InlineData("worldserver", null, "ArcaneCore worldserver")]
    [InlineData("realmserver", "", "ArcaneCore realmserver")]
    public void TheTitleNamesTheDaemonAndItsPort(string daemon, string? port, string expected)
        => Assert.Equal(expected, ServerConsoleTitle.Compose(daemon, port));

    [Fact]
    public void SettingTheTitleWithoutAConsoleWindowDoesNotThrow()
        => ServerConsoleTitle.TrySet("worldserver", "8085"); // the test host's output is redirected: skipped, never thrown
}
