#!/bin/sh
# Nightly copy of the game database, one file per weekday, so a week of history is kept and
# the oldest is overwritten. Installed as /usr/local/bin/criticalcount-backup and run by
# /etc/cron.d/criticalcount-backup (see DEPLOY.md). sqlite3's .backup is safe while the
# server is running.
set -eu
DB=/var/lib/criticalcount/criticalcount.db
DIR=/var/lib/criticalcount/backups
mkdir -p "$DIR"
sqlite3 "$DB" ".backup '$DIR/criticalcount-$(date +%a).db'"
