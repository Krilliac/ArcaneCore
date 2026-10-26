#!/usr/bin/env bash
# Dumps the three ArcaneCore schemas of the compose database container into one gzip file and prunes old dumps. Cron-able:
#
#   17 4 * * *  cd /path/to/checkout && BACKUP_DIR=/path/to/backups COMPOSE_FILE=docker-compose.prod.yml tools/ops/deploy/backup-database.sh
#
# The dump runs inside the database container as root with the container's own MARIADB_ROOT_PASSWORD, so no password is on a command
# line or in the cron entry. --single-transaction takes a consistent InnoDB snapshot without locking the running realm and world.
# Restore: docs/guide/docker.md, "Backups".
#
# Environment (all optional):
#   COMPOSE_FILE   compose file to use                    (default docker-compose.yml)
#   DB_SERVICE     the database service name              (default arcane-database)
#   BACKUP_DIR     where dumps are written                (default ./backups)
#   KEEP_DAYS      delete dumps older than this many days (default 14; 0 keeps everything)
set -euo pipefail

compose_file="${COMPOSE_FILE:-docker-compose.yml}"
service="${DB_SERVICE:-arcane-database}"
backup_dir="${BACKUP_DIR:-./backups}"
keep_days="${KEEP_DAYS:-14}"

case "$keep_days" in
  '' | *[!0-9]*) echo "KEEP_DAYS must be a whole number of days, got '$keep_days'" >&2; exit 64 ;;
esac

mkdir -p "$backup_dir"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
target="$backup_dir/arcanecore-$stamp.sql.gz"
partial="$target.partial"
trap 'rm -f "$partial"' EXIT

# pipefail makes a failed dump fail the script instead of leaving a valid-looking gzip of nothing.
docker compose -f "$compose_file" exec -T "$service" sh -c \
  'exec mariadb-dump -uroot -p"$MARIADB_ROOT_PASSWORD" --single-transaction --routines --events --databases arcanecore_auth arcanecore_characters arcanecore_world' \
  | gzip -9 > "$partial"

mv "$partial" "$target"
trap - EXIT
echo "wrote $target ($(wc -c < "$target") bytes)"

if [ "$keep_days" -gt 0 ]; then
  find "$backup_dir" -maxdepth 1 -name 'arcanecore-*.sql.gz' -type f -mtime "+$keep_days" -print -delete
fi
