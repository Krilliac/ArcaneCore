using Xunit;

namespace ArcaneCore.Cluster.E2E.Tests;

/// <summary>The lock-file port reservation of the cluster fixture. No child process, database or listener is involved: only files in a private temp directory.</summary>
public sealed class PortReservationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcane-port-reservations-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        else if (File.Exists(_root))
        {
            File.Delete(_root);
        }
    }

    [Fact]
    public void TwoHoldersOfOneDirectory_CannotHoldOnePort_AndAReleasedPortCanBeClaimedAgain()
    {
        using var first = new PortReservations(_root);
        var second = new PortReservations(_root);
        try
        {
            Assert.True(first.Enabled);
            Assert.True(second.Enabled);
            Assert.True(first.TryReserve(12345));
            Assert.False(second.TryReserve(12345));
            Assert.True(second.TryReserve(12346)); // another port is free

            first.Dispose();
            Assert.True(second.TryReserve(12345));
        }
        finally
        {
            second.Dispose();
        }
    }

    [Fact]
    public void AFileWhereTheDirectoryBelongs_DisablesReservation_InsteadOfRejectingEveryPort()
    {
        File.WriteAllText(_root, "not a directory");
        using var reservations = new PortReservations(_root);

        Assert.False(reservations.Enabled);
        Assert.True(reservations.TryReserve(12345));
        Assert.True(reservations.TryReserve(12345)); // nothing is held, so nothing conflicts
    }
}
