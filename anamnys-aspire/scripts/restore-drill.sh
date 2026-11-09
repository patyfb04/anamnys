#!/bin/sh
# shellcheck disable=SC2016 # single-quoted scripts run inside containers; their $ expand there, on purpose
# Anamnys restore drill (design/specs/2026-11-05-backup-restore-design.md §4.3). Restores the backup of a running setup
# (locally: the AppHost with Parameters:backup-enabled=true, whose backup-store SeaweedFS is the destination) into a
# throwaway Docker network, verifies it with anamnys-aspire.RestoreCheck, measures RPO and RTO against the goals and
# writes design/drills/AAAA-MM-DD-restore-drill.md. The throwaway environment is always torn down.
#
#   scripts/restore-drill.sh [--target-time "AAAA-MM-DD HH:MM:SS-03"] [--origin <name>]
#
# Default target: the live database's now() minus 5 minutes. Access to the live setup: the backup bucket (read), the
# live object store (read, RestoreCheck's drill-only SHA-256 comparison) and the live database (one read-only
# transaction: its clock, and the newest AuditEntries.At / IntegritySeals.SealedAt / BackupRuns.FinishedAt at or before
# the target). Nothing else of the live setup is touched; the throwaway containers have no access to the live
# containers' network.
#
# Environment (secrets only through the environment; none is printed or written to the record):
#   BACKUP_ENCRYPTION_KEY                        AppHost parameter backup-encryption-key (required)
#   BACKUP_S3_ACCESS_KEY BACKUP_S3_SECRET_KEY    parameters backup-store-access-key / backup-store-secret-key (required)
#   BACKUP_S3_ENDPOINT                           backup store URL from this host (default: the published port of the
#                                                running backup-store-* container, http://localhost:<port>)
#   BACKUP_S3_BUCKET                             default anamnys-backup
#   SOURCE_S3_ACCESS_KEY SOURCE_S3_SECRET_KEY    parameters storage-access-key / storage-secret-key (required)
#   SOURCE_S3_ENDPOINT                           live object store URL from this host (default: storage-* container)
#   SOURCE_S3_BUCKET                             default notes
#   PGPASSWORD                                   parameter postgres-password (required; the restored cluster keeps the
#                                                live superuser and its password)
#   PGUSER                                       default postgres
#   LIVE_PG_PORT                                 live PostgreSQL port on this host (default: postgres-* container)
#   ANAMNYS_DB KEYCLOAK_DB                       default anamnysdb / keycloakdb
#   DRILL_CONTAINER_HOST                         how a container reaches this host (default host.docker.internal)
#   DRILL_PG_IMAGE DRILL_AGENT_IMAGE             images to use (default: built from postgres/ and backup-agent/)
#   DRILL_RECORD_FILE                            default design/drills/<today>-restore-drill.md (-2, -3 if taken)
#   DRILL_TIMEOUT                                seconds for the restore (default 7200, the RTO goal)

set -u
export MSYS_NO_PATHCONV=1

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
REPO_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
# Host path as the native docker CLI reads it: C:/... under Git Bash (pwd -W), the POSIX path elsewhere.
DOCKER_REPO_DIR=$(cd "$SCRIPT_DIR/.." && { pwd -W 2>/dev/null || pwd; })
GOAL_RPO_DB=60
GOAL_RPO_FILES=900
GOAL_RTO=7200

say() {
    printf '%s drill: %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"
}

usage() {
    echo 'usage: restore-drill.sh [--target-time "AAAA-MM-DD HH:MM:SS-03"] [--origin <name>]' >&2
    exit 2
}

dk() {
    secs=$1
    shift
    timeout "$secs" docker "$@"
}

TARGET=""
ORIGIN=local
while [ $# -gt 0 ]; do
    case "$1" in
        --target-time) [ $# -ge 2 ] || usage; TARGET=$2; shift 2 ;;
        --origin) [ $# -ge 2 ] || usage; ORIGIN=$2; shift 2 ;;
        *) usage ;;
    esac
done

