#!/bin/sh
# Replaces all service databases with a backup. Run from the repository root:
#   docker/postgres/restore.sh 20261102T120000Z
set -eu

compose="${COMPOSE:-docker compose -f docker-compose.prod.yml --env-file .env.prod}"
backups="${BACKUP_DIR:-backups}"
timestamp="${1:-}"

if [ -z "$timestamp" ] || [ ! -d "$backups/$timestamp" ]; then
    echo "Usage: $0 <timestamp>. Available backups:" >&2
    ls "$backups" >&2
    exit 1
fi

printf 'This replaces all current data with backup %s. Type yes to continue: ' "$timestamp"
read -r answer
[ "$answer" = yes ] || { echo "Cancelled."; exit 1; }

# Nothing may use the databases during the restore.
services="caddy gateway booking payment notification backup"
$compose stop $services

for database in booking payment notification; do
    # One transaction: a failed restore leaves the database as it was.
    $compose exec -T postgres pg_restore -U postgres -d "$database" \
        --clean --if-exists --single-transaction --exit-on-error < "$backups/$timestamp/$database.dump"
    echo "Restored $database"
done

$compose start $services
echo "Done. Check https://localhost and the Dead letters and Failed refunds panels in Grafana."
