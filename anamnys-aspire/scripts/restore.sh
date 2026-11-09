#!/bin/sh
# shellcheck disable=SC2016 # single-quoted scripts run inside containers; their $ expand there, on purpose
# Anamnys restore (design/specs/2026-11-05-backup-restore-design.md §4.1): rebuilds PostgreSQL (both databases) and the
# object store from the backup bucket, on a new server, to a point in time or to the latest state.
#
#   scripts/restore.sh --target-time "AAAA-MM-DD HH:MM:SS-03"
#   scripts/restore.sh --latest
#
# Steps:
#   1. Pick the newest full backup that finished before the target (--latest: the newest), wal-g backup-fetch it into a
#      fresh data volume.
#   2. Recovery: recovery.signal, restore_command='wal-g wal-fetch %f %p', recovery_target_time and
#      recovery_target_action=promote (--latest: no target, recovery runs to the end of the archived WAL). Archiving is
#      off on the restored server, so it never writes into the backup bucket. Start PostgreSQL and wait for promotion.
#      When the archived WAL ends before any commit after the target (an idle database, or WAL not shipped yet),
#      PostgreSQL stops with "recovery ended before configured recovery target was reached"; every commit replayed is
#      then before the target. Only when that first run reached the newest archived WAL segment (otherwise: a gap or a
#      fetch failure, and the restore fails), the script restarts the recovery bounded to exactly where the first run
#      ended (recovery_target_lsn = its "redo done at" LSN, inclusive), so WAL archived meanwhile can never take it past
#      the target. --latest is checked the same way: recovery must end at the newest archived segment.
#      Every wal-g, rclone and docker failure fails the restore (exit 1); nothing is skipped as "not there".
#   3. Objects: rclone copy from backupcrypt:current/ into the new object store (its bucket must be new or empty); for a
#      target, then overlay every version folder backupcrypt:versions/<UTC AAAA-MM-DDTHHMM>/ whose minute is at or after
#      the target's minute (UTC), newest first, so each object is as it was at the last sync before the target.
#      Documented limit: object state is exact to the 10-minute sync grid, and an object created after that sync is left
#      in place (no row references it).
#   4. Optionally, the row-count snapshot of the backup used (meta/counts/<backup-name>.json) to RESTORE_COUNTS_FILE.
#
# The application server is not started. Chapter 40: run anamnys-aspire.RestoreCheck first and start the server only
# after it passes. Nothing printed contains secrets, object names or clinical data; tool output goes to a private work
# directory, kept (and named) only when a step fails.
#
# Environment. The backup bucket, exactly as the backup agent has it (AppHost.cs):
#   WALG_S3_PREFIX AWS_ENDPOINT AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY        WAL-G (AWS_REGION, AWS_S3_FORCE_PATH_STYLE,
#   WALG_LIBSODIUM_KEY                                                         WALG_LIBSODIUM_KEY_TRANSFORM and
#                                                                              WALG_COMPRESSION_METHOD default as there)
#   BACKUP_S3_ENDPOINT BACKUP_S3_BUCKET BACKUP_S3_ACCESS_KEY BACKUP_S3_SECRET_KEY    rclone, backup bucket
#   BACKUP_ENCRYPTION_KEY                                                      rclone crypt password
# The endpoints must be reachable from containers on RESTORE_NETWORK.
# The new server:
#   RESTORE_NETWORK            Docker network the restored PostgreSQL joins (must exist)
#   RESTORE_PG_CONTAINER       name of the restored PostgreSQL container (default anamnys-restored-postgres)
#   RESTORE_PG_VOLUME          data volume, created here, must not exist (default <container>-data)
#   RESTORE_PG_PUBLISH         optional docker -p value for 5432, e.g. 127.0.0.1:5432:5432 or 127.0.0.1::5432
#   RESTORE_S3_ENDPOINT RESTORE_S3_BUCKET RESTORE_S3_ACCESS_KEY RESTORE_S3_SECRET_KEY    new object store; the bucket
#                              has no default, must be new or empty (the restore refuses one that holds any
#                              object, so it can never write over a live store), reachable from RESTORE_NETWORK
#   RESTORE_PG_IMAGE           PostgreSQL+WAL-G image (default: built from postgres/ as anamnys-postgres-backup:restore)
#   RESTORE_AGENT_IMAGE        backup-agent image for wal-g/rclone (default: built from backup-agent/ as
#                              anamnys-backup-agent:restore)
#   RESTORE_COUNTS_FILE        optional: where to write the row-count snapshot of the backup used
#   RESTORE_SUMMARY_FILE       optional: key=value summary (backup and its finish time, mode, versions overlaid),
#                              no secrets
#   RESTORE_TIMEOUT            seconds allowed for the database recovery (default 3600)

