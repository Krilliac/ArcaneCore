using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Clustering;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Cluster.Supervision;

/// <summary>
/// Startup check of the <c>Cluster:Supervisor</c> section (docs/CLUSTERING_DESIGN.md M15.4): a value that does not bind or that
/// <see cref="ClusterSupervisorOptions.Validate"/> refuses is an error (exit 78), and so is running the supervisor mode and worker
/// mode in one process (the supervisor builds no world; a worker is a world).
/// </summary>
public sealed class ClusterSupervisorConfigChecks : IConfigCheck
{
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var options = new ClusterSupervisorOptions();
        try
        {
            configuration.GetSection(ClusterSupervisorOptions.SectionName).Bind(options);
        }
        catch (InvalidOperationException ex)
        {
            return [new ConfigIssue(ConfigSeverity.Error, ClusterSupervisorOptions.SectionName, $"a value does not fit its key ({ex.Message}).",
                "use true/false, whole numbers, and the console modes Merged, Window or None")];
        }

        var issues = new List<ConfigIssue>();
        foreach (string problem in options.Validate())
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, ClusterSupervisorOptions.SectionName, problem, "see the M15.4 section of docs/CLUSTERING_DESIGN.md"));
        }

        foreach ((string warning, string remediation) in options.WarningsWithRemediation())
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Warning, $"{ClusterSupervisorOptions.SectionName}:Processes", warning, remediation));
        }

        if (options.Enabled && configuration.GetSection(GatewayWorkerOptions.SectionName).GetValue<bool>(nameof(GatewayWorkerOptions.Enabled)))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, $"{ClusterSupervisorOptions.SectionName}:Enabled",
                $"the supervisor and {GatewayWorkerOptions.SectionName}:Enabled (worker mode) are both on, but the supervisor builds no world.",
                $"turn one off: the supervisor launches workers as child processes with their own {GatewayWorkerOptions.SectionName}:Enabled"));
        }

        if (options.Enabled)
        {
            issues.AddRange(CheckFencing(options, configuration));
            issues.AddRange(CheckRealmRowLabels(options));
        }

        return issues;
    }

    /// <summary>
    /// A supervisor that runs more than one world-type child (role combined, worker, social or coordinator: the last two are labels on a World executable), or a child that sets <c>Cluster:Node:Role</c> to something but Combined, has several
    /// writers on one characters database: each must run with <c>Cluster:Fencing:Enabled=true</c> (an unfenced world skips <c>PrimeDurableIdAllocatorsAsync</c> and collides on the
    /// in-memory id counters), and each needs its own <c>Cluster:Fencing:OwnerNode</c> and <c>Cluster:Worker:NodeId</c>. A child's value is its command-line argument
    /// (<c>--Key=value</c>, as <c>tools/ops/live/start-cluster.ps1</c> passes them) or else the supervisor's own configuration, which the children inherit.
    /// A SQLite characters database with several worlds is refused by <see cref="ClusterSqliteConfigChecks"/>.
    /// </summary>
    private static IEnumerable<ConfigIssue> CheckFencing(ClusterSupervisorOptions options, IConfiguration configuration)
    {
        var worlds = options.Processes
            .Where(p => string.Equals(p.Value.Role, SupervisedRoles.Combined, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Value.Role, SupervisedRoles.Worker, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Value.Role, SupervisedRoles.Social, StringComparison.OrdinalIgnoreCase) // social and coordinator are labels on a World executable (ClusterNodeOptions.Validate refuses a
                || string.Equals(p.Value.Role, SupervisedRoles.Coordinator, StringComparison.OrdinalIgnoreCase)) // dedicated social node), so such a child is a whole Combined world: a writer like any other
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        bool several = worlds.Count > 1;
        var ownerNodes = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodeIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var shards = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var standbyShards = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, SupervisedProcessConfig process) in worlds)
        {
            string path = $"{ClusterSupervisorOptions.SectionName}:Processes:{key}";
            IReadOnlyDictionary<string, string> args = ParseArguments(process.Arguments);
            string role = args.GetValueOrDefault("Cluster:Node:Role") ?? configuration["Cluster:Node:Role"] ?? string.Empty;
            bool nonCombined = role.Length > 0 && !string.Equals(role, "Combined", StringComparison.OrdinalIgnoreCase);
            if (!several && !nonCombined)
            {
                continue;
            }

            string? enabledText = args.GetValueOrDefault("Cluster:Fencing:Enabled") ?? configuration["Cluster:Fencing:Enabled"];
            if (!bool.TryParse(enabledText, out bool fenced) || !fenced)
            {
                yield return new ConfigIssue(ConfigSeverity.Error, $"{path}:Arguments",
                    $"world child '{key}' runs without Cluster:Fencing:Enabled=true, but {(several ? $"the supervisor runs {worlds.Count} world-type children against one characters database" : $"it sets Cluster:Node:Role={role}")}. An unfenced world skips the durable id allocator priming and collides with the others on its in-memory id counters.",
                    "pass --Cluster:Fencing:Enabled=true in the child's Arguments (or set Cluster:Fencing:Enabled=true in the shared configuration), on a PostgreSQL or MariaDB characters database");
            }

            if (several)
            {
                string owner = args.GetValueOrDefault("Cluster:Fencing:OwnerNode") ?? configuration["Cluster:Fencing:OwnerNode"] ?? Environment.MachineName;
                if (ownerNodes.TryGetValue(owner, out string? other))
                {
                    yield return new ConfigIssue(ConfigSeverity.Error, $"{path}:Arguments",
                        $"world children '{other}' and '{key}' share the Cluster:Fencing:OwnerNode '{owner}', so they would claim the same durable ownership row.",
                        "give each world child its own --Cluster:Fencing:OwnerNode=<name> in its Arguments");
                }
                else
                {
                    ownerNodes[owner] = key;
                }

                string nodeId = args.GetValueOrDefault("Cluster:Worker:NodeId") ?? configuration["Cluster:Worker:NodeId"] ?? new GatewayWorkerOptions().NodeId;
                if (nodeIds.TryGetValue(nodeId, out other))
                {
                    yield return new ConfigIssue(ConfigSeverity.Error, $"{path}:Arguments",
                        $"world children '{other}' and '{key}' share the Cluster:Worker:NodeId '{nodeId}', so the gateway cannot tell them apart.",
                        "give each world child its own --Cluster:Worker:NodeId=<name> in its Arguments");
                }
                else
                {
                    nodeIds[nodeId] = key;
                }

                // Worker fencing scope: the durable scope and the shard advisory lock are named after the shard, compared without regard to case, so two shard ids that differ only in case are one shard.
                string scopeText = args.GetValueOrDefault("Cluster:Fencing:Scope") ?? configuration["Cluster:Fencing:Scope"] ?? string.Empty;
                // A standby (--Cluster:Worker:Standby=true) deliberately names the shard of the active world it takes over from (Cluster:Fencing:Shard, so its promotion claims the same durable scope);
                // it claims and locks nothing until then, so it is not a second claimant of that shard (tools/ops/live/start-cluster.ps1 -Standby).
                bool isStandby = bool.TryParse(args.GetValueOrDefault("Cluster:Worker:Standby") ?? configuration["Cluster:Worker:Standby"], out bool standbyFlag) && standbyFlag;
                if (Enum.TryParse(scopeText, ignoreCase: true, out CharacterFencingScope scopeValue) && Enum.IsDefined(scopeValue) && scopeValue == CharacterFencingScope.Worker) // names and numbers, as the options binder reads them
                {
                    string shardText = args.GetValueOrDefault("Cluster:Fencing:Shard") ?? configuration["Cluster:Fencing:Shard"] ?? string.Empty;
                    string shard = (string.IsNullOrWhiteSpace(shardText) ? nodeId : shardText).Trim();
                    if (isStandby)
                    {
                        // Two standbys on one shard would both try to take over the same world; they are compared like active shards (ignoring case).
                        if (standbyShards.TryGetValue(shard, out other))
                        {
                            yield return new ConfigIssue(ConfigSeverity.Error, $"{path}:Arguments",
                                $"standby children '{other}' and '{key}' both stand behind the Cluster:Fencing:Scope=Worker shard '{shard}' (shard ids are compared ignoring case), so a handoff could not tell which one takes the world over.",
                                "give each standby the shard of exactly one active world (--Cluster:Fencing:Shard=<that world's shard>)");
                        }
                        else
                        {
                            standbyShards[shard] = key;
                        }
                    }
                    else if (shards.TryGetValue(shard, out other))
                    {
                        yield return new ConfigIssue(ConfigSeverity.Error, $"{path}:Arguments",
                            $"world children '{other}' and '{key}' share the Cluster:Fencing:Scope=Worker shard '{shard}' (shard ids are compared ignoring case), so they would claim the same durable scope and shard lock.",
                            "give each world child its own --Cluster:Worker:NodeId=<name> (or --Cluster:Fencing:Shard=<name>) that differs by more than letter case");
                    }
                    else
                    {
                        shards[shard] = key;
                    }
                }
            }
        }

        // A standby that names a shard no active world uses would promote into a durable scope nothing else holds (and nothing hands over to it).
        foreach ((string standbyShard, string standbyKey) in standbyShards)
        {
            if (!shards.ContainsKey(standbyShard))
            {
                yield return new ConfigIssue(ConfigSeverity.Error, $"{ClusterSupervisorOptions.SectionName}:Processes:{standbyKey}:Arguments",
                    $"standby child '{standbyKey}' names the shard '{standbyShard}', but no active world child uses that shard, so it has no world to stand behind.",
                    "set the standby's --Cluster:Fencing:Shard to the shard (--Cluster:Fencing:Shard, else --Cluster:Worker:NodeId) of the active world it is to take over from");
            }
        }
    }

    /// <summary>
    /// Fail closed on the realm-list row: a child labelled social or coordinator is a whole World executable (see <see cref="CheckFencing"/>), so with
    /// <c>World:Realm:Id</c> above 0 (the default is 1) it could flip the realm's row Online and Offline beside the world that really serves it. Such a child
    /// must carry <c>--World:Realm:Id=0</c> in its own Arguments: the supervisor does not forward its own command line or configuration, and the child may
    /// have another working directory and conf, so the shared configuration is not trusted. Any mention of the key that is not the exact <c>--Key=value</c>
    /// form (space form, slash form, inside a quoted value, or right after a <c>--Key</c> that has no <c>=</c>) is unreadable here and refused, as is a value that is not 0.
    /// </summary>
    private static IEnumerable<ConfigIssue> CheckRealmRowLabels(ClusterSupervisorOptions options)
    {
        foreach ((string key, SupervisedProcessConfig process) in options.Processes.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(process.Role, SupervisedRoles.Social, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(process.Role, SupervisedRoles.Coordinator, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string resolved = ResolveChildRealmId(process.Arguments ?? string.Empty, out bool writesRow);
            if (writesRow)
            {
                yield return new ConfigIssue(ConfigSeverity.Error, $"{ClusterSupervisorOptions.SectionName}:Processes:{key}:Arguments",
                    $"world child '{key}' is labelled {process.Role} but its own Arguments do not set World:Realm:Id=0 (found {resolved}), so a world with a realm id above 0 could write that realm's realm-list row (Online, Offline, population) beside the world that serves the realm.",
                    "pass --World:Realm:Id=0 in the child's Arguments (exact --Key=value form), so only the combined world or the gateway writes the realm row");
            }
        }
    }

    /// <summary>
    /// The realm id the child's Arguments set, for the message; <paramref name="writesRow"/> is false only when every mention of the key is an exact <c>--World:Realm:Id=0</c>.
    /// The line is split the way the child's command line is (whitespace outside double quotes, the quotes themselves removed), so a mention that sits inside a quoted
    /// value of another key is not a setting of its own and is refused as unreadable, and so is a mention that follows a <c>--Key</c> with no <c>=</c>
    /// (the configuration command-line provider takes that next word as the key's value, not as a key). With several mentions the message names the offending
    /// value (the first that is not 0), not the last one. A bare <c>/Key</c> counts like a bare <c>--Key</c> (the command-line provider reads both the same way), and a line holding a
    /// line break (the child's argv splits only at space and tab) or a backslash before a double quote is refused outright (the operating system's escape rules are not modelled here). The check is deliberately over-strict: <c>--A --B --World:Realm:Id=0</c>
    /// is refused although the provider gives <c>--A</c> the value <c>--B</c> and does read the id; that fails closed.
    /// </summary>
    internal static string ResolveChildRealmId(string arguments, out bool writesRow)
    {
        const string KeyName = "World:Realm:Id";
        string prefix = "--" + KeyName + "=";
        string? last = null;
        string? offending = null;
        bool unreadable = false;
        bool previousWasBareKey = false;

        // The Arguments string reaches the child raw and the operating system's argument parsing treats a backslash before a double quote as an escape, which this
        // splitter does not model; a line holding one cannot be read reliably here, so it is refused rather than guessed at.
        // A line break is refused too: the child's argv splits only at space and tab, so a CR or LF keeps "--A=x<LF>--World:Realm:Id=0" one argument (a mention inside another key's value).
        if (arguments.Contains("\\\"", StringComparison.Ordinal) || arguments.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            writesRow = true;
            return "an argument form the supervisor cannot read";
        }

        foreach (string text in SplitCommandLine(arguments))
        {
            bool mentions = text.Contains(KeyName, StringComparison.OrdinalIgnoreCase);
            if (mentions)
            {
                if (previousWasBareKey || !text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    unreadable = true;
                }
                else
                {
                    string value = text[prefix.Length..];
                    last = value;
                    if (offending is null
                        && (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int id) || id != 0))
                    {
                        offending = value;
                    }
                }
            }

            previousWasBareKey = (text.StartsWith("--", StringComparison.Ordinal) || text.StartsWith('/')) && !text.Contains('=', StringComparison.Ordinal);
        }

        writesRow = last is null || unreadable || offending is not null;
        return unreadable ? "an argument form the supervisor cannot read" : offending ?? last ?? "no value, so the default 1";
    }

    /// <summary>Splits a command line at whitespace outside double quotes; the quote characters are dropped, as the operating system's argument parsing drops them.</summary>
    private static IEnumerable<string> SplitCommandLine(string arguments)
    {
        var current = new System.Text.StringBuilder();
        bool inQuote = false;
        bool started = false;
        foreach (char c in arguments)
        {
            if (c == '"')
            {
                inQuote = !inQuote;
                started = true;
            }
            else if (!inQuote && (c == ' ' || c == '\t'))
            {
                if (started)
                {
                    yield return current.ToString();
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started)
        {
            yield return current.ToString();
        }
    }

    private static readonly char[] WhitespaceSeparators = [' ', '\t', '\r', '\n'];

    /// <summary>The <c>--Key=value</c> tokens of a child's command line (whitespace separated, later tokens win, keys case-insensitive); other tokens are ignored.</summary>
    internal static Dictionary<string, string> ParseArguments(string arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in arguments.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            string text = token.Trim('"');
            int eq = text.IndexOf('=', StringComparison.Ordinal);
            if (text.StartsWith("--", StringComparison.Ordinal) && eq > 2)
            {
                result[text[2..eq]] = text[(eq + 1)..].Trim('"');
            }
        }

        return result;
    }
}