: "${BACKUP_S3_BUCKET:=anamnys-backup}"
: "${SOURCE_S3_BUCKET:=notes}"
: "${PGUSER:=postgres}"
: "${ANAMNYS_DB:=anamnysdb}"
: "${KEYCLOAK_DB:=keycloakdb}"
: "${DRILL_CONTAINER_HOST:=host.docker.internal}"
: "${DRILL_TIMEOUT:=7200}"

missing=""
for name in BACKUP_ENCRYPTION_KEY BACKUP_S3_ACCESS_KEY BACKUP_S3_SECRET_KEY SOURCE_S3_ACCESS_KEY SOURCE_S3_SECRET_KEY \
    PGPASSWORD; do
    eval "value=\${$name:-}"
    # shellcheck disable=SC2154 # value is assigned by the eval above
    [ -n "$value" ] || missing="$missing $name"
done
if [ -n "$missing" ]; then
    echo "restore-drill.sh: missing environment:$missing (see the header of this script)" >&2
    exit 2
fi
export PGUSER PGPASSWORD

# The host port a running live container publishes: published_port <name prefix> <container port>.
published_port() {
    container=$(dk 30 ps --filter "name=^$1-" --format '{{.Names}}' | head -n 1)
    [ -n "$container" ] || return 1
    dk 30 port "$container" "$2/tcp" | head -n 1 | sed 's/.*://'
}

if [ -z "${BACKUP_S3_ENDPOINT:-}" ]; then
    port=$(published_port backup-store 8333) || { echo "restore-drill.sh: no backup-store container; set BACKUP_S3_ENDPOINT" >&2; exit 2; }
    BACKUP_S3_ENDPOINT=http://localhost:$port
fi
if [ -z "${SOURCE_S3_ENDPOINT:-}" ]; then
    port=$(published_port storage 8333) || { echo "restore-drill.sh: no storage container; set SOURCE_S3_ENDPOINT" >&2; exit 2; }
    SOURCE_S3_ENDPOINT=http://localhost:$port
fi
if [ -z "${LIVE_PG_PORT:-}" ]; then
    LIVE_PG_PORT=$(published_port postgres 5432) || { echo "restore-drill.sh: no postgres container; set LIVE_PG_PORT" >&2; exit 2; }
fi

# The same URL as a container sees it.
container_url() {
    printf '%s' "$1" | sed -e "s#//localhost:#//$DRILL_CONTAINER_HOST:#" -e "s#//127\.0\.0\.1:#//$DRILL_CONTAINER_HOST:#"
}
BACKUP_S3_ENDPOINT_C=$(container_url "$BACKUP_S3_ENDPOINT")

PREFIX="anamnys-drill-$(date -u +%Y%m%d%H%M%S)-$$"
NET="$PREFIX-net"
PG="$PREFIX-pg"
S3="$PREFIX-s3"
umask 077
WORK=$(mktemp -d "${TMPDIR:-/tmp}/anamnys-drill.XXXXXX") || exit 1

teardown() {
    say "tearing down $PREFIX"
    dk 120 rm -f -v "$PG" "$S3" > /dev/null 2>&1
    dk 120 volume rm "$PG-data" > /dev/null 2>&1
    dk 120 network rm "$NET" > /dev/null 2>&1
    rm -rf "$WORK"
    if dk 30 ps -a --filter "name=^$PREFIX" --format '{{.Names}}' | grep -q . \
        || dk 30 volume ls --filter "name=^$PREFIX" --format '{{.Name}}' | grep -q .; then
        say "WARNING: leftovers named $PREFIX* remain; remove them by hand"
    fi
}
trap teardown EXIT
trap 'exit 130' INT TERM

TODAY=$(date +%Y-%m-%d)
# Sets DRILL_RECORD_FILE (design/drills/<today>-restore-drill.md, -2, -3 when taken) unless given.
record_path() {
    [ -z "${DRILL_RECORD_FILE:-}" ] || return 0
    mkdir -p "$REPO_DIR/design/drills"
    DRILL_RECORD_FILE="$REPO_DIR/design/drills/$TODAY-restore-drill.md"
    n=2
    while [ -e "$DRILL_RECORD_FILE" ]; do
        DRILL_RECORD_FILE="$REPO_DIR/design/drills/$TODAY-restore-drill-$n.md"
        n=$((n + 1))
    done
}

