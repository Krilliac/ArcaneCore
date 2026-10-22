# DevOps: decisions and observations

Source: [plan](../../reviews/2026-10-22-refactor-audit-plan.md), [findings](../../reviews/2026-10-22-refactor-audit-findings.md).

## Observations

- [DISCOVERED] How CI runs (`.github/workflows/ci.yml`):
  - a locked restore, then a Release build
  - one matrix job per `tests/ci/{Game,World,Data,Rest}.slnf`, each with `-m:1`
  - MariaDB 10.11 and PostgreSQL 16 service containers
  - the MockClient self-test, in the Rest job

  The local equivalent runs through `tools/ops/ci/run-in-slot.ps1` ([CLAUDE.md](../../../CLAUDE.md)).
- [DISCOVERED] The deploy scripts have three problems:
  - They report `healthy=True` when expected world-log lines are missing (#169, bug).
  - `deploy-post-checks.py` re-implements the `deploy-live.py` verification (#159).
  - `deploy-live.py` `main()` is a 410-line nest of closures (#163).

  Only the shared expected-lines module is in scope, together with #169. The restructure is deferred.
- [DISCOVERED] `scripts/dev-runner.ps1` writes unescaped paths into the window scripts it generates (#170, bug).
- [DISCOVERED] There is no central package management, so package versions repeat across csproj files (#189). Wave 5a would add `Directory.Packages.props` and regenerate every `packages.lock.json` in one commit.
- [DISCOVERED] Docker and the redeploy scripts string-match the name of the Playerbots pack-copy target (#187). Do not rename it.
