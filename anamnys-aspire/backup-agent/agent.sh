#!/bin/sh
# Anamnys backup agent (design/specs/2026-11-05-backup-restore-design.md §2.3). POSIX sh, one process with a 60-second
# loop; the long steps run as background jobs so the loop (and the WAL keepalive) never waits on them:
#   - every pass: if pg_stat_archiver shows no archived WAL in the last 5 min (and archiving is not failing, and the
#     agent did not switch in the last 5 min), write one WAL record and switch WAL, so an idle database still ships a
#     segment and the server's 10-minute WAL check does not false-alarm (worst-case archive age about 6 minutes);
#   - every 10 min (job; objects RPO goal 15 min): rclone sync of the live object store to files/current/, displaced
#     copies to files/versions/<UTC AAAA-MM-DDTHHMM>/, both through the crypt remote;
#   - daily at 03:00 (TZ, America/Sao_Paulo) (job): wal-g backup-push (remote, BASE_BACKUP), the row-count snapshot to
#     meta/counts/<backup-name>.json, the first full of each month marked permanent, then retention (run even when
#     the full failed); a failed full is retried once, an hour later, the same day;
#   - on start: create the bucket (before anything else, so PostgreSQL's archiver stops failing as early as possible),
#     give anamnys_backup_agent its login and password, take a full backup if none exists.
# Every run writes one BackupRuns row (kind, times, outcome, bytes, short error code, backup name) as
# anamnys_backup_agent. Nothing printed or recorded contains clinical data, object names or secrets: tool output goes
# to private files under $HOME/work and only a short code is logged.
#
# Environment (set by AppHost.cs):
#   PGHOST PGPORT PGUSER PGPASSWORD    PostgreSQL superuser (WAL-G requires it for backup-push)
#   ANAMNYS_DB KEYCLOAK_DB             database names
#   BACKUP_AGENT_DB_PASSWORD           password given to anamnys_backup_agent
#   WALG_S3_PREFIX AWS_ENDPOINT AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_S3_FORCE_PATH_STYLE AWS_REGION
#   WALG_LIBSODIUM_KEY WALG_LIBSODIUM_KEY_TRANSFORM WALG_COMPRESSION_METHOD    WAL-G, as on the PostgreSQL container
#   SOURCE_S3_ENDPOINT SOURCE_S3_ACCESS_KEY SOURCE_S3_SECRET_KEY SOURCE_S3_BUCKET    live object store
#   BACKUP_S3_ENDPOINT BACKUP_S3_BUCKET BACKUP_S3_ACCESS_KEY BACKUP_S3_SECRET_KEY    backup bucket
#   BACKUP_ENCRYPTION_KEY              32 random bytes, base64 (WAL-G libsodium key; rclone crypt password)

set -u

FULL_HOUR=3
FULL_RETRY_DELAY=3600
FILES_INTERVAL=600
WAL_STALE_SECONDS=300
LOOP_SECONDS=60
KEEP_FULL=30
VERSIONS_DAYS=30
PERMANENT_MONTHS=12
COUNT_TABLES="Providers Patients Notes NoteSections ClinicalDocuments ExternalDocuments IntegritySeals IntegrityAnchors AuditEntries Dossiers ShareLinks"

STATE_DIR="${HOME:-/var/lib/backup-agent}"
WORK="$STATE_DIR/work"
mkdir -p "$WORK"
chmod 0700 "$STATE_DIR" "$WORK"
umask 077
export RCLONE_CONFIG="$STATE_DIR/rclone.conf"
# Bounds, so a stuck connection fails a step instead of hanging it.
export PGCONNECT_TIMEOUT=30
export RCLONE_CONTIMEOUT=60s
export RCLONE_TIMEOUT=5m
TAB=$(printf '\t')

log() {
    printf '%s backup-agent: %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"
}

require_env() {
    missing=""
    for name in PGHOST PGPORT PGUSER PGPASSWORD ANAMNYS_DB KEYCLOAK_DB BACKUP_AGENT_DB_PASSWORD WALG_S3_PREFIX \
        AWS_ENDPOINT AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY WALG_LIBSODIUM_KEY SOURCE_S3_ENDPOINT \
        SOURCE_S3_ACCESS_KEY SOURCE_S3_SECRET_KEY SOURCE_S3_BUCKET BACKUP_S3_ENDPOINT BACKUP_S3_BUCKET \
        BACKUP_S3_ACCESS_KEY BACKUP_S3_SECRET_KEY BACKUP_ENCRYPTION_KEY; do
        eval "value=\${$name:-}"
        # shellcheck disable=SC2154 # value is assigned by the eval above
        [ -n "$value" ] || missing="$missing $name"
    done
    if [ -n "$missing" ]; then
        log "missing environment:$missing"
        exit 2
    fi
}