set -u
# Git Bash on Windows rewrites arguments that look like paths (-v vol:/path); a Linux host ignores this.
export MSYS_NO_PATHCONV=1

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
REPO_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
PGDATA_DIR=/var/lib/postgresql/18/docker

log() {
    printf '%s restore: %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"
}

die() {
    log "FAILED: $*"
    if [ -n "${WORK:-}" ] && [ -d "$WORK" ]; then
        KEEP_WORK=1
        log "tool output kept in $WORK"
    fi
    if [ "${CREATED_VOLUME:-0}" -eq 1 ]; then
        log "before a new attempt remove: docker rm -f $RESTORE_PG_CONTAINER; docker volume rm $RESTORE_PG_VOLUME"
    fi
    exit 1
}

usage() {
    echo 'usage: restore.sh --target-time "AAAA-MM-DD HH:MM:SS-03" | --latest' >&2
    exit 2
}

# docker with a bound, so a stuck daemon fails a step instead of hanging it: dk <seconds> <docker args...>
dk() {
    secs=$1
    shift
    timeout "$secs" docker "$@"
}

TARGET=""
LATEST=0
while [ $# -gt 0 ]; do
    case "$1" in
        --target-time)
            [ $# -ge 2 ] || usage
            TARGET=$2
            shift 2
            ;;
        --latest)
            LATEST=1
            shift
            ;;
        *) usage ;;
    esac
done
if [ "$LATEST" -eq 1 ] && [ -n "$TARGET" ]; then usage; fi
if [ "$LATEST" -eq 0 ] && [ -z "$TARGET" ]; then usage; fi
if [ -n "$TARGET" ]; then
    case "$TARGET" in
        [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]\ [0-9][0-9]:[0-9][0-9]:[0-9][0-9][+-][0-9][0-9]) ;;
        [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]\ [0-9][0-9]:[0-9][0-9]:[0-9][0-9][+-][0-9][0-9]:[0-9][0-9]) ;;
        *) echo 'restore.sh: --target-time must look like "2026-11-07 14:32:00-03"' >&2; exit 2 ;;
    esac
fi

: "${AWS_REGION:=us-east-1}"
: "${AWS_S3_FORCE_PATH_STYLE:=true}"
: "${WALG_LIBSODIUM_KEY_TRANSFORM:=base64}"
: "${WALG_COMPRESSION_METHOD:=brotli}"
: "${RESTORE_PG_CONTAINER:=anamnys-restored-postgres}"
: "${RESTORE_PG_VOLUME:=$RESTORE_PG_CONTAINER-data}"
: "${RESTORE_PG_PUBLISH:=}"
: "${RESTORE_COUNTS_FILE:=}"
: "${RESTORE_SUMMARY_FILE:=}"
: "${RESTORE_TIMEOUT:=3600}"
export AWS_REGION AWS_S3_FORCE_PATH_STYLE WALG_LIBSODIUM_KEY_TRANSFORM WALG_COMPRESSION_METHOD

missing=""
for name in WALG_S3_PREFIX AWS_ENDPOINT AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY WALG_LIBSODIUM_KEY \
    BACKUP_S3_ENDPOINT BACKUP_S3_BUCKET BACKUP_S3_ACCESS_KEY BACKUP_S3_SECRET_KEY BACKUP_ENCRYPTION_KEY \
    RESTORE_NETWORK RESTORE_S3_ENDPOINT RESTORE_S3_BUCKET RESTORE_S3_ACCESS_KEY RESTORE_S3_SECRET_KEY; do
    eval "value=\${$name:-}"
    # shellcheck disable=SC2154 # value is assigned by the eval above
    [ -n "$value" ] || missing="$missing $name"
