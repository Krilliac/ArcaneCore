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
    public void ADirectoryDeletedAfterConstruction_IsRecreated_InsteadOfCountingEveryPortAsHeld()
    {
        using var reservations = new PortReservations(_root);
        Assert.True(reservations.Enabled);
        Directory.Delete(_root, recursive: true);

        Assert.True(reservations.TryReserve(12345));
        using var other = new PortReservations(_root);
        Assert.False(other.TryReserve(12345)); // the first claim is a real lock in the recreated directory
    }

    [Fact]
    public void FixturePortChoice_SkipsHeldPorts_AndSurvivesADeletedLockDirectory()
    {
        using var holder = new PortReservations(_root);
        using var fixturePorts = new PortReservations(_root);
        Assert.True(holder.TryReserve(13001));
        int candidate = 13000;

        int first = ClusterProcessFixture.PickFreePort(fixturePorts, static _ => true, () => candidate++);
        Assert.Equal(13000, first);
        int second = ClusterProcessFixture.PickFreePort(fixturePorts, static _ => true, () => candidate++);
        Assert.Equal(13002, second); // 13001 is held by the other holder and is skipped

        // An open lock file keeps a directory from being deleted on Windows: release every claim, then remove the directory under a still-live instance.
        using var survivor = new PortReservations(_root);
        holder.Dispose();
        fixturePorts.Dispose();
        Directory.Delete(_root, recursive: true);
        int attempts = 0;
        int third = ClusterProcessFixture.PickFreePort(survivor, static _ => true, () => { attempts++; return 13100; });
        Assert.Equal(13100, third);
        Assert.Equal(1, attempts); // one candidate: a vanished directory no longer burns the whole range
    }

    [Fact]
    public void FixturePortChoice_GivesUp_AfterTheRange_WhenNoCandidateCanListen()
    {
        using var ports = new PortReservations(_root);
        int attempts = 0;

        Assert.Throws<InvalidOperationException>(() => ClusterProcessFixture.PickFreePort(ports, static _ => false, () => { attempts++; return 14000; }));
        Assert.Equal(ClusterProcessFixture.PortRangeSize, attempts);
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