# The rclone config holds endpoints and the bucket only; keys and crypt passwords come from the environment.
render_rclone_config() {
    sed -e "s|@SOURCE_ENDPOINT@|$SOURCE_S3_ENDPOINT|g" \
        -e "s|@DEST_ENDPOINT@|$BACKUP_S3_ENDPOINT|g" \
        -e "s|@DEST_BUCKET@|$BACKUP_S3_BUCKET|g" \
        /etc/backup-agent/rclone.conf.template > "$RCLONE_CONFIG"
    export RCLONE_CONFIG_STORAGESRC_ACCESS_KEY_ID="$SOURCE_S3_ACCESS_KEY"
    export RCLONE_CONFIG_STORAGESRC_SECRET_ACCESS_KEY="$SOURCE_S3_SECRET_KEY"
    export RCLONE_CONFIG_BACKUPDEST_ACCESS_KEY_ID="$BACKUP_S3_ACCESS_KEY"
    export RCLONE_CONFIG_BACKUPDEST_SECRET_ACCESS_KEY="$BACKUP_S3_SECRET_KEY"
    obscured=$(printf '%s' "$BACKUP_ENCRYPTION_KEY" | rclone obscure -) || { log "rclone obscure failed"; exit 2; }
    export RCLONE_CONFIG_BACKUPCRYPT_PASSWORD="$obscured"
    export RCLONE_CONFIG_METACRYPT_PASSWORD="$obscured"
    unset obscured
}

# psql as the superuser (PG* environment). Reads SQL from stdin or -c. Statements are bounded.
psql_super() {
    PGOPTIONS="-c statement_timeout=300000" psql -X -q -t -A -v ON_ERROR_STOP=1 --no-password "$@"
}

# psql as anamnys_backup_agent on the application database. Reads SQL from stdin.
psql_agent() {
    PGUSER=anamnys_backup_agent PGPASSWORD="$BACKUP_AGENT_DB_PASSWORD" PGOPTIONS="-c statement_timeout=300000" \
        psql -X -q -t -A -v ON_ERROR_STOP=1 --no-password -d "$ANAMNYS_DB" "$@"
}

# The password is read by psql from the environment (\getenv), never on a command line, and the statement is kept out
# of the server log even if it fails.
init_role() {
    psql_super -d "$ANAMNYS_DB" > "$WORK/init.out" 2>&1 <<'SQL'
\getenv agent_password BACKUP_AGENT_DB_PASSWORD
BEGIN;
SET LOCAL log_min_error_statement = panic;
SET LOCAL log_statement = none;
ALTER ROLE anamnys_backup_agent LOGIN PASSWORD :'agent_password';
COMMIT;
SQL
}

# record_run <kind> <started (ISO 8601)> <outcome> <bytes or empty> <error code or empty> <backup name or empty>
#            [<log detail>]
record_run() {
    if ! psql_agent -v kind="$1" -v started="$2" -v outcome="$3" -v bytes="$4" -v error_code="$5" -v backup_name="$6" \
        > "$WORK/record-$1.out" 2>&1 <<'SQL'
INSERT INTO "BackupRuns" ("Kind", "StartedAt", "FinishedAt", "Outcome", "Bytes", "ErrorCode", "BackupName")
VALUES (:'kind', :'started'::timestamptz, now(), :'outcome', NULLIF(:'bytes', '')::bigint, NULLIF(:'error_code', ''),
        NULLIF(:'backup_name', ''));
SQL
    then
        log "could not record the $1 run (BackupRuns insert failed)"
    fi
    log "$1 $3${5:+ ($5)}${6:+ $6}${7:+ ($7)}"
}

now_iso() {
    date -u +%Y-%m-%dT%H:%M:%SZ
}