done
if [ -n "$missing" ]; then
    echo "restore.sh: missing environment:$missing" >&2
    exit 2
fi

umask 077
WORK=$(mktemp -d "${TMPDIR:-/tmp}/anamnys-restore.XXXXXX") || exit 1
KEEP_WORK=0
cleanup_work() {
    [ "$KEEP_WORK" -eq 1 ] || rm -rf "$WORK"
}
trap cleanup_work EXIT
trap 'exit 130' INT TERM

# Images: given, or built from this repository (the same Dockerfiles the AppHost builds).
if [ -z "${RESTORE_PG_IMAGE:-}" ]; then
    RESTORE_PG_IMAGE=anamnys-postgres-backup:restore
    log "building $RESTORE_PG_IMAGE from postgres/"
    dk 1800 build -q -t "$RESTORE_PG_IMAGE" "$REPO_DIR/postgres" > "$WORK/build-pg.log" 2>&1 \
        || die "building the PostgreSQL image"
fi
if [ -z "${RESTORE_AGENT_IMAGE:-}" ]; then
    RESTORE_AGENT_IMAGE=anamnys-backup-agent:restore
    log "building $RESTORE_AGENT_IMAGE from backup-agent/"
    dk 1800 build -q -t "$RESTORE_AGENT_IMAGE" "$REPO_DIR/backup-agent" > "$WORK/build-agent.log" 2>&1 \
        || die "building the backup-agent image"
fi

# Secrets reach containers as "-e NAME" (value taken from this environment), never on a command line.
WALG_ENV="-e WALG_S3_PREFIX -e AWS_ENDPOINT -e AWS_REGION -e AWS_ACCESS_KEY_ID -e AWS_SECRET_ACCESS_KEY
 -e AWS_S3_FORCE_PATH_STYLE -e WALG_LIBSODIUM_KEY -e WALG_LIBSODIUM_KEY_TRANSFORM -e WALG_COMPRESSION_METHOD"
RCLONE_ENV="-e BACKUP_S3_ENDPOINT -e BACKUP_S3_BUCKET -e BACKUP_S3_ACCESS_KEY -e BACKUP_S3_SECRET_KEY
 -e BACKUP_ENCRYPTION_KEY -e RESTORE_S3_ENDPOINT -e RESTORE_S3_BUCKET -e RESTORE_S3_ACCESS_KEY -e RESTORE_S3_SECRET_KEY"
export BACKUP_S3_ENDPOINT BACKUP_S3_BUCKET BACKUP_S3_ACCESS_KEY BACKUP_S3_SECRET_KEY BACKUP_ENCRYPTION_KEY
export RESTORE_S3_ENDPOINT RESTORE_S3_BUCKET RESTORE_S3_ACCESS_KEY RESTORE_S3_SECRET_KEY
export WALG_S3_PREFIX AWS_ENDPOINT AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY WALG_LIBSODIUM_KEY

# The agent's rclone config (crypt remotes backupcrypt: and metacrypt:) plus restoredst:, the new object store, all
# defined by RCLONE_CONFIG_* inside the container. Used as: agent_sh <seconds> '<shell commands>'.
RCLONE_SETUP='set -eu
sed -e "s|@SOURCE_ENDPOINT@|$BACKUP_S3_ENDPOINT|g" -e "s|@DEST_ENDPOINT@|$BACKUP_S3_ENDPOINT|g" \
    -e "s|@DEST_BUCKET@|$BACKUP_S3_BUCKET|g" /etc/backup-agent/rclone.conf.template > /tmp/rclone.conf
