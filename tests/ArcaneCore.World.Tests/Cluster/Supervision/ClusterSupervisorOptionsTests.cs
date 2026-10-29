using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Cluster.Supervision;
using ArcaneCore.World.Tests.Ops;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

public sealed class ClusterSupervisorOptionsTests
{
    private const string Valid = "Server=127.0.0.1;Port=3306;Database=x;User=arcane;Password=Sup3rSecret!;";

    private static ClusterSupervisorOptions Bind(params (string Key, string Value)[] values)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
        var options = new ClusterSupervisorOptions();
        configuration.GetSection(ClusterSupervisorOptions.SectionName).Bind(options);
        return options;
    }

    private static SupervisedProcessConfig Process(string role, string name = "p") => new()
    {
        Name = name,
        Role = role,
        ExecutablePath = "dotnet",
    };

    [Fact]
    public void Binds_ProcessesMap_MapAssignments_ConsoleMode_AndName()
    {
        ClusterSupervisorOptions options = Bind(
            ("Cluster:Supervisor:Enabled", "true"),
            ("Cluster:Supervisor:MinClusterProcesses", "2"),
            ("Cluster:Supervisor:Processes:worker-EK:Name", "worker:EK"),
            ("Cluster:Supervisor:Processes:worker-EK:Role", "worker"),
            ("Cluster:Supervisor:Processes:worker-EK:ExecutablePath", "ArcaneCore.World"),
            ("Cluster:Supervisor:Processes:worker-EK:Console", "None"),
            ("Cluster:Supervisor:Processes:worker-EK:MapAssignments:0", "0"),
            ("Cluster:Supervisor:Processes:worker-EK:MapAssignments:1", "30"),
            ("Cluster:Supervisor:Processes:gateway:Role", "gateway"),
            ("Cluster:Supervisor:Processes:gateway:ExecutablePath", "ArcaneCore.Gateway"));

        Assert.True(options.Enabled);
        Assert.Equal(2, options.MinClusterProcesses);
        Assert.Equal(2, options.Processes.Count);
        SupervisedProcessConfig worker = options.Processes["worker-EK"];
        Assert.Equal("worker:EK", worker.Name);
        Assert.Equal(SupervisedConsoleMode.None, worker.Console);
        Assert.Equal([0u, 30u], worker.MapAssignments);
        Assert.Equal(SupervisedConsoleMode.Merged, options.Processes["gateway"].Console);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Defaults_AreValid_AndDisabled()
    {
        var options = new ClusterSupervisorOptions();

        Assert.False(options.Enabled);
        Assert.Equal(3, options.MinClusterProcesses);
        Assert.Equal(9, options.MaxConcurrentProcesses);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Validate_EnabledBelowMinClusterProcesses_IsError()
    {
        var options = new ClusterSupervisorOptions { Enabled = true };
        options.Processes["realm"] = Process(SupervisedRoles.Realm, "realm");
        options.Processes["gateway"] = Process(SupervisedRoles.Gateway, "gateway");

        string problem = Assert.Single(options.Validate());
        Assert.Contains("MinClusterProcesses", problem, StringComparison.Ordinal);

        options.Enabled = false;
        Assert.Empty(options.Validate()); // only an enabled supervisor needs its topology
    }

    [Fact]
    public void Validate_OverlappingWorkerMaps_IsError()
    {
        var options = new ClusterSupervisorOptions();
        options.Processes["w-a"] = new SupervisedProcessConfig { Name = "w-a", Role = SupervisedRoles.Worker, ExecutablePath = "dotnet", MapAssignments = [0, 1] };
        options.Processes["w-b"] = new SupervisedProcessConfig { Name = "w-b", Role = SupervisedRoles.Worker, ExecutablePath = "dotnet", MapAssignments = [1, 530] };

        string problem = Assert.Single(options.Validate());
        Assert.Contains("map 1", problem, StringComparison.Ordinal);
        Assert.Contains("w-a", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_MinAboveMax_IsError()
    {
        var options = new ClusterSupervisorOptions { MaxConcurrentProcesses = 2, MinClusterProcesses = 3 };

        Assert.Contains(options.Validate(), p => p.Contains("MinClusterProcesses", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RangesRolesKeysAndProcessFields_AreChecked()
    {
        var options = new ClusterSupervisorOptions
        {
            MaxConcurrentProcesses = 65,
            MinFreeMemoryReserveMb = -1,
            MaxTotalAllocatedMemoryMb = 0,
        };
        options.Processes["worker:EK"] = Process(SupervisedRoles.Worker, "worker:EK");
        options.Processes["odd"] = Process("overlord", "odd");
        options.Processes["blank"] = new SupervisedProcessConfig { Role = SupervisedRoles.Realm }; // no executable

        string[] problems = [.. options.Validate()];

        Assert.Contains(problems, p => p.Contains("MaxConcurrentProcesses", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("MinFreeMemoryReserveMb", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("MaxTotalAllocatedMemoryMb", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'worker:EK' must not contain ':'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'overlord'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("blank", StringComparison.Ordinal) && p.Contains("ExecutablePath", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_TwoKeysWithTheSameEffectiveName_IsError()
    {
        var options = new ClusterSupervisorOptions();
        options.Processes["worker-a"] = Process(SupervisedRoles.Worker, "worker:EK");
        options.Processes["worker-b"] = Process(SupervisedRoles.Worker, "WORKER:ek"); // names compare case-insensitively
        options.Processes["worker:EK"] = new SupervisedProcessConfig { Role = SupervisedRoles.Worker, ExecutablePath = "dotnet" }; // no Name: the key is the name

        string[] problems = [.. options.Validate()];

        Assert.Contains(problems, p => p.Contains("same name", StringComparison.Ordinal) && p.Contains("worker-b", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("same name", StringComparison.Ordinal) && p.Contains("'worker:EK'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DrainTimeoutAboveTenMinutes_IsError()
    {
        var options = new ClusterSupervisorOptions();
        options.Processes["slow"] = new SupervisedProcessConfig { Name = "slow", Role = SupervisedRoles.Realm, ExecutablePath = "dotnet", DrainTimeoutMs = 600_001 };

        Assert.Contains(options.Validate(), p => p.Contains("DrainTimeoutMs", StringComparison.Ordinal));
        options.Processes["slow"].DrainTimeoutMs = 600_000;
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Options_Warnings_WhenDrainTimeoutsExceedTheShutdownCap()
    {
        static ClusterSupervisorOptions WithProcesses(int count, bool enabled)
        {
            var options = new ClusterSupervisorOptions { Enabled = enabled, MinClusterProcesses = 1 };
            for (int i = 0; i < count; i++)
            {
                options.Processes[$"p{i}"] = new SupervisedProcessConfig { Name = $"p{i}", Role = SupervisedRoles.Worker, ExecutablePath = "dotnet", DrainTimeoutMs = 600_000 };
            }

            return options;
        }

        Assert.Single(WithProcesses(7, enabled: true).Warnings()); // 70 min + margin > 1 h
        Assert.Empty(WithProcesses(5, enabled: true).Warnings()); // 50 min + margin
        Assert.Empty(WithProcesses(7, enabled: false).Warnings());

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cluster:Supervisor:Enabled"] = "true",
            ["Cluster:Supervisor:MinClusterProcesses"] = "1",
            ["Cluster:Supervisor:MaxConcurrentProcesses"] = "9",
            ["Cluster:Supervisor:Processes:p0:Role"] = "worker",
            ["Cluster:Supervisor:Processes:p0:ExecutablePath"] = "dotnet",
            ["Cluster:Supervisor:Processes:p0:DrainTimeoutMs"] = "600000",
            ["Cluster:Supervisor:Processes:p1:Role"] = "worker",
            ["Cluster:Supervisor:Processes:p1:ExecutablePath"] = "dotnet",
            ["Cluster:Supervisor:Processes:p1:DrainTimeoutMs"] = "600000",
            ["Cluster:Supervisor:Processes:p2:Role"] = "worker",
            ["Cluster:Supervisor:Processes:p2:ExecutablePath"] = "dotnet",
            ["Cluster:Supervisor:Processes:p2:DrainTimeoutMs"] = "600000",
            ["Cluster:Supervisor:Processes:p3:Role"] = "worker",
            ["Cluster:Supervisor:Processes:p3:ExecutablePath"] = "dotnet",
            ["Cluster:Supervisor:Processes:p3:DrainTimeoutMs"] = "600000",
            ["Cluster:Supervisor:Processes:p4:Role"] = "worker",
            ["Cluster:Supervisor:Processes:p4:ExecutablePath"] = "dotnet",
            ["Cluster:Supervisor:Processes:p4:DrainTimeoutMs"] = "600000",
            ["Cluster:Supervisor:Processes:p5:Role"] = "worker",
            ["Cluster:Supervisor:Processes:p5:ExecutablePath"] = "dotnet",
            ["Cluster:Supervisor:Processes:p5:DrainTimeoutMs"] = "600000",
            ["Cluster:Supervisor:Processes:p6:Role"] = "worker",
            ["Cluster:Supervisor:Processes:p6:ExecutablePath"] = "dotnet",
            ["Cluster:Supervisor:Processes:p6:DrainTimeoutMs"] = "600000",
        }).Build();

        ConfigIssue issue = Assert.Single(new ClusterSupervisorConfigChecks().Check(configuration));
        Assert.Equal(ConfigSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void ConfigChecks_ReportBindFailures_AndSupervisorWithWorkerMode()
    {
        var check = new ClusterSupervisorConfigChecks();

        IConfiguration notANumber = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cluster:Supervisor:MaxConcurrentProcesses"] = "many" })
            .Build();
        Assert.Contains(check.Check(notANumber), i => i.Severity == ConfigSeverity.Error && i.Key == "Cluster:Supervisor");

        IConfiguration both = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cluster:Supervisor:Enabled"] = "true",
                ["Cluster:Supervisor:MinClusterProcesses"] = "1",
                ["Cluster:Supervisor:Processes:realm:Role"] = "realm",
                ["Cluster:Supervisor:Processes:realm:ExecutablePath"] = "dotnet",
                ["Cluster:Worker:Enabled"] = "true",
            })
            .Build();
        ConfigIssue issue = Assert.Single(check.Check(both));
        Assert.Equal(ConfigSeverity.Error, issue.Severity);
        Assert.Equal("Cluster:Supervisor:Enabled", issue.Key);

        Assert.Empty(check.Check(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public async Task CheckConfig_ExitsInvalidConfiguration_ForBadSupervisorConfig()
    {
        string[] database =
        [
            "--Database:Auth:Provider=MariaDb", "--Database:Auth:ConnectionString=" + Valid,
            "--Database:Characters:Provider=MariaDb", "--Database:Characters:ConnectionString=" + Valid,
            "--Database:World:Provider=MariaDb", "--Database:World:ConnectionString=" + Valid,
        ];

        ConfProcess.Result result = await ConfProcess.RunWorldAsync(
            ["check-config", .. database, "--Cluster:Supervisor:Enabled=true", "--Cluster:Supervisor:MinClusterProcesses=3"]);

        Assert.Equal(ExitCodes.InvalidConfiguration, result.ExitCode);
        Assert.Contains("MinClusterProcesses", result.Output, StringComparison.Ordinal);
    }
}