# Prints the backups as lines "<name> <start epoch> <permanent true|false> <compressed bytes>", oldest first, to the
# file named by $1. Fails when WAL-G or the JSON parse fails (an empty listing is not a failure).
list_backups() {
    wal-g backup-list --detail --json 2> "$WORK/list.err" > "$WORK/list.json" || return 1
    if [ ! -s "$WORK/list.json" ]; then
        : > "$1"
        return 0
    fi
    jq -r 'if type == "array" then .[] else empty end
           | [.backup_name, (.start_time | sub("\\.[0-9]+"; "") | sub("(Z|[+-][0-9]{2}:[0-9]{2})$"; "Z") | fromdateiso8601),
              (.is_permanent // false), (.compressed_size // 0)] | @tsv' "$WORK/list.json" > "$WORK/list.tsv" 2> "$WORK/list.err" \
        || return 1
    sort -t "$TAB" -k2,2n "$WORK/list.tsv" > "$1"
}

# The application's row counts plus the Keycloak user count per realm, counts only.
write_counts() {
    backup_name="$1"
    : > "$WORK/counts.sql"
    printf 'SELECT json_build_object(\n' >> "$WORK/counts.sql"
    first=1
    for table in $COUNT_TABLES; do
        [ "$first" -eq 1 ] || printf ',\n' >> "$WORK/counts.sql"
        printf "'%s', (SELECT count(*) FROM \"%s\")" "$table" "$table" >> "$WORK/counts.sql"
        first=0
    done
    printf ');\n' >> "$WORK/counts.sql"
    tables_json=$(psql_agent < "$WORK/counts.sql" 2> "$WORK/counts.err") || return 1
    keycloak_json=$(psql_super -d "$KEYCLOAK_DB" 2> "$WORK/counts.err" <<'SQL'
SELECT coalesce(json_object_agg(r.name, (SELECT count(*) FROM user_entity u WHERE u.realm_id = r.id)), '{}'::json)
FROM realm r WHERE r.name LIKE 'anamnys-%';
SQL
) || return 1
    jq -n --arg backup "$backup_name" --arg at "$(now_iso)" --argjson tables "$tables_json" \
        --argjson keycloakUsers "$keycloak_json" \
        '{backup: $backup, takenAt: $at, tables: $tables, keycloakUsers: $keycloakUsers}' > "$WORK/counts.json" \
        || return 1
    rclone copyto "$WORK/counts.json" "metacrypt:counts/$backup_name.json" > "$WORK/counts.err" 2>&1
}

run_full() {
    started=$(now_iso)
    month=$(date +%Y-%m)
    if ! wal-g backup-push > "$WORK/full.log" 2>&1; then
        record_run full "$started" failed "" walg_backup_push ""
        return 1
    fi
    if ! list_backups "$WORK/backups-full.tsv" || [ ! -s "$WORK/backups-full.tsv" ]; then
        record_run full "$started" failed "" walg_backup_list ""
        return 1
    fi
    newest=$(tail -n 1 "$WORK/backups-full.tsv")
    name=$(printf '%s' "$newest" | cut -f1)
    bytes=$(printf '%s' "$newest" | cut -f4)
    # The snapshot first: every backup that exists gets its counts, even if marking it fails.
    if ! write_counts "$name"; then
        record_run full "$started" failed "$bytes" counts_snapshot "$name"
        return 1
    fi
    # The first full backup of each calendar month is kept permanently (12 monthly backups, spec §1).
    permanent_this_month=0
    while IFS="$TAB" read -r b_name b_start b_permanent _; do
        [ -n "$b_name" ] || continue
        if [ "$b_permanent" = "true" ] && [ "$(date -d "@$b_start" +%Y-%m)" = "$month" ]; then
            permanent_this_month=1
        fi
    done < "$WORK/backups-full.tsv"
    if [ "$permanent_this_month" -eq 0 ] && ! wal-g backup-mark "$name" > "$WORK/mark.log" 2>&1; then
        record_run full "$started" failed "$bytes" walg_backup_mark "$name"
        return 1
    fi
    record_run full "$started" ok "$bytes" "" "$name"
}

run_retention() {
    started=$(now_iso)
    if ! wal-g delete retain FULL "$KEEP_FULL" --confirm > "$WORK/retention.log" 2>&1; then
        record_run retention "$started" failed "" walg_delete_retain ""
        return 1
    fi
    # Permanent (monthly) backups older than 12 months: unmark, then delete.
    if ! list_backups "$WORK/backups-retention.tsv"; then
        record_run retention "$started" failed "" walg_backup_list ""
        return 1
    fi
    cutoff=$(date -d "$PERMANENT_MONTHS months ago" +%s)
    while IFS="$TAB" read -r b_name b_start b_permanent _; do
        [ -n "$b_name" ] || continue
        if [ "$b_permanent" = "true" ] && [ "$b_start" -lt "$cutoff" ]; then
            if ! wal-g backup-mark -i "$b_name" > "$WORK/retention.log" 2>&1 \
                || ! wal-g delete target "$b_name" --confirm > "$WORK/retention.log" 2>&1; then
                record_run retention "$started" failed "" walg_delete_permanent ""
                return 1
            fi
        fi
    done < "$WORK/backups-retention.tsv"
    purge_versions "$started"
}

# Object version folders older than 30 days. Folder names are encrypted in the bucket, so the listing and the purge go
# through the crypt remote, which shows the decrypted UTC timestamps (AAAA-MM-DDTHHMM). Every folder must decrypt and
# match the pattern, and the crypt view must show as many folders as the raw bucket: otherwise the run fails rather
# than recording an ok that purged nothing.
purge_versions() {
    started="$1"
    if ! rclone lsf --dirs-only "backupdest:$BACKUP_S3_BUCKET/files/" > "$WORK/raw-top.txt" 2> "$WORK/retention.log" \
        || ! rclone lsf --dirs-only "backupcrypt:" > "$WORK/crypt-top.txt" 2> "$WORK/retention.log"; then
        record_run retention "$started" failed "" rclone_list_versions ""
        return 1
    fi
    if [ "$(wc -l < "$WORK/raw-top.txt")" -ne "$(wc -l < "$WORK/crypt-top.txt")" ]; then
        record_run retention "$started" failed "" versions_undecryptable ""
        return 1
    fi
    : > "$WORK/versions.txt"
    if grep -qx 'versions/' "$WORK/crypt-top.txt" \
        && ! rclone lsf --dirs-only "backupcrypt:versions/" > "$WORK/versions.txt" 2> "$WORK/retention.log"; then
        record_run retention "$started" failed "" rclone_list_versions ""
        return 1
    fi
    versions_cutoff=$(date -u -d "$VERSIONS_DAYS days ago" +%Y%m%d%H%M)
    found=0
    purged=0
    while read -r version; do
        version=${version%/}
        [ -n "$version" ] || continue
        found=$((found + 1))
        case "$version" in
            [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9][0-9][0-9]) ;;
            *)
                record_run retention "$started" failed "" versions_unrecognized "" "versions found $found"
                return 1
                ;;
        esac
        if [ "$(printf '%s' "$version" | tr -d 'T-')" -lt "$versions_cutoff" ]; then
            if ! rclone purge "backupcrypt:versions/$version" > "$WORK/retention.log" 2>&1; then
                record_run retention "$started" failed "" rclone_purge_versions "" "versions found $found, purged $purged"
                return 1
            fi
            purged=$((purged + 1))
        fi
    done < "$WORK/versions.txt"
    record_run retention "$started" ok "" "" "" "versions found $found, purged $purged"
}