export RCLONE_CONFIG=/tmp/rclone.conf RCLONE_CONTIMEOUT=60s RCLONE_TIMEOUT=5m
export RCLONE_CONFIG_BACKUPDEST_ACCESS_KEY_ID="$BACKUP_S3_ACCESS_KEY"
export RCLONE_CONFIG_BACKUPDEST_SECRET_ACCESS_KEY="$BACKUP_S3_SECRET_KEY"
obscured=$(printf "%s" "$BACKUP_ENCRYPTION_KEY" | rclone obscure -)
export RCLONE_CONFIG_BACKUPCRYPT_PASSWORD="$obscured" RCLONE_CONFIG_METACRYPT_PASSWORD="$obscured"
export RCLONE_CONFIG_RESTOREDST_TYPE=s3 RCLONE_CONFIG_RESTOREDST_PROVIDER=Other
export RCLONE_CONFIG_RESTOREDST_ENDPOINT="$RESTORE_S3_ENDPOINT" RCLONE_CONFIG_RESTOREDST_FORCE_PATH_STYLE=true
export RCLONE_CONFIG_RESTOREDST_ACCESS_KEY_ID="$RESTORE_S3_ACCESS_KEY"
export RCLONE_CONFIG_RESTOREDST_SECRET_ACCESS_KEY="$RESTORE_S3_SECRET_KEY"
export RCLONE_CONFIG_WALDEST_TYPE=s3 RCLONE_CONFIG_WALDEST_PROVIDER=Other RCLONE_CONFIG_WALDEST_ENDPOINT="$AWS_ENDPOINT"
export RCLONE_CONFIG_WALDEST_FORCE_PATH_STYLE=true RCLONE_CONFIG_WALDEST_REGION="$AWS_REGION"
export RCLONE_CONFIG_WALDEST_ACCESS_KEY_ID="$AWS_ACCESS_KEY_ID"
export RCLONE_CONFIG_WALDEST_SECRET_ACCESS_KEY="$AWS_SECRET_ACCESS_KEY"
'
# The destination bucket must be new or empty: the object restore copies into it (copy, not sync), so a bucket that
# already holds objects (worst case the live store) would have them silently reverted. Creates the bucket when absent.
# Run inside agent_sh; the bucket name is not a secret.
DEST_EMPTY_CHECK='
rclone mkdir "restoredst:$RESTORE_S3_BUCKET"
rclone lsf -R --files-only "restoredst:$RESTORE_S3_BUCKET" > /tmp/dest-before.txt
if [ -s /tmp/dest-before.txt ]; then
    echo "restore-error: destination bucket $RESTORE_S3_BUCKET already contains objects; restore only into a new or empty bucket" >&2
    exit 1
fi
'
agent_sh() {
    secs=$1
    # shellcheck disable=SC2086 # the env lists are deliberately split into words
    dk "$secs" run --rm --network "$RESTORE_NETWORK" $WALG_ENV $RCLONE_ENV -e TARGET_EPOCH -e TARGET_MINUTE \
        -e BACKUP_NAME -e WAL_PATH --entrypoint sh "$RESTORE_AGENT_IMAGE" -c "$RCLONE_SETUP$2"
}

