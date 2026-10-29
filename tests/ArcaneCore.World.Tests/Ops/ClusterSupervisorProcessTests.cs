using ArcaneCore.Kernel.Ops;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// The World executable in supervisor mode (<c>Cluster:Supervisor:Enabled</c>), end to end. Each test runs the real
/// <c>ArcaneCore.World.dll</c>; the only child the supervisor may ever start is <c>dotnet --version</c>, and every database path
/// is inside a temp directory, so a regression that falls through to the world would create files there (the sentinel) and not touch anything real.
/// </summary>
public sealed class ClusterSupervisorProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-supervisor-" + Guid.NewGuid().ToString("N")[..8]);

    public ClusterSupervisorProcessTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A late handle on Windows: the temp directory is not the subject of the test.
        }
    }

    private string CharactersDb => Path.Combine(_dir, "characters.db");

    private string[] SupervisorArgs(params string[] extra) =>
    [
        "--Database:Auth:Provider=Sqlite", "--Database:Auth:ConnectionString=Data Source=" + Path.Combine(_dir, "auth.db"),
        "--Database:Characters:Provider=Sqlite", "--Database:Characters:ConnectionString=Data Source=" + CharactersDb,
        "--Database:World:Provider=Sqlite", "--Database:World:ConnectionString=Data Source=" + Path.Combine(_dir, "world.db"),
        "--World:Port=48999",
        "--Logging:ArcaneCore:File:Enabled=true", "--Logging:ArcaneCore:File:Path=" + Path.Combine(_dir, "world.log"),
        "--Cluster:Supervisor:Enabled=true",
        "--Cluster:Supervisor:MinClusterProcesses=1",
        "--Cluster:Supervisor:MinFreeMemoryReserveMb=999999999",
        "--Cluster:Supervisor:Processes:probe:Role=worker",
        "--Cluster:Supervisor:Processes:probe:ExecutablePath=dotnet",
        "--Cluster:Supervisor:Processes:probe:Arguments=--version",
        .. extra,
    ];

    [Fact]
    public async Task World_SupervisorMode_HonoursMinClusterProcesses_AndTouchesNoDatabase()
    {
        ConfProcess.Result result = await ConfProcess.RunWorldAsync(SupervisorArgs());

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.Contains("MinClusterProcesses", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(CharactersDb), "the characters database was created: the world started");
        Assert.False(File.Exists(Path.Combine(_dir, "auth.db")), "the auth database was created: the world started");
    }

    [Fact]
    public async Task World_SupervisedChild_WithSupervisorEnabled_Exits78()
    {
        ConfProcess.Result result = await ConfProcess.RunWorldAsync(
            new Dictionary<string, string?> { ["ARCANECORE_SUPERVISED_CHILD"] = "worker-EK" },
            SupervisorArgs("--Cluster:Supervisor:MinFreeMemoryReserveMb=0"));

        Assert.Equal(ExitCodes.InvalidConfiguration, result.ExitCode);
        Assert.Contains("ARCANECORE_SUPERVISED_CHILD", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(CharactersDb), "the characters database was created: the world started");
    }
}