# A drill that fails before the restore still leaves a record (verdict reprovado, with the reason).
setup_failed() {
    say "FAILED: $1"
    record_path
    {
        echo "# Restore drill, $TODAY"
        echo
        echo "Generated by \`scripts/restore-drill.sh\` (spec \`design/specs/2026-11-05-backup-restore-design.md\` §4.3)."
        echo
        echo "| Item | Value |"
        echo "|---|---|"
        echo "| Date | $TODAY |"
        echo "| Origin | $ORIGIN |"
        echo "| Target time | ${TARGET:-not set} |"
        echo "| Failed at | $1 |"
        echo
        echo "## Verdict"
        echo
        echo "**reprovado** (the drill stopped before the restore: $1)"
    } > "$DRILL_RECORD_FILE"
    say "verdict: reprovado; record: $DRILL_RECORD_FILE"
    exit 1
}

say "drill $PREFIX, origin $ORIGIN"
say "backup store $BACKUP_S3_ENDPOINT (bucket $BACKUP_S3_BUCKET), live store $SOURCE_S3_ENDPOINT, live PostgreSQL port $LIVE_PG_PORT"

# --- Preparation (before the RTO clock): images and RestoreCheck built.
if [ -z "${DRILL_PG_IMAGE:-}" ]; then
    DRILL_PG_IMAGE=anamnys-postgres-backup:drill
    say "building $DRILL_PG_IMAGE"
    dk 1800 build -q -t "$DRILL_PG_IMAGE" "$DOCKER_REPO_DIR/postgres" > "$WORK/build-pg.log" 2>&1 || setup_failed "image build"
fi
if [ -z "${DRILL_AGENT_IMAGE:-}" ]; then
    DRILL_AGENT_IMAGE=anamnys-backup-agent:drill
    say "building $DRILL_AGENT_IMAGE"
    dk 1800 build -q -t "$DRILL_AGENT_IMAGE" "$DOCKER_REPO_DIR/backup-agent" > "$WORK/build-agent.log" 2>&1 \
        || setup_failed "image build"
fi
say "building anamnys-aspire.RestoreCheck"
# dotnet is a native program on Windows: Git Bash must translate its path arguments, so the conversion is back on for it.
(
    unset MSYS_NO_PATHCONV
    exec timeout 900 dotnet build "$REPO_DIR/anamnys-aspire.RestoreCheck" -v q -nologo
) > "$WORK/build-check.log" 2>&1 || setup_failed "RestoreCheck build (see the build output by running it by hand)"

# psql in the agent image. Live: one read-only transaction through the host port. Restored: on the drill network.
psql_live() {
    dk 120 run --rm -i -e PGPASSWORD -e PGUSER -e PGOPTIONS="-c default_transaction_read_only=on" \
        --entrypoint psql "$DRILL_AGENT_IMAGE" -X -q -t -A -v ON_ERROR_STOP=1 --no-password \
        -h "$DRILL_CONTAINER_HOST" -p "$LIVE_PG_PORT" -d "$ANAMNYS_DB" "$@"
}
psql_restored() {
    dk 120 run --rm -i --network "$NET" -e PGPASSWORD -e PGUSER --entrypoint psql "$DRILL_AGENT_IMAGE" \
        -X -q -t -A -v ON_ERROR_STOP=1 --no-password -h "$PG" -p 5432 -d "$ANAMNYS_DB" "$@"
}

# --- Target and the newest live change at or before it (live clock: the WAL timestamps come from it).
say "reading the live database clock"
if [ -z "$TARGET" ]; then
    TARGET=$(printf '%s\n' "SET TIME ZONE 'America/Sao_Paulo';" \
        "SELECT to_char(now() - interval '5 minutes', 'YYYY-MM-DD HH24:MI:SSOF');" | psql_live 2> "$WORK/live.err") \
        || setup_failed "reading the live database"