# WAL-G's segments, read raw (names are plain; contents stay encrypted): <bucket>/<path>/wal_005/ of WALG_S3_PREFIX.
WAL_PATH=${WALG_S3_PREFIX#s3://}
WAL_PATH=${WAL_PATH%/}
export WAL_PATH

# Prints the newest archived WAL segment name (24 hex digits); fails when the listing fails.
newest_archived_segment() {
    agent_sh 300 '
rclone lsf --files-only "waldest:$WAL_PATH/wal_005/" > /tmp/wal.txt
grep -E "^[0-9A-F]{24}([.]|$)" /tmp/wal.txt | cut -c1-24 | sort | tail -n 1
' 2> "$WORK/wal-list.err"
}

# The last segment PostgreSQL restored from the archive, from its log ($1).
last_restored_segment() {
    sed -n 's/.*restored log file "\([0-9A-F]\{24\}\)" from archive.*/\1/p' "$1" | tail -n 1
}

# Recovery ended at the end of the restored WAL (log $1): that must be the newest archived segment. Anything else is a
# gap, a fetch failure (network, key) or WAL archived meanwhile, and the restore fails rather than land elsewhere.
require_end_of_archive() {
    last=$(last_restored_segment "$1")
    [ -n "$last" ] || die "recovery restored no WAL segment from the archive (fetch failure?)"
    newest=$(newest_archived_segment) || die "listing the archived WAL segments"
    [ -n "$newest" ] || die "no archived WAL segment found under the WAL-G prefix"
    if [ "$last" != "$newest" ]; then
        die "recovery stopped at WAL segment $last but the archive holds segments up to $newest: a gap or a fetch failure (or WAL archived since the recovery started: run the restore again)"
    fi
    log "recovery reached the newest archived WAL segment ($last)"
}

# The target in UTC (GNU date in the agent image, so the host's date does not matter).
TARGET_EPOCH=""
TARGET_MINUTE=""
if [ -n "$TARGET" ]; then
    TARGET_EPOCH=$(dk 120 run --rm --entrypoint date "$RESTORE_AGENT_IMAGE" -u -d "$TARGET" +%s 2> "$WORK/date.err") \
        || die "could not read the target time"
    TARGET_MINUTE=$(dk 120 run --rm --entrypoint date "$RESTORE_AGENT_IMAGE" -u -d "@$TARGET_EPOCH" +%Y-%m-%dT%H%M \
        2> "$WORK/date.err") || die "could not read the target time"
    log "target $TARGET (UTC minute $TARGET_MINUTE)"
else
    log "target: latest"
fi
export TARGET_EPOCH TARGET_MINUTE

# Nothing may be overwritten: the container and the volume must be new.
if dk 60 container inspect "$RESTORE_PG_CONTAINER" > /dev/null 2>&1; then
    die "container $RESTORE_PG_CONTAINER already exists"
fi
if dk 60 volume inspect "$RESTORE_PG_VOLUME" > /dev/null 2>&1; then
    die "volume $RESTORE_PG_VOLUME already exists"
fi
dk 60 network inspect "$RESTORE_NETWORK" > /dev/null 2>&1 || die "network $RESTORE_NETWORK does not exist"
# The same holds for the object store, checked now so a wrong bucket fails before the long database restore (and again
# right before the copy).
agent_sh 300 "$DEST_EMPTY_CHECK" > /dev/null 2> "$WORK/dest-check.err" \
    || die "checking the destination bucket$(sed -n "s/^restore-error: / (/p" "$WORK/dest-check.err" | head -n 1 | sed "s/$/)/")"

# 1. The full backup: the newest that finished before the target (or the newest).
log "listing full backups"
agent_sh 300 '
wal-g backup-list --detail --json > /tmp/list.json 2> /tmp/list.err || { cat /tmp/list.err >&2; exit 1; }
[ -s /tmp/list.json ] || exit 0
jq -r "if type == \"array\" then .[] else empty end
       | [.backup_name,
          (.finish_time | sub(\"\\\\.[0-9]+\"; \"\") | sub(\"(Z|[+-][0-9]{2}:[0-9]{2})$\"; \"Z\") | fromdateiso8601),
          .finish_time] | @tsv" /tmp/list.json
' > "$WORK/backups.tsv" 2> "$WORK/backups.err" || die "wal-g backup-list"
sort -t "$(printf '\t')" -k2,2n "$WORK/backups.tsv" > "$WORK/backups-sorted.tsv"
if [ -n "$TARGET_EPOCH" ]; then
    awk -F '\t' -v t="$TARGET_EPOCH" '$2 < t' "$WORK/backups-sorted.tsv" > "$WORK/candidates.tsv"
else
    cp "$WORK/backups-sorted.tsv" "$WORK/candidates.tsv"
fi
[ -s "$WORK/candidates.tsv" ] || die "no full backup finished before the target"
BACKUP_NAME=$(tail -n 1 "$WORK/candidates.tsv" | cut -f1)
BACKUP_FINISHED=$(tail -n 1 "$WORK/candidates.tsv" | cut -f3)
BACKUP_FINISHED_EPOCH=$(tail -n 1 "$WORK/candidates.tsv" | cut -f2)
export BACKUP_NAME
log "full backup $BACKUP_NAME (finished $BACKUP_FINISHED)"

# 2. Fetch into a new volume, write the recovery settings, start, wait for promotion.
dk 60 volume create "$RESTORE_PG_VOLUME" > /dev/null || die "creating volume $RESTORE_PG_VOLUME"
CREATED_VOLUME=1
log "fetching the full backup into volume $RESTORE_PG_VOLUME"
if [ -n "$TARGET" ]; then
    RECOVERY_TARGET_LINE="recovery_target_time = '$TARGET'"
else
    RECOVERY_TARGET_LINE=""
fi
export RECOVERY_TARGET_LINE
# shellcheck disable=SC2086
dk 7200 run --rm --network "$RESTORE_NETWORK" $WALG_ENV -e BACKUP_NAME -e RECOVERY_TARGET_LINE \
    -e PGDATA="$PGDATA_DIR" -v "$RESTORE_PG_VOLUME:/var/lib/postgresql" --entrypoint sh "$RESTORE_PG_IMAGE" -c '
set -eu
mkdir -p "$PGDATA"
chown postgres:postgres "$PGDATA"
chmod 0700 "$PGDATA"
gosu postgres wal-g backup-fetch "$PGDATA" "$BACKUP_NAME"
gosu postgres touch "$PGDATA/recovery.signal"
{
    echo "# anamnys restore.sh: point-in-time recovery from the backup bucket"
    echo "restore_command = '"'"'wal-g wal-fetch %f %p'"'"'"
    echo "recovery_target_action = '"'"'promote'"'"'"
    echo "recovery_target_timeline = '"'"'latest'"'"'"
    [ -z "$RECOVERY_TARGET_LINE" ] || echo "$RECOVERY_TARGET_LINE"
} >> "$PGDATA/postgresql.auto.conf"
' > "$WORK/fetch.log" 2>&1 || die "wal-g backup-fetch"
log "full backup fetched"

start_postgres() {
    publish=""
    [ -z "$RESTORE_PG_PUBLISH" ] || publish="-p $RESTORE_PG_PUBLISH"
    # Archiving off: the restored server must never write into the backup bucket (its WAL is a new timeline).
    # shellcheck disable=SC2086
    dk 120 run -d --name "$RESTORE_PG_CONTAINER" --network "$RESTORE_NETWORK" $publish $WALG_ENV \
        -e PGDATA="$PGDATA_DIR" -v "$RESTORE_PG_VOLUME:/var/lib/postgresql" "$RESTORE_PG_IMAGE" \
        -c archive_mode=off > "$WORK/run.log" 2>&1
}

# Prints "promoted", "running" or "stopped".
pg_state() {
    running=$(dk 30 inspect -f '{{.State.Running}}' "$RESTORE_PG_CONTAINER" 2> /dev/null)
    [ "$running" = "true" ] || { echo stopped; return 0; }
    if dk 30 exec -u postgres "$RESTORE_PG_CONTAINER" pg_controldata -D "$PGDATA_DIR" 2> /dev/null \
            | grep -q 'cluster state: *in production' \
        && dk 30 exec -u postgres "$RESTORE_PG_CONTAINER" pg_isready -q -h localhost > /dev/null 2>&1; then
        echo promoted
    else
        echo running
    fi
}

# Waits for promotion. Returns 0 promoted, 1 failed, 3 when the WAL ended before the target (the container is gone).
wait_promoted() {
    deadline=$(( $(date +%s) + RESTORE_TIMEOUT ))
    while :; do
        state=$(pg_state)
        case "$state" in
            promoted) return 0 ;;
            stopped)
                dk 60 logs "$RESTORE_PG_CONTAINER" > "$WORK/postgres.log" 2>&1
                dk 60 rm -f "$RESTORE_PG_CONTAINER" > /dev/null 2>&1
                if grep -q 'recovery ended before configured recovery target was reached' "$WORK/postgres.log"; then
                    return 3
                fi
                return 1
                ;;
        esac
        [ "$(date +%s)" -lt "$deadline" ] || { dk 60 logs "$RESTORE_PG_CONTAINER" > "$WORK/postgres.log" 2>&1; return 1; }
        sleep 5
    done
}

MODE=latest
[ -z "$TARGET" ] || MODE=target
log "starting PostgreSQL in recovery ($RESTORE_PG_CONTAINER)"
start_postgres || die "starting PostgreSQL"
wait_promoted
result=$?
if [ "$result" -eq 3 ]; then
    # The first run replayed every archived record up to "redo done at <LSN>" and found no commit after the target.
    # The retry is bounded to exactly that point (recovery_target_lsn, inclusive), so WAL archived since then (the
    # live server keeps archiving) or a segment that fetches now but failed then can never take it past the target;
    # and it is taken only when the first run reached the newest archived segment (no gap, no fetch failure).
    cp "$WORK/postgres.log" "$WORK/postgres-first.log"
    REDO_LSN=$(sed -n 's/.*redo done at \([0-9A-F]\{1,8\}\/[0-9A-F]\{1,8\}\).*/\1/p' "$WORK/postgres-first.log" | tail -n 1)
    [ -n "$REDO_LSN" ] || die "recovery ended before the target and its end point (redo done at) is not in the log"
    log "the archived WAL ends before any commit after the target (redo done at $REDO_LSN)"
    require_end_of_archive "$WORK/postgres-first.log"
    log "recovering to exactly that point (recovery_target_lsn $REDO_LSN, inclusive): every commit up to it is before the target"
    MODE=end-of-wal-before-target
    export REDO_LSN
    # shellcheck disable=SC2086
    dk 300 run --rm -e PGDATA="$PGDATA_DIR" -e REDO_LSN -v "$RESTORE_PG_VOLUME:/var/lib/postgresql" --entrypoint sh \
        "$RESTORE_PG_IMAGE" -c 'set -eu
sed -i "/^recovery_target_time = /d" "$PGDATA/postgresql.auto.conf"
{
    echo "recovery_target_lsn = '"'"'$REDO_LSN'"'"'"
    echo "recovery_target_inclusive = on"
} >> "$PGDATA/postgresql.auto.conf"
[ -f "$PGDATA/recovery.signal" ] || gosu postgres touch "$PGDATA/recovery.signal"' > "$WORK/retarget.log" 2>&1 \
        || die "setting the recovery target LSN"
    start_postgres || die "starting PostgreSQL"
    wait_promoted
    result=$?
fi
[ "$result" -eq 0 ] || die "PostgreSQL recovery did not complete (log: $WORK/postgres.log)"
dk 60 logs "$RESTORE_PG_CONTAINER" > "$WORK/postgres.log" 2>&1
if [ "$MODE" = latest ]; then
    # Recovery to the end of the archive promotes at the first segment it cannot fetch: it must be the end.
    require_end_of_archive "$WORK/postgres.log"
fi
TIMELINE=$(sed -n 's/.*selected new timeline ID: \([0-9]*\).*/\1/p' "$WORK/postgres.log" | tail -n 1)
log "PostgreSQL promoted (timeline ${TIMELINE:-unknown}, mode $MODE)"

# 3. Objects.
log "restoring objects into bucket $RESTORE_S3_BUCKET"
agent_sh 7200 "$DEST_EMPTY_CHECK"'
# Every listing goes to a file and its exit code counts (set -e): a failure on the left of a pipe or inside an if
# would otherwise pass as "nothing there".
rclone lsf --dirs-only "backupdest:$BACKUP_S3_BUCKET/" > /tmp/bucket-top.txt
: > /tmp/raw-top.txt
: > /tmp/crypt-top.txt
# files/ exists once the agent has synced at least one object.
if grep -qx "files/" /tmp/bucket-top.txt; then
    rclone lsf --dirs-only "backupdest:$BACKUP_S3_BUCKET/files/" > /tmp/raw-top.txt
    rclone lsf --dirs-only backupcrypt: > /tmp/crypt-top.txt
fi
# A folder name that does not decrypt (wrong key) is skipped by the crypt view: the counts must agree.
if [ "$(wc -l < /tmp/raw-top.txt)" -ne "$(wc -l < /tmp/crypt-top.txt)" ]; then
    echo "restore-error: object backup folders do not decrypt (wrong BACKUP_ENCRYPTION_KEY?)" >&2
    exit 1
fi
current=0
if grep -qx "current/" /tmp/crypt-top.txt; then
    rclone lsf -R --files-only backupcrypt:current > /tmp/current.txt
    current=$(wc -l < /tmp/current.txt)
    rclone copy backupcrypt:current "restoredst:$RESTORE_S3_BUCKET" --log-level ERROR --transfers 8
fi
echo "current $current"
overlaid=0
: > /tmp/versions.txt
if [ -n "$TARGET_MINUTE" ] && grep -qx "versions/" /tmp/crypt-top.txt; then
    rclone lsf --dirs-only backupcrypt:versions/ > /tmp/versions-raw.txt
    sed "s|/$||" /tmp/versions-raw.txt | sort -r > /tmp/versions.txt
fi
if [ -s /tmp/versions.txt ]; then
    while read -r version; do
        case "$version" in
            [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9][0-9][0-9]) ;;
            *) echo "restore-error: unrecognized version folder" >&2; exit 1 ;;
        esac
        # Same-format UTC stamps: lexical order is time order.
        if [ "$(printf "%s\n%s\n" "$version" "$TARGET_MINUTE" | sort | head -n 1)" = "$TARGET_MINUTE" ]; then
            rclone copy "backupcrypt:versions/$version" "restoredst:$RESTORE_S3_BUCKET" --log-level ERROR --transfers 8
            overlaid=$((overlaid + 1))
            echo "overlaid $version"
        fi
    done < /tmp/versions.txt
