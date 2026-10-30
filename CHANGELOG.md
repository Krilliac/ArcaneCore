# Changelog

## Unreleased

- Content importer: the `import`, `import-dbc`, `import-map-dbc`, `proc-events` and `spell-threat` verbs now exit 3 on a world database whose schema is behind, instead of upgrading it silently. Pass `--migrate` (throwaway or staging worlds) or run `arcane-db upgrade` (production worlds). See [database-upgrade.md](docs/ops/database-upgrade.md).
- Logging: the `Logging:ArcaneCore:Json:Path` deprecation warning now fires only when a file sink is enabled; with logging off the key has no effect and no longer fails startup under `Startup:Strict`. A reload that leaves the resolved log file unchanged (for example moving the path between `Logging:ArcaneCore:Json:Path` and `Logging:ArcaneCore:File:Path`) no longer logs a restart-only notice.
- Logging: the text and JSON sinks now escape Unicode format characters (bidi overrides such as U+202E, zero-width U+200B, the byte order mark U+FEFF) as `\uXXXX`, in addition to control characters.