fi
case "$TARGET" in
    [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]\ [0-9][0-9]:[0-9][0-9]:[0-9][0-9][+-][0-9][0-9]) ;;
    [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]\ [0-9][0-9]:[0-9][0-9]:[0-9][0-9][+-][0-9][0-9]:[0-9][0-9]) ;;
    *) setup_failed "unexpected target time format" ;;
esac
# The newest change: application writes (AuditEntries, IntegritySeals) and the backup agent's own BackupRuns row
# (written every 10 minutes), so even an idle database has a row written after the full backup that must come back
# through WAL replay.
CHANGE_SQL="SELECT coalesce(extract(epoch FROM greatest(
    (SELECT max(\"At\") FROM \"AuditEntries\" WHERE \"At\" <= :'t'::timestamptz),
    (SELECT max(\"SealedAt\") FROM \"IntegritySeals\" WHERE \"SealedAt\" <= :'t'::timestamptz),
    (SELECT max(\"FinishedAt\") FROM \"BackupRuns\" WHERE \"FinishedAt\" <= :'t'::timestamptz)))::bigint::text, '');"
LIVE_INFO=$(printf '%s\n' "SELECT extract(epoch FROM now())::bigint || ' ' || extract(epoch FROM :'t'::timestamptz)::bigint;" \
    "$CHANGE_SQL" | psql_live -v t="$TARGET" 2> "$WORK/live.err") || setup_failed "reading the live database"
LIVE_NOW=$(printf '%s\n' "$LIVE_INFO" | sed -n 1p | cut -d' ' -f1)
TARGET_EPOCH=$(printf '%s\n' "$LIVE_INFO" | sed -n 1p | cut -d' ' -f2)
LIVE_CHANGE=$(printf '%s\n' "$LIVE_INFO" | sed -n 2p)
HOST_NOW=$(date +%s)
SKEW=$((HOST_NOW - LIVE_NOW))
if [ -n "$LIVE_CHANGE" ]; then
    say "target $TARGET; newest live change at or before it: $((TARGET_EPOCH - LIVE_CHANGE)) s before the target"
else
    say "target $TARGET; no live change at or before it"
fi
CLOCK_NOTE=""
if [ "${SKEW#-}" -gt 60 ]; then
    CLOCK_NOTE="Host clock and Docker (database) clock differ by $SKEW s; times here use the database clock."
    say "NOTE: $CLOCK_NOTE"
fi

# --- RTO clock starts: throwaway environment, restore, RestoreCheck.
RTO_START=$(date +%s)
say "creating throwaway network $NET and object store $S3"
dk 60 network create "$NET" > /dev/null || setup_failed "network"
RESTORE_S3_ACCESS_KEY=$(od -An -tx1 -N16 /dev/urandom | tr -d ' \n')
RESTORE_S3_SECRET_KEY=$(od -An -tx1 -N24 /dev/urandom | tr -d ' \n')
S3_ACCESS_KEY=$RESTORE_S3_ACCESS_KEY
S3_SECRET_KEY=$RESTORE_S3_SECRET_KEY
export RESTORE_S3_ACCESS_KEY RESTORE_S3_SECRET_KEY S3_ACCESS_KEY S3_SECRET_KEY
dk 120 run -d --name "$S3" --network "$NET" -p 127.0.0.1::8333 -e S3_ACCESS_KEY -e S3_SECRET_KEY \
    --entrypoint /bin/sh chrislusf/seaweedfs:4.48 -c \
    'printf "{\"identities\":[{\"name\":\"restore\",\"credentials\":[{\"accessKey\":\"%s\",\"secretKey\":\"%s\"}],\"actions\":[\"Admin\",\"Read\",\"Write\",\"List\"]}]}" "$S3_ACCESS_KEY" "$S3_SECRET_KEY" > /tmp/s3.json && exec weed server -dir=/data -s3 -s3.port=8333 -s3.config=/tmp/s3.json -volume.max=0' \
    > /dev/null 2> "$WORK/s3.err" || setup_failed "throwaway object store"
S3_PORT=$(dk 30 port "$S3" 8333/tcp | head -n 1 | sed 's/.*://')
i=0
# Output through the shell, not curl -o: a native curl on Windows cannot open /dev/null.
until curl -fsS "http://127.0.0.1:$S3_PORT/healthz" > /dev/null 2>&1; do
    i=$((i + 1))
    [ "$i" -lt 60 ] || setup_failed "throwaway object store not healthy"
    sleep 2
done

say "restore (scripts/restore.sh) into $PG"
(
    export WALG_S3_PREFIX="s3://$BACKUP_S3_BUCKET/pg" AWS_ENDPOINT="$BACKUP_S3_ENDPOINT_C"
    export AWS_ACCESS_KEY_ID="$BACKUP_S3_ACCESS_KEY" AWS_SECRET_ACCESS_KEY="$BACKUP_S3_SECRET_KEY"
    export WALG_LIBSODIUM_KEY="$BACKUP_ENCRYPTION_KEY"
    export BACKUP_S3_ENDPOINT="$BACKUP_S3_ENDPOINT_C" BACKUP_S3_BUCKET
    export RESTORE_NETWORK="$NET" RESTORE_PG_CONTAINER="$PG" RESTORE_PG_VOLUME="$PG-data"
    # The destination bucket is always passed (restore.sh has no default and refuses a non-empty bucket): the live
    # bucket name, but in the throwaway store just created, so it is new and empty.
    export RESTORE_PG_PUBLISH=127.0.0.1::5432 RESTORE_S3_ENDPOINT="http://$S3:8333" RESTORE_S3_BUCKET="$SOURCE_S3_BUCKET"
    export RESTORE_PG_IMAGE="$DRILL_PG_IMAGE" RESTORE_AGENT_IMAGE="$DRILL_AGENT_IMAGE"
    export RESTORE_COUNTS_FILE="$WORK/counts.json" RESTORE_SUMMARY_FILE="$WORK/summary.txt" RESTORE_TIMEOUT="$DRILL_TIMEOUT"
    # --foreground: restore.sh stays in the drill's process group, so Ctrl-C stops it too (not only the drill).
    exec timeout --foreground "$DRILL_TIMEOUT" sh "$SCRIPT_DIR/restore.sh" --target-time "$TARGET"
)
RESTORE_EXIT=$?
summary() {
    [ -f "$WORK/summary.txt" ] && sed -n "s/^$1=//p" "$WORK/summary.txt"
}

CHECK_EXIT=1
CHECK_DURATION=""
RTO=""
R_CHANGE=""
SYNC_USED=""
REASONS=""
add_reason() {
    REASONS="$REASONS
- $1"
}
if [ "$RESTORE_EXIT" -eq 0 ]; then
    PG_PORT=$(dk 30 port "$PG" 5432/tcp | head -n 1 | sed 's/.*://')
    pw_quoted=$(printf '%s' "$PGPASSWORD" | sed 's/"/""/g')
    # The snapshot is required: without it row_counts would be "skipped" and keycloak only "> 0 users" (vacuous).
    [ -s "$WORK/counts.json" ] || add_reason "no row-count snapshot (meta/counts/<backup>.json) for the backup used"
    say "running RestoreCheck"
    (
        export RESTORECHECK_CONNECTION="Host=127.0.0.1;Port=$PG_PORT;Database=$ANAMNYS_DB;Username=$PGUSER;Password=\"$pw_quoted\""
        export RESTORECHECK_KEYCLOAK_CONNECTION="Host=127.0.0.1;Port=$PG_PORT;Database=$KEYCLOAK_DB;Username=$PGUSER;Password=\"$pw_quoted\""
        export RESTORECHECK_S3_ENDPOINT="http://127.0.0.1:$S3_PORT" RESTORECHECK_S3_BUCKET="$SOURCE_S3_BUCKET"
        export RESTORECHECK_S3_ACCESS_KEY="$RESTORE_S3_ACCESS_KEY" RESTORECHECK_S3_SECRET_KEY="$RESTORE_S3_SECRET_KEY"
        export RESTORECHECK_SOURCE_S3_ENDPOINT="$SOURCE_S3_ENDPOINT" RESTORECHECK_SOURCE_S3_BUCKET="$SOURCE_S3_BUCKET"
        # The live database, opened read-only (Options sets the session read-only): the baseline that tells
        # problems already in the live data from problems of the restore.
        export RESTORECHECK_BASELINE_CONNECTION="Host=127.0.0.1;Port=$LIVE_PG_PORT;Database=$ANAMNYS_DB;Username=$PGUSER;Password=\"$pw_quoted\";Options=\"-c default_transaction_read_only=on\""
        export RESTORECHECK_SOURCE_S3_ACCESS_KEY="$SOURCE_S3_ACCESS_KEY" RESTORECHECK_SOURCE_S3_SECRET_KEY="$SOURCE_S3_SECRET_KEY"
        # File paths (not secrets) as arguments, so Git Bash translates them for the native dotnet.
        # --counts always: a missing or empty file is a RestoreCheck setup failure, never a skipped check.
        set -- --tsa-root "$REPO_DIR/anamnys-aspire.AppHost/integrity/freetsa-cacert.pem" --counts "$WORK/counts.json"
        unset MSYS_NO_PATHCONV
        exec timeout 3600 dotnet run --no-build --no-launch-profile --project "$REPO_DIR/anamnys-aspire.RestoreCheck" -- "$@"
    ) > "$WORK/check.json" 2> "$WORK/check.err"
    CHECK_EXIT=$?
    [ "$CHECK_EXIT" -ne 0 ] || RTO=$(( $(date +%s) - RTO_START ))
    say "RestoreCheck exit $CHECK_EXIT"

    # RPO (database) input from the restored database: the newest change recovered at or before the target.
    R_CHANGE=$(printf '%s\n' "$CHANGE_SQL" | psql_restored -v t="$TARGET" 2> "$WORK/restored.err") \
        || { R_CHANGE=""; add_reason "could not read the restored database for RPO (database)"; }
    # RPO (objects) input from the live BackupRuns (read-only, after the restore): the sync the restore used is the
    # newest ok files run whose version stamp minute is before the target's minute, the folders restore.sh did not
    # overlay. The live table also has a run that finished after the target (absent from the restored database). The
    # stamp is taken a moment after StartedAt (agent.sh): StartedAt's minute stands for it; the seconds between are
    # the remaining imprecision.
    SYNC_USED=$(printf '%s\n' "SELECT coalesce(extract(epoch FROM max(\"StartedAt\"))::bigint::text, '') FROM \"BackupRuns\"
         WHERE \"Kind\" = 'files' AND \"Outcome\" = 'ok'
           AND date_trunc('minute', \"StartedAt\" AT TIME ZONE 'UTC') < date_trunc('minute', :'t'::timestamptz AT TIME ZONE 'UTC');" \
        | psql_live -v t="$TARGET" 2> "$WORK/live2.err") || { SYNC_USED=""; add_reason "could not read the live BackupRuns for RPO (objects)"; }
else
    say "restore failed (exit $RESTORE_EXIT)"
    add_reason "scripts/restore.sh failed (exit $RESTORE_EXIT)"
fi
[ "$CHECK_EXIT" -eq 0 ] || [ "$RESTORE_EXIT" -ne 0 ] || add_reason "RestoreCheck failed (exit $CHECK_EXIT)"

# --- Measurements against the goals.
# RPO (database) = target - newest change recovered at or before the target. When the restored database holds the
# newest live change at or before the target, nothing before the target was lost and the newest change recovered is
# taken as the target itself (an idle period before the target is not data loss): RPO 0.
RPO_DB=""
RPO_DB_NOTE=""
if [ "$RESTORE_EXIT" -eq 0 ]; then
    if [ -z "$LIVE_CHANGE" ]; then
        RPO_DB=0
        RPO_DB_NOTE="no change in the live database at or before the target"
    elif [ -n "$R_CHANGE" ] && [ "$R_CHANGE" -ge "$LIVE_CHANGE" ]; then
        RPO_DB=0
        RPO_DB_NOTE="the newest live change at or before the target ($((TARGET_EPOCH - LIVE_CHANGE)) s before it) was recovered"
    elif [ -n "$R_CHANGE" ]; then
        RPO_DB=$((TARGET_EPOCH - R_CHANGE))
        RPO_DB_NOTE="newest change recovered $RPO_DB s before the target; newest live change $((TARGET_EPOCH - LIVE_CHANGE)) s before it"
    else
        RPO_DB_NOTE="no change recovered although the live database has one"
    fi
fi
# Point-in-time replay actually exercised: a change after the full backup came back through WAL replay, and at least
# one object version folder was overlaid. Not a verdict condition, but the record says so when it was not.
BACKUP_FINISHED_EPOCH=$(summary backup_finished_epoch)
OVERLAID_COUNT=$(summary versions_overlaid)
REPLAYED=não
if [ -n "$R_CHANGE" ] && [ -n "$BACKUP_FINISHED_EPOCH" ] && [ "$R_CHANGE" -gt "$BACKUP_FINISHED_EPOCH" ]; then
    REPLAYED=sim
fi
OVERLAY_DONE=não
if [ -n "$OVERLAID_COUNT" ] && [ "$OVERLAID_COUNT" -gt 0 ]; then
    OVERLAY_DONE=sim
fi
PITR_NOTE=""
if [ "$RESTORE_EXIT" -eq 0 ] && { [ "$REPLAYED" != sim ] || [ "$OVERLAY_DONE" != sim ]; }; then
    PITR_NOTE="point-in-time replay not exercised"
    say "NOTE: $PITR_NOTE (WAL replay after the full: $REPLAYED; version folders overlaid: ${OVERLAID_COUNT:-0})"
fi
RPO_FILES=""
[ -z "$SYNC_USED" ] || RPO_FILES=$((TARGET_EPOCH - SYNC_USED))

meets() { [ -n "$1" ] && [ "$1" -le "$2" ] && echo sim || echo "não"; }
fmt() {
    if [ -z "$1" ]; then echo "não medido"; else printf '%d s (%dm%02ds)' "$1" $(($1 / 60)) $(($1 % 60)); fi
}
OK_RTO=$(meets "$RTO" "$GOAL_RTO")
OK_RPO_DB=$(meets "$RPO_DB" "$GOAL_RPO_DB")
OK_RPO_FILES=$(meets "$RPO_FILES" "$GOAL_RPO_FILES")
VERDICT=reprovado
[ "$OK_RTO" = sim ] || [ -z "$RTO" ] || add_reason "RTO above the goal"
[ "$OK_RPO_DB" = sim ] || add_reason "RPO (database) not measured or above the goal"
[ "$OK_RPO_FILES" = sim ] || add_reason "RPO (objects) not measured or above the goal"
if [ -z "$REASONS" ] && [ "$OK_RTO" = sim ] && [ "$RESTORE_EXIT" -eq 0 ] && [ "$CHECK_EXIT" -eq 0 ]; then
    VERDICT=aprovado
fi

# The RestoreCheck results as table rows (jq in the agent image; RestoreCheck output has no clinical text).
CHECK_ROWS=""
if [ -s "$WORK/check.json" ]; then
    CHECK_ROWS=$(dk 60 run --rm -i --entrypoint jq "$DRILL_AGENT_IMAGE" -r \
        '.checks[] | "| \(.name) | \(if .passed then "ok" else "falhou" end) | \(.detail // "" | tostring | gsub("[|\n]"; " ")) |"' \
        < "$WORK/check.json" 2> /dev/null)
    CHECK_DURATION=$(dk 60 run --rm -i --entrypoint jq "$DRILL_AGENT_IMAGE" -r '.durationSeconds // ""' \
        < "$WORK/check.json" 2> /dev/null)
fi
[ -n "$CHECK_ROWS" ] || CHECK_ROWS="| (sem resultado) | falhou | RestoreCheck não produziu resultado |"

# --- The record.
record_path
{
    echo "# Restore drill, $TODAY"
    echo
    echo "Generated by \`scripts/restore-drill.sh\` (spec \`design/specs/2026-11-05-backup-restore-design.md\` §4.3)."
    echo "No secrets and no clinical data: counts, times and check results only."
    echo
    echo "| Item | Value |"
    echo "|---|---|"
    echo "| Date | $TODAY |"
    echo "| Origin | $ORIGIN |"
    echo "| Target time | $TARGET |"
    echo "| Backup used | $(summary backup) (finished $(summary backup_finished)) |"
    echo "| Recovery mode | $(summary mode) (timeline $(summary timeline)) |"
    echo "| Objects restored | $(summary objects) (version folders overlaid: $(summary versions_overlaid)) |"
    echo "| WAL replay of a change after the full backup | $REPLAYED |"
    echo "| Object version folders overlaid | ${OVERLAID_COUNT:-0} ($OVERLAY_DONE) |"
    [ -z "$PITR_NOTE" ] || echo "| Point-in-time | **$PITR_NOTE** |"
    echo "| Row-count snapshot | $(summary counts) |"
    echo "| Restore exit code | $RESTORE_EXIT |"
    echo "| RestoreCheck exit code | $CHECK_EXIT${CHECK_DURATION:+ (${CHECK_DURATION} s)} |"
    echo
    echo "## RPO and RTO"
    echo
    echo "| Measure | Measured | Goal | Met |"
    echo "|---|---|---|---|"
    echo "| RPO, database | $(fmt "$RPO_DB") | 1 min | $OK_RPO_DB |"
    echo "| RPO, objects | $(fmt "$RPO_FILES") | 15 min | $OK_RPO_FILES |"
    echo "| RTO | $(fmt "$RTO") | 2 h | $OK_RTO |"
    echo
    echo "- RPO (database): target minus the newest change (AuditEntries.At, IntegritySeals.SealedAt or BackupRuns.FinishedAt)"
    echo "  recovered at or before the target; ${RPO_DB_NOTE:-not measured}."
    echo "- RPO (objects): target minus the start of the object sync the restore used (newest ok \`files\` run in"
    echo "  the live BackupRuns whose start minute (UTC) is before the target's minute, the version folders not overlaid)."
    echo "- RTO: from the creation of the throwaway environment to RestoreCheck passing (images and RestoreCheck were"
    echo "  built before the clock started)."
    echo "- WAL replay: \"sim\" when the newest change recovered (including the agent's BackupRuns rows) is later than"
    echo "  the full backup's finish, i.e. it came back through WAL replay. Overlay: version folders restore.sh copied over"
    echo "  current/. When either is \"não\" the drill did not exercise point-in-time recovery of that part (not a"
    echo "  verdict condition)."
    [ -z "$CLOCK_NOTE" ] || echo "- Clock: $CLOCK_NOTE"
    echo
    echo "## RestoreCheck"
    echo
    echo "| Check | Result | Detail |"
    echo "|---|---|---|"
    echo "$CHECK_ROWS"
    echo
    echo "## Verdict"
    echo
    echo "**$VERDICT**"
    [ -z "$REASONS" ] || { echo; echo "Reasons:"; echo "$REASONS" | sed '/^$/d'; }
    echo
    echo "\`aprovado\` only when every check passed and RPO and RTO meet the goals (1 min, 15 min, 2 h)."
} > "$DRILL_RECORD_FILE"

say "RPO database $(fmt "$RPO_DB") ($OK_RPO_DB), RPO objects $(fmt "$RPO_FILES") ($OK_RPO_FILES), RTO $(fmt "$RTO") ($OK_RTO)"
say "verdict: $VERDICT; record: $DRILL_RECORD_FILE"
[ "$VERDICT" = aprovado ]