fi
rclone lsf -R --files-only "restoredst:$RESTORE_S3_BUCKET" > /tmp/restored.txt
restored=$(wc -l < /tmp/restored.txt)
# The destination was empty before the copy (DEST_EMPTY_CHECK) and every object of current/ is copied (version folders
# only add or replace), so fewer restored than current/ holds means a copy failed.
if [ "$restored" -lt "$current" ]; then
    echo "restore-error: restored $restored objects, but the backup holds $current" >&2
    exit 1
fi
echo "objects $restored"
echo "versions_overlaid $overlaid"
' > "$WORK/objects.log" 2> "$WORK/objects.err" || die "restoring objects$(sed -n "s/^restore-error: / (/p" "$WORK/objects.err" | head -n 1 | sed "s/$/)/")"
OBJECTS=$(sed -n 's/^objects //p' "$WORK/objects.log")
CURRENT_OBJECTS=$(sed -n 's/^current //p' "$WORK/objects.log")
[ "$CURRENT_OBJECTS" -gt 0 ] || log "WARNING: the object backup (files/current/) is empty or absent"
OVERLAID=$(sed -n 's/^versions_overlaid //p' "$WORK/objects.log")
OVERLAID_LIST=$(sed -n 's/^overlaid //p' "$WORK/objects.log" | tr '\n' ' ' | sed 's/ $//')
log "objects restored: $OBJECTS (version folders overlaid: $OVERLAID${OVERLAID_LIST:+, $OVERLAID_LIST})"