run_files() {
    started=$(now_iso)
    # A fresh install has no source bucket until the server creates it: nothing to copy is a successful run.
    if ! rclone lsf --dirs-only "storagesrc:" > "$WORK/buckets.txt" 2> "$WORK/files.log"; then
        record_run files "$started" failed "" rclone_list_source ""
        return 1
    fi
    if ! grep -qx "$SOURCE_S3_BUCKET/" "$WORK/buckets.txt"; then
        record_run files "$started" ok 0 "" ""
        return 0
    fi
    stamp=$(date -u +%Y-%m-%dT%H%M)
    # NOTICE level and one final stats line only: no per-object lines (object names) are produced.
    rclone sync "storagesrc:$SOURCE_S3_BUCKET" "backupcrypt:current" --backup-dir "backupcrypt:versions/$stamp" \
        --use-json-log --log-level NOTICE --stats 24h --stats-log-level NOTICE > "$WORK/files.log" 2>&1
    code=$?
    bytes=$(jq -r 'select(.stats != null) | .stats.bytes' "$WORK/files.log" 2>/dev/null | tail -n 1)
    if [ "$code" -ne 0 ]; then
        record_run files "$started" failed "$bytes" "rclone_exit_$code" ""
        return 1
    fi
    record_run files "$started" ok "${bytes:-0}" "" ""
}

