#!/bin/sh
# Dumps each service database into /backups/<timestamp>/ and removes backups older than 7 days.
# The databases are dumped one after another, so they are not from exactly the same moment.
set -eu

directory="/backups/$(date -u +%Y%m%dT%H%M%SZ)"
temporary="$directory.tmp"
mkdir -p "$temporary"

for database in booking payment notification; do
    pg_dump -h postgres -U postgres -Fc -f "$temporary/$database.dump" "$database"
    # Fails on an unreadable dump, so a broken backup never gets its final name.
    pg_restore --list "$temporary/$database.dump" > /dev/null
done

# Customer emails are in the dumps. Readable by the owner of the backup folder only.
chmod 700 "$temporary"
chmod 600 "$temporary"/*.dump
chown -R "$(stat -c %u:%g /backups)" "$temporary"
mv "$temporary" "$directory"

find /backups -mindepth 1 -maxdepth 1 -type d -mtime +7 -exec rm -rf {} +
echo "Backup finished: $directory"