# 4. The row-count snapshot of the backup used.
COUNTS_STATUS=not-requested
if [ -n "$RESTORE_COUNTS_FILE" ]; then
    if agent_sh 300 'rclone cat "metacrypt:counts/$BACKUP_NAME.json"' > "$WORK/counts.json" 2> "$WORK/counts.err" \
        && [ -s "$WORK/counts.json" ]; then
        cp "$WORK/counts.json" "$RESTORE_COUNTS_FILE"
        COUNTS_STATUS=written
        log "row-count snapshot of $BACKUP_NAME written"
    else
        COUNTS_STATUS=missing
        log "WARNING: no row-count snapshot for $BACKUP_NAME (RestoreCheck cannot compare row counts; the drill fails)"
    fi
fi

if [ -n "$RESTORE_SUMMARY_FILE" ]; then
    {
        echo "backup=$BACKUP_NAME"
        echo "backup_finished=$BACKUP_FINISHED"
        echo "backup_finished_epoch=$BACKUP_FINISHED_EPOCH"
        echo "target=$TARGET"
        echo "target_minute_utc=$TARGET_MINUTE"
        echo "mode=$MODE"
        echo "timeline=$TIMELINE"
        echo "objects=$OBJECTS"
        echo "versions_overlaid=$OVERLAID"
        echo "versions_overlaid_list=$OVERLAID_LIST"
        echo "counts=$COUNTS_STATUS"
    } > "$RESTORE_SUMMARY_FILE"
fi

log "done. PostgreSQL: container $RESTORE_PG_CONTAINER (volume $RESTORE_PG_VOLUME); objects: bucket $RESTORE_S3_BUCKET."
log "next: run anamnys-aspire.RestoreCheck against them; start the application server only after it passes."
