using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>
/// The operator's apply: a read-only pre-flight (<see cref="SchemaPlanner"/>) that refuses a database newer
/// than the code, an unknown state and every blocker (conflicting index, duplicate rows, mismatching table)
/// before any DDL is issued, then the very same <see cref="SchemaBootstrapper"/> the daemons run, so the
/// lock, the resume rule and the per-step version write are one code path, not two.
/// <para>
/// The pre-flight reads outside the lock and can be overtaken by another process; the bootstrapper decides
/// again under the lock, so a stale pre-flight can only fail closed, never apply something the plan did not allow.
/// On MariaDB/MySQL DDL commits implicitly: an apply that dies (or is refused by a race) midway leaves the
/// finished steps in place with the version row at the last completed step, and running it again converges.
/// SQLite is atomic; PostgreSQL gets resumability only (a single transaction around the bootstrap is an
/// open item, see docs/integration/db-upgrade-tooling.md).
/// </para>
/// </summary>
public static class SchemaUpgrader
{
    /// <summary>Plan, refuse what cannot be applied, then apply. Returns the plan that was executed.</summary>
    /// <exception cref="SchemaDowngradeException">The database is newer than the code.</exception>
    /// <exception cref="SchemaBlockedException">The plan holds a refusal.</exception>
    /// <exception cref="SchemaPolicyException"><paramref name="options"/> forbid the change.</exception>
    public static async Task<SchemaPlan> ApplyAsync(
        DbContext db, SchemaDefinition definition, SchemaUpgradeOptions? options = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        options ??= new SchemaUpgradeOptions();
        SchemaPlan plan = await SchemaPlanner.PlanAsync(db, definition, includeScript: false, cancellationToken).ConfigureAwait(false);

        SchemaPolicyException.ThrowIfForbidden(options.Policy, plan);
        if (plan.State == SchemaState.Newer)
        {
            throw new SchemaDowngradeException(plan.Refusal!, plan.DatabaseVersion!.Value, plan.CodeVersion);
        }

        if (plan.IsRefused)
        {
            int more = plan.Blockers.Count - (plan.Refusal is null ? 1 : 0);
            string message = plan.FirstRefusal! + (more > 0 ? $" (and {more} more refusal(s); run arcane-db plan to see all)" : string.Empty);
            throw new SchemaBlockedException(message, plan);
        }

        // A database that does not exist yet has no sessions, and on a server the probe itself would fail
        // (Unknown database / 3D000), so a fresh install must never reach it (SchemaPlan Missing).
        if (options.RefuseActiveSessions && plan.NeedsApply && plan.State != SchemaState.Missing)
        {
            int? others = await (options.SessionProbe is { } probe
                ? probe(db, cancellationToken)
                : ServerProbe.CountOtherSessionsAsync(db, cancellationToken)).ConfigureAwait(false);
            if (others is > 0)
            {
                throw new SchemaActiveSessionsException(
                    $"{others} other session(s) are connected to the {definition.Component} database. Stop every ArcaneCore daemon and tool first " +
                    "(visibility is best effort: sessions of other roles may not be listed), or pass --allow-active-sessions if you know they are idle.",
                    others.Value);
            }
        }

        await SchemaBootstrapper.EnsureAsync(db, definition, options, logger, cancellationToken).ConfigureAwait(false);
        return plan;
    }
}