# Idle database: archive_timeout only switches WAL when something was written, so with nothing archived for 5 minutes
# write one record (a transaction id commit) and switch, and the archiver ships a segment. Not while archiving is
# failing (each switch would only pile up another 16 MB segment in pg_wal; the monitor alerts on the failure), and at
# most once per 5 minutes.
LAST_SWITCH=0
wal_keepalive() {
    state=$(PGOPTIONS="-c statement_timeout=30000" psql -X -q -t -A --no-password -d postgres -c \
        "SELECT coalesce(last_archived_time < now() - interval '$WAL_STALE_SECONDS seconds', true)::text || ' ' ||
                coalesce(last_failed_time > coalesce(last_archived_time, '-infinity'), false)::text
         FROM pg_stat_archiver" 2> "$WORK/wal.err") || {
        log "wal check failed (pg_stat_archiver)"
        return 1
    }
    case "$state" in
        "true false") ;;
        *) return 0 ;;
    esac
    now=$(date +%s)
    [ $((now - LAST_SWITCH)) -ge "$WAL_STALE_SECONDS" ] || return 0
    LAST_SWITCH=$now
    if PGOPTIONS="-c statement_timeout=30000" psql -X -q -t -A --no-password -d postgres \
        -c "SELECT pg_current_xact_id()" -c "SELECT pg_switch_wal()" > "$WORK/wal.out" 2> "$WORK/wal.err"; then
        log "wal switched (no archive in the last 5 minutes)"
    else
        log "wal switch failed"
    fi
}

# Background jobs. Each writes its exit code to $WORK/<job>.done when it ends; the loop reaps it from there.
FULL_PID=""
FILES_PID=""

full_job() {
    run_full
    result=$?
    # Retention runs every day, whether or not the full succeeded (old versions are still purged).
    run_retention
    return "$result"
}

start_full() {
    rm -f "$WORK/full.done"
    ( full_job; echo "$?" > "$WORK/full.done" ) &
    FULL_PID=$!
}

start_files() {
    rm -f "$WORK/files.done"
    ( run_files; echo "$?" > "$WORK/files.done" ) &
    FILES_PID=$!
}

require_env
render_rclone_config

# The bucket first: PostgreSQL may already be trying to archive, and every attempt fails until the bucket exists.
# PostgreSQL retries the same segment (at the latest a minute after a failure), so once this succeeds the next archive
# lands and last_archived_time passes last_failed_time again (the monitor's WAL check then clears).
until rclone mkdir "backupdest:$BACKUP_S3_BUCKET" > "$WORK/mkdir.log" 2>&1; do
    log "waiting for the backup bucket"
    sleep 30
done

until init_role; do
    log "waiting for PostgreSQL and the anamnys_backup_agent role (migration 2026-11-05-backup-runs.sql)"
    sleep 30
done
log "anamnys_backup_agent login ready"

# The day of the newest full backup, so a restart does not take a second full the same day; none at all: take one now.
LAST_FULL_DAY=""
FULL_RETRY_AT=0
FULL_RETRIED_DAY=""
if ! list_backups "$WORK/backups-start.tsv"; then
    log "backup listing failed at start; the schedule decides"
elif [ -s "$WORK/backups-start.tsv" ]; then
    LAST_FULL_DAY=$(date -d "@$(tail -n 1 "$WORK/backups-start.tsv" | cut -f2)" +%Y-%m-%d)
else
    log "no full backup yet"
    LAST_FULL_DAY=$(date +%Y-%m-%d)
    start_full
fi

last_files=0
while :; do
    wal_keepalive
    now=$(date +%s)
    today=$(date +%Y-%m-%d)
    hour=$(date +%H)

    if [ -n "$FILES_PID" ] && [ -f "$WORK/files.done" ]; then
        wait "$FILES_PID"
        FILES_PID=""
    fi
    if [ -n "$FULL_PID" ] && [ -f "$WORK/full.done" ]; then
        wait "$FULL_PID"
        FULL_PID=""
        # A failed full is retried once, an hour later, the same day; the monitor alerts on the failed row meanwhile.
        if [ "$(cat "$WORK/full.done")" != "0" ] && [ "$FULL_RETRIED_DAY" != "$today" ]; then
            FULL_RETRY_AT=$((now + FULL_RETRY_DELAY))
            FULL_RETRIED_DAY=$today
        fi
    fi

    if [ -z "$FILES_PID" ] && [ $((now - last_files)) -ge "$FILES_INTERVAL" ]; then
        last_files=$now
        start_files
    fi
    if [ -z "$FULL_PID" ]; then
        if [ "${hour#0}" -ge "$FULL_HOUR" ] && [ "$today" != "$LAST_FULL_DAY" ]; then
            LAST_FULL_DAY=$today
            FULL_RETRY_AT=0
            start_full
        elif [ "$FULL_RETRY_AT" -gt 0 ] && [ "$now" -ge "$FULL_RETRY_AT" ]; then
            FULL_RETRY_AT=0
            start_full
        fi
    fi
    sleep "$LOOP_SECONDS"
done
