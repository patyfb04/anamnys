# Chapter 40: Backup and Point-in-Time Restore (F-46)

How everything the product stores is copied off the server, encrypted, continuously; how to bring it back on a new
server to a chosen moment; and how the restore is rehearsed, measured and recorded. Last checked against the code on
2026-11-07 (branch `feature/backup-restore`). Design: `design/specs/2026-11-05-backup-restore-design.md`. The passing
drill records are `design/drills/2026-11-07-restore-drill-4.md` and `design/drills/2026-11-08-restore-drill.md` (the first with a change replayed after the full and an object overlay).

What a future change has to respect: the word "cofre" is used only when the rule in section 1 holds; the encryption key
never lives only on the server (section 6); backups are encrypted before they leave the server and the bucket must
reveal nothing (section 2); the restored server never writes into the backup bucket (section 7); and a restore check
that passes with nothing to verify is a failure, not a pass (section 10).

---

## 1. Purpose and the "cofre" rule

Done when (backlog): backup is verified, with RPO and RTO tested and documented.

**The "cofre" rule.** The word "cofre" (vault) may appear in any outward material (site, proposals, in-app text,
messages) only when **both** hold: F-46 is running in production, and the newest drill in `design/drills/` has the
verdict `aprovado` and is **less than 90 days old**. Until a production deployment exists the word is not used, even
though the local drill passed. A drill older than 90 days withdraws the right until a new one passes.

Out of scope: an administration screen for backups; a live replica; the production deployment itself (chapter 17
describes what it must supply); automatic rotation of the encryption key (manual procedure, section 6).

---

## 2. Architecture

```
 PostgreSQL (anamnysdb + keycloakdb) --wal-push (archive_command)-->  +--------------------------------+
        ^                                                             |  backup bucket (any S3,        |
        | backup-push, row counts, run log                            |  off the server)               |
 backup-agent (sh scheduler) --- wal-g backup-push ------------------>|  pg/            WAL-G          |
        |                                                             |  files/         rclone crypt   |
        +-- rclone sync <-- SeaweedFS "notes" bucket ---------------->|  meta/counts/   row counts     |
                                                                      +--------------------------------+
 Server: BackupMonitor (every 15 min) reads pg_stat_archiver + BackupRuns, e-mails the administrator
```

- **PostgreSQL image** (`postgres/Dockerfile`): `postgres:18.3` (the tag Aspire uses), WAL-G v3.0.9 checked by
  SHA-256, `ca-certificates`, and `postgres/pg_hba.conf`. With backup disabled the image behaves exactly like the stock
  one. With backup enabled the AppHost passes `archive_mode=on`, `archive_command=timeout 60 wal-g wal-push %p`,
  `archive_timeout=60`, `wal_level=replica`, `hba_file` (Aspire's rules plus `host replication all all scram-sha-256`,
  which the agent's remote `backup-push` needs) and `PGCTLTIMEOUT=300`. The `timeout 60` bounds a push to an
  unreachable destination (a timed-out push fails and PostgreSQL retries the segment); `PGCTLTIMEOUT` lets the image's
  first-start shutdown outlast three such retries.
- **`backup-agent`** (`backup-agent/`, runs only with backup enabled): Debian image with WAL-G and rclone v1.75.2 (both
  checked by SHA-256), `postgresql-client` and a POSIX `sh` scheduler, `agent.sh`, as a non-root user, time zone
  `America/Sao_Paulo`. It does, in one loop (a 60 s pass):
  - **at start:** creates the bucket first, sets the login password of the `anamnys_backup_agent` role (retries every
    30 s until the migration created the role), takes a full backup if none exists;
  - **WAL keepalive:** when `pg_stat_archiver` shows no archive in the last 5 minutes (and archiving is not failing),
    it forces a WAL switch, so an idle database does not look stale. Cost: one small segment per idle 5 minutes;
  - **objects every 10 minutes:** `rclone sync` from the `notes` bucket to the crypt remote `current/`, with
    `--backup-dir versions/<UTC AAAA-MM-DDTHHMM>`, so a deletion or overwrite moves the old copy to a dated folder;
  - **full backup daily from 03:00 (America/Sao_Paulo):** `wal-g backup-push`; then the row-count snapshot (written
    first, so every backup that exists has its counts even if marking fails); then the first full of each month is
    marked permanent (`backup-mark`); then retention (section 4). A failed full is retried once, an hour later. The
    agent takes at most one full per day (a restart does not take a second one the same day);
  - **run log:** each run inserts a `BackupRuns` row (kind, start, end, outcome, bytes, short error code, backup name)
    as the role `anamnys_backup_agent`, which can only insert there and read the snapshot tables.

  Tool output stays in a private work directory; the container log carries only `<kind> <outcome> (<code>) <name>`.
- **Destination layout** (the bucket reveals nothing): `pg/` (WAL-G, libsodium-encrypted, brotli), `files/` (rclone
  crypt with **encrypted file and folder names**, so `current/` and `versions/<ts>/` exist only through the crypt
  remote `backupcrypt:`), `meta/counts/<backup-name>.json` (row counts, encrypted). The rclone crypt password is the
  same `backup-encryption-key`.
- **`BackupMonitor`** (server, `Backup/`, only when `Backup:Enabled`): every 15 minutes, section 11.
- **Data:** migration `design/migrations/2026-11-05-backup-runs.sql` (idempotent, also in `anamnys-db-script.sql`):
  `BackupRuns`, `BackupAlerts` (one open alert per check) and the role. **Apply it to any persistent environment before
  turning backup on**; the agent waits for the role.
- **Row-count snapshot:** after each full, a JSON with the row count of Providers, Patients, Notes, NoteSections,
  ClinicalDocuments, ExternalDocuments, IntegritySeals, IntegrityAnchors, AuditEntries, Dossiers and ShareLinks, and
  the Keycloak users per `anamnys-*` realm. Counts only.

---

## 3. Goals and measured results

| Measure | Goal | Drill 4 (2026-11-07, local) | Drill 2026-11-08 (local, replay + overlay) |
|---|---|---|---|
| RPO, database | 1 minute (WAL shipped at least every 60 s) | 0 s | 0 s |
| RPO, objects | 15 minutes (sync every 10 minutes) | 6m57s | 2m35s |
| RTO, everything on a new server | 2 hours | 7m38s (RestoreCheck 302 s of it) | 5m10s |

Reading the numbers:

- RPO (database) is 0 when the restored database holds the newest change that existed at or before the target. An idle
  period before the target is not data loss; the record shows both offsets.
- **Drill 4 did not prove point-in-time replay.** Its newest change before the target was older than the full backup
  used, and no object version folder was overlaid, so neither WAL replay of a real change nor the object overlay ran end
  to end. The drill now also counts the agent's own `BackupRuns` rows as changes and records "WAL replay of a change
  after the full backup" and the number of version folders overlaid, marking "point-in-time replay not exercised"
  when either is missing. Drill 5 (a change after the full, an object overwritten after the target) is pending.
- RPO (objects) is the target minus the start of the sync the restore used, so it is between 0 and about 10 minutes
  plus the sync duration; the 15-minute goal leaves margin.
- RTO starts when the throwaway environment is created and stops when RestoreCheck passes. The images and the
  RestoreCheck build are made before the clock starts. The drill ran on a development database (143 objects restored),
  so a production-sized one will take longer: the goal is 2 hours and the monthly drill measures the real figure.

---

## 4. Retention

- **Database:** point-in-time restore over the last 30 days (`wal-g delete retain FULL 30 --confirm` after every full)
  plus the first full of each month kept as permanent for 12 months (older permanent backups are unmarked and
  deleted).
- **Objects:** `current/` holds the latest state; `versions/<ts>/` folders older than 30 days are purged. The purge
  refuses to record success when version folder names do not decrypt (`versions_undecryptable`) or have an unexpected
  shape (`versions_unrecognized`).
- **`restore.sh` restores only inside the 30-day point-in-time window.** The monthly permanent fulls are kept for 12
  months, but `restore.sh` has no way to restore one of them on its own: it always recovers to a target time (or the
  latest), which needs the WAL after the full, and that WAL is gone after 30 days. A target older than 30 days fails
  ("a gap or a fetch failure"). Restoring a monthly full needs a manual procedure (`wal-g backup-fetch <name>` into a
  new volume and recovery with `recovery_target = 'immediate'`); this is a known gap (section 12).

---

## 5. Enabling it in Development

Off by default: with `Parameters:backup-enabled` unset the AppHost graph is exactly the old one (no agent, no extra
store, stock PostgreSQL), and the test suite is unaffected. To try it:

```sh
cd anamnys-aspire
dotnet user-secrets set "Parameters:backup-enabled" "true" --project anamnys-aspire.AppHost
dotnet user-secrets set "Parameters:backup-encryption-key" "$(openssl rand -base64 32)" --project anamnys-aspire.AppHost
dotnet user-secrets set "Parameters:backup-alert-email" "you@example.com" --project anamnys-aspire.AppHost
```

Then start the AppHost. It adds a second SeaweedFS, `backup-store` (volume `anamnys-backup-store-data`, bucket
`anamnys-backup`), as the destination, and generates `backup-store-access-key`, `backup-store-secret-key` and
`backup-agent-db-password` once into the user secrets. Keep the encryption key somewhere other than the secrets file too
(section 6): a throwaway dev key is fine, but the habit is the point.

**Remove `Parameters:backup-enabled` afterwards** (`dotnet user-secrets remove "Parameters:backup-enabled" --project
anamnys-aspire.AppHost`). The test fixtures pin `backup-enabled=false`, so a switch left on no longer changes the
tests, but every dev run of the AppHost keeps starting the backup containers until it is removed.

The first full backup runs at agent start. The dev database needs at least one provider for the drill (RestoreCheck
fails on a database with no providers).

---

## 6. Keys

- **`backup-encryption-key`** (32 random bytes, base64: `openssl rand -base64 32`) encrypts everything: WAL-G (libsodium)
  and rclone (crypt password). **It must be kept outside the server**, in the password manager of the account owner.
  **Losing it makes every backup unreadable, permanently.** No one, including us, can recover the data without it.
- The destination credentials (`backup-s3-access-key`, `backup-s3-secret-key`) are separate and replaceable at will.
- **Rotation (manual):**
  1. Generate the new key and store it in the password manager next to the old one (never overwrite the old).
  2. Set the new value in the secret store and redeploy: PostgreSQL, the agent and the server restart with it.
  3. **Get a full backup with the new key, as soon as possible.** WAL is written with the new key from the redeploy on,
     but the newest full is still the old-key one, and `restore.sh` uses one key for both: **between the redeploy and
     the first new-key full no moment can be restored point-in-time** (an old-key base backup cannot be replayed with
     new-key WAL). Moments before the redeploy still restore with the old key. The agent takes at most one full per day
     (it skips the start-up full when one already exists that day), so either:
     - wait for the next 03:00 full (up to about a day without point-in-time restore); or
     - force one now: `docker exec <backup-agent container> wal-g backup-push`, the same command the agent's full runs
       (its environment already has the new key and the database settings; not exercised yet). That full has no
       row-count snapshot and no `BackupRuns` row; the next scheduled full has both, and a drill needs one with a
       snapshot.

     Confirm the new full with `wal-g backup-list` (forced) or an `ok` `full` row in `BackupRuns` (scheduled).
  4. **Keep the old key until every backup made with it has expired** (30 days of point-in-time, 12 months for the
     monthly fulls). A restore of an older moment needs the key that wrote it: restore with the old key in the
     environment (`WALG_LIBSODIUM_KEY`, `BACKUP_ENCRYPTION_KEY`).
  5. Objects: files already in `files/` stay readable only with the key that wrote them, and the agent's crypt remote
     uses only the new key. After rotating, empty the destination `files/` (the old key still safe elsewhere) and let
     the next sync rebuild it with the new key, or keep the old key for as long as that data matters.
  6. Run a drill (section 9) with the new key.
- The key serves WAL-G and rclone at once, so a compromise exposes both stores. Rotate as above and treat the old
  backups as exposed.

---

## 7. Restoring: step by step

The scenario: it is Tuesday, someone deleted or corrupted data at 14:35, and you want the system **back to 14:32,
before the mistake**. Or the server is gone and you want the latest state. The restore builds a **new** PostgreSQL
(both databases) and a **new** object store; nothing existing is overwritten. The scripts are POSIX `sh`; run them in
Git Bash or any Linux shell with Docker.

1. **Decide the moment.** For "back to 14:32" write the time with the offset: `"2026-11-10 14:32:00-03"`. For a total
   loss use `--latest`.
2. **Prepare a Docker network** for the new server and a new object store on it (a SeaweedFS or any S3 reachable from
   that network), with an **empty** bucket for the objects (`restore.sh` refuses a bucket that already holds any
   object, so it can never revert a live store).

   ```sh
   docker network create anamnys-restore-net
   ```

3. **Export the environment** (from the password manager and the deployment secrets; the names are the agent's):

   ```sh
   # the backup bucket
   export WALG_S3_PREFIX="s3://<bucket>/pg"  AWS_ENDPOINT="<endpoint>"
   export AWS_ACCESS_KEY_ID="..."  AWS_SECRET_ACCESS_KEY="..."  WALG_LIBSODIUM_KEY="<backup-encryption-key>"
   export BACKUP_S3_ENDPOINT="<endpoint>"  BACKUP_S3_BUCKET="<bucket>"
   export BACKUP_S3_ACCESS_KEY="..."  BACKUP_S3_SECRET_KEY="..."  BACKUP_ENCRYPTION_KEY="<backup-encryption-key>"
   # the new server
   export RESTORE_NETWORK=anamnys-restore-net
   export RESTORE_S3_ENDPOINT="<new object store, reachable from the network>"  RESTORE_S3_BUCKET="<new, empty bucket>"
   export RESTORE_S3_ACCESS_KEY="..."  RESTORE_S3_SECRET_KEY="..."
   export RESTORE_PG_PUBLISH="127.0.0.1:5432:5432"
   export RESTORE_COUNTS_FILE="$PWD/counts.json"
   ```

   `RESTORE_S3_BUCKET` has no default and must be new or empty. Optional: `RESTORE_PG_CONTAINER` (default `anamnys-restored-postgres`),
   `RESTORE_PG_VOLUME` (default `<container>-data`; neither may exist), `RESTORE_TIMEOUT` (default 3600 s),
   `RESTORE_PG_IMAGE` and `RESTORE_AGENT_IMAGE` (default: built from `postgres/` and `backup-agent/`),
   `RESTORE_SUMMARY_FILE`. The endpoints must be reachable from containers on `RESTORE_NETWORK`.
4. **Run the restore:**

   ```sh
   sh scripts/restore.sh --target-time "2026-11-10 14:32:00-03"     # or: sh scripts/restore.sh --latest
   ```

   It converts the target to UTC; checks that the destination bucket is empty (before the database restore and again
   before the copy); picks the newest full backup that finished before the target; runs `wal-g
   backup-fetch` into the new volume; writes `recovery.signal`, `restore_command='wal-g wal-fetch %f %p'`,
   `recovery_target_time` and `recovery_target_action=promote` (`--latest`: no target); starts PostgreSQL with
   archiving **off** and waits for promotion; restores the objects with `rclone copy` from `current/` and, for a
   target, overlays each `versions/<ts>` folder whose UTC minute is at or after the target's minute, newest first;
   writes the row-count snapshot of the backup used to `RESTORE_COUNTS_FILE`. It prints counts only (no names, no
   clinical data). **Every `wal-g`, `rclone` and `docker` failure aborts it with exit 1**: a restore that silently lands
   elsewhere is worse than a failed one. On failure it names the container and volume to remove before trying again, and
   keeps a private work directory with the tool output.
5. **Verify with RestoreCheck** (section 8, absolute mode, with `--counts "$RESTORE_COUNTS_FILE"`) before starting the
   application.
6. **Start the application against the restored PostgreSQL and object store** (the deployment shape is not chosen yet;
   point the server's connection strings and `storage` settings at the new containers), and sign in as a provider.
7. **Set backup up again on the new server.** The restored PostgreSQL runs with `archive_mode=off` on purpose, so it
   never writes into the old bucket. Enable backup in the new deployment (a new bucket, or a clean one) and watch the
   first full backup. Until then the new server is **not protected**.

**The end-of-WAL fallback.** If the archived WAL holds no commit after the target (a quiet database, or "now minus
5 minutes" with the last segment not shipped yet), PostgreSQL stops with "recovery ended before configured recovery
target was reached". Every commit replayed is before the target, so the script retries, but only when the first run
reached the newest archived segment, and it bounds the retry to exactly where the first run ended
(`recovery_target_lsn`, inclusive), so WAL archived in the meantime can never take it past the target. The summary
reports the mode `end-of-wal-before-target`.

**"Run again" cases.** The restore fails, on purpose, with "recovery stopped at WAL segment A but the archive holds
segments up to C" when the recovery ended before the newest archived segment: either a real gap or fetch failure
(investigate the bucket), or WAL archived while the restore ran (typical of `--latest` against a still-running origin;
just run it again). It also fails with "object backup folders do not decrypt (wrong BACKUP_ENCRYPTION_KEY?)" and with
"no full backup finished before the target".

**The destination must be reachable at the first start of a fresh volume.** The postgres image initializes a fresh
volume with a temporary server that inherits the archive settings; if the bucket is unreachable at that moment, the
shutdown of that temporary server waits on the push. `timeout 60` and `PGCTLTIMEOUT=300` bound the wait, but the
unreachable-destination first start was **not measured** (section 12). Supply the endpoint, bucket and credentials and
check them before the first deploy. A restart of an initialized volume is not affected.

**Limits of the object restore.** Object state is exact to the 10-minute sync grid. An object created after the sync
used is left in place (nothing in the database references it). A deletion at the origin is recoverable from the version
folders for 30 days.

---

## 8. RestoreCheck

Console project `anamnys-aspire.RestoreCheck` (references the server project). It reads the restored PostgreSQL and
object store and prints JSON `{ "passed", "checks": [{ "name", "passed", "detail" }], "durationSeconds" }`; exit code 0
or 1. Output has counts and sequence numbers only, never clinical text or object names. A failure while setting up
becomes one failing `setup` check.

Options (each also read from the environment as `RESTORECHECK_<NAME>`, dashes to underscores): `--connection`
(anamnysdb), `--keycloak-connection`, `--s3-endpoint`, `--s3-bucket`, `--s3-access-key`, `--s3-secret-key`,
`--counts <snapshot.json>`, `--sample` (default 200), `--tsa-root <pem>` (the F-45 root, so anchors verify),
`--source-s3-*` (the live store, for the hash comparison) and `--baseline-connection`.

| Check | What it demands |
|---|---|
| `integrity` | `IntegrityVerifier.VerifyChainAsync` for every provider (chapter 38). Fails with zero providers, or with zero seals when the snapshot has some. |
| `row_counts` | every snapshot table has at least the snapshot count (more is expected: rows between the full backup and the target). With `--counts` given but unreadable, empty or without a value it is a setup failure. Without `--counts` it reports "skipped"; **a real restore always passes it**. |
| `files` | a random sample of signed notes and documents: the object exists and its SHA-256 equals `PdfSha256`; of clean external documents: it exists and its size matches (and, with `--source-s3-*`, its SHA-256 matches the live store). Fails when candidates exist but none was checked. |
| `keycloak` | the realms `anamnys-%` exist; with a snapshot, every snapshot realm exists with at least its snapshot user count (per realm, so a loss in one realm cannot hide behind another). |

**Two modes.**

- **Absolute (a real restore, no baseline):** every chain must be sound and every sampled object present and correct.
  Anything wrong is a failure. Do not start the application on a restore that fails here without understanding why.
- **Baseline (drills):** with `--baseline-connection` (a read-only connection to the live database; it requires
  `--source-s3-*`), a chain broken in the restore is pre-existing only when the live chain has the same problem (same
  kind at the same first-problem sequence); any other break counts. An object missing or different in the restore is
  pre-existing only when the live source is readable and holds it missing or wrong too. A baseline database that cannot
  be read fails `integrity`; a source store that cannot be read fails `files`; a single failed source read counts as
  a new problem, never a pre-existing one. The detail reads "N new; M pre-existing (...)" and
  only the *new* ones fail. This exists because the development database carries problems that are not restore defects
  (the tests write into it; section 12) and must neither hide nor fake a restore result.

---

## 9. The drill

`scripts/restore-drill.sh` restores into a throwaway environment, verifies, measures and writes the record. The live
system is only read (a read-only transaction, and the live object store for the hash comparison).

**How to run** (Git Bash from `anamnys-aspire/`, with the AppHost running with `backup-enabled`, the agent having taken
its first full at least 5 minutes before and at least one object sync before the target minute):

```sh
S=$(dotnet user-secrets list --project anamnys-aspire.AppHost)
get() { printf '%s\n' "$S" | sed -n "s/^Parameters:$1 = //p"; }
export BACKUP_ENCRYPTION_KEY="$(get backup-encryption-key)"
export BACKUP_S3_ACCESS_KEY="$(get backup-store-access-key)"
export BACKUP_S3_SECRET_KEY="$(get backup-store-secret-key)"
export SOURCE_S3_ACCESS_KEY="$(get storage-access-key)"
export SOURCE_S3_SECRET_KEY="$(get storage-secret-key)"
export PGPASSWORD="$(get postgres-password)"
unset S
sh scripts/restore-drill.sh            # optional: --origin <name>, --target-time "..."
```

Optional overrides: `BACKUP_S3_ENDPOINT`, `SOURCE_S3_ENDPOINT`, `LIVE_PG_PORT` (otherwise read from the running
containers), `BACKUP_S3_BUCKET` (default `anamnys-backup`), `SOURCE_S3_BUCKET` (default `notes`), `PGUSER`,
`ANAMNYS_DB`, `KEYCLOAK_DB`, `DRILL_CONTAINER_HOST`, `DRILL_PG_IMAGE`, `DRILL_AGENT_IMAGE`, `DRILL_RECORD_FILE`,
`DRILL_TIMEOUT` (default 7200). The export block uses secrets, so run it in your own shell; nothing is printed or
written to the record.

**Keep the machine awake** for at least 20 minutes before and during the run (disable sleep). A sleeping host or a
stalled Docker VM stops the agent's loops; the drill then correctly reports a large RPO (objects), and in production the
monitor's 45-minute alert would fire.

What it does: reads the live database (target = the database's now minus 5 minutes, in `America/Sao_Paulo`, and the
newest change at or before it); creates a throwaway network, SeaweedFS and PostgreSQL; runs `restore.sh`; runs
RestoreCheck in baseline mode with the snapshot of the backup used (`--counts` is always passed; a missing snapshot
fails the drill); measures RPO and RTO; writes the record; tears everything down (a trap removes containers, volume and
network and warns about leftovers). A setup failure (image build, network, throwaway store) still writes a short
`reprovado` record.

**Record:** `design/drills/AAAA-MM-DD-restore-drill.md` (`-2`, `-3` if the name is taken): date, origin, target, backup
used, recovery mode, objects restored, RPO and RTO against the goals, one row per check, a "Reasons" list and the
verdict. **`aprovado` only when every check passed, RPO and RTO meet the goals and both exit codes are 0.** No secrets
and no clinical data.

**Cadence:** monthly. A failed drill opens a gap in the gaps file until a passing one. The "cofre" rule (section 1)
counts 90 days from the newest `aprovado`. History on 2026-11-07: record 1 (setup failure, Docker build path under Git
Bash), record 2 (restore fine, `reprovado`: pre-existing dev-data problems, before baseline mode), record 3 (setup
failure, the server build was locked by a running Aspire), record 4 **aprovado** (RPO database 0 s, objects 6m57s, RTO
7m38s; 0 new problems; pre-existing dev-data problems listed separately). Records 1 to 3 are kept as history. On 2026-11-08 `2026-11-08-restore-drill.md` **aprovado** with an explicit target: a change made after the full backup was replayed and one object overwritten after the target came back from its version folder (RPO database 0 s, objects 2m35s, RTO 5m10s).

---

## 10. What a restore must never do

- Pass with nothing to verify. RestoreCheck fails on zero providers, zero seals where the snapshot has some, candidate
  files but none checked, a missing or unreadable snapshot in the drill, and (baseline mode) an unreadable baseline
  database or source store.
- Write into an object store that already holds data: `restore.sh` refuses a destination bucket with any object.
- Land silently at another moment: gaps, a wrong key, an unreadable folder or a short object count are failures.
- Write into the backup bucket from the restored server (archiving is off).
- Print secrets, names of objects or clinical text; secrets reach containers only as `-e NAME`.

---

## 11. What to do on each alert

`BackupMonitor` e-mails `Backup:AlertEmail` (Resend) with subject "Anamnys: problema no backup", the check name, the age
in minutes and the UTC time, nothing clinical. One e-mail per check per problem episode, stored in `BackupAlerts`,
re-armed after the check passes. A send failure is retried at the next run. **Start-up grace:** for 30 minutes after
the server starts, a check with no record at all ("sem registro": no full, no sync, nothing archived yet) is not
alerted, so a fresh deployment does not e-mail before the agent's first runs; a check that has a record is judged
normally from the start.

| Check | Fires when | What to do |
|---|---|---|
| `wal` | `pg_stat_archiver.last_archived_time` is older than 10 minutes or null, or `last_failed_time` is newer than `last_archived_time` | Check the destination (reachable, credentials, bucket, space) and the PostgreSQL log for `archive command failed`. The archiver retries the same segment by itself; when one succeeds the alert closes at the next run. The longer it lasts, the larger the RPO and the more WAL piles up on the server disk. |
| `full` | no `ok` full in the last 26 hours | Read the `full` rows of `BackupRuns` (`ErrorCode`): `walg_backup_push` (destination or replication access), `counts_snapshot`. Fix the cause and restart the agent (it takes a full at start if none exists); the 03:00 slot also retries. |
| `files` | no `ok` object sync in the last 45 minutes | Read the `files` rows (`rclone_exit_N`); check source and destination reachability and the host clock. A host or Docker stall also causes it. |
| `failed_run` | any `failed` run since the last check | Read the row: kind and error code say which part. For `retention`, `versions_undecryptable` or `versions_unrecognized` mean version folders do not decrypt: a wrong key or an unexpected folder. |

After any fix, confirm with a new `ok` run in `BackupRuns`. While an alert is open, do not claim custody; if the key is
in doubt, see section 6.

---

## 12. Limits and known gaps

- The "cofre" rule (section 1) is not met yet: there is no production, and the passing drill is a local one.
- The drill ran on a development database (143 objects restored); production size will change the RTO.
- A missing bucket on a real S3 (a fast failure) was not demonstrated. SeaweedFS creates the bucket on the first PUT;
  the agent creates the bucket first. The expected behavior rests on PostgreSQL's documented archiver (it keeps
  retrying the segment).
- With the destination unreachable at the very first start of a fresh volume, only the documentation and the bounds
  (`timeout 60`, `PGCTLTIMEOUT=300`) exist; it was not measured because the Docker clock jumped during the attempt.
- `restore.sh` cannot restore a monthly permanent full older than the 30-day window (section 4): it has only
  `--target-time` and `--latest`. A manual procedure is needed until a `--backup <name>` option exists.
- The object restore's check "restored objects >= objects in `current/`" holds because the destination bucket must be
  empty (`restore.sh` refuses one with any object).
- After a key rotation, no moment between the redeploy and the first new-key full can be restored point-in-time
  (section 6).
- Drill 4 did not exercise WAL replay of a change after the full nor the object overlay (section 3); drill 5 pending.
- A failure of `wal-g backup-list` at agent start skips the immediate full only before 03:00 (the 03:00 slot covers
  it); after 03:00 the first pass of the loop starts a full anyway.
- The development database carries pre-existing problems written by the tests (6 `content_changed` chains, missing
  objects); baseline mode reports them separately. They are not restore defects.
- `--latest` against a still-archiving origin may need "run again" (section 7).
- Advisories remain in the test and AppHost projects (`Microsoft.AspNetCore.SignalR.Common` NU1903, `MessagePack`
  NU1902); `System.Security.Cryptography.Pkcs` and `Cryptography.Xml` were moved to 10.0.14.
- The restored server has no backup until it is set up again (section 7, step 7).
- Production hosting is not chosen; a real deployment supplies the destination and keys (chapter 17, chapter 20
  section 2.1).

---

## 13. Tests

- `Backup/BackupMonitorTests`: disabled does nothing; fresh passes; inclusive boundaries; WAL stale, failing, never;
  full older than 26 h; files older than 45 min; failed run; one e-mail per episode and re-arm; send failure retried;
  no clinical data in the e-mail; start-up grace (no record within 30 minutes: no alert; at 30 minutes: alerts; a
  stale record inside the grace still alerts).
- `Data/BackupRunsMigrationTests`: idempotent, checks, the partial index, the role (exists, not superuser, the
  migration leaves its `rolcanlogin` unchanged; grants through `SET ROLE`), `pg_stat_archiver` readable by a non-superuser. The migration is appended to the other chains.
- `Backup/AppHostBackupModelTests`: with backup unset the graph is exactly the old one (stock PostgreSQL, no
  `PGCTLTIMEOUT`); enabled: agent, store, archive args, WAL-G environment, `Backup__*` on the server; publish: the
  backup parameters, no store. The disabled case and `SharedAppHostFixture` pin `Parameters:backup-enabled=false`, so a
  switch left on in the user secrets after a drill cannot change the test graph.
- `Backup/RestoreCheckTests`: a healthy database passes; tampered seal, count below the snapshot, wrong PDF hash,
  missing object, zero providers, zero seals against the snapshot, candidates but none checked, bad `--counts`; one
  Keycloak realm below its snapshot while the sum holds; baseline mode (same problem live is pre-existing; a chain
  broken only in the restore, or broken live in another way, fails; unreadable source store or baseline database
  fails); option parsing and environment fallback.
- Scripts: `shellcheck` v0.11.0 (Docker `koalaman/shellcheck`) clean on `agent.sh`, `restore.sh` and
  `restore-drill.sh`; `.gitattributes` keeps them (and the backup Dockerfiles and config) LF.
- The drill itself is the end-to-end test (section 9). Running the suite needs the AppHost stopped.

---

## 14. Where things are

| Piece | Path |
|---|---|
| Spec | `design/specs/2026-11-05-backup-restore-design.md` |
| Migration | `design/migrations/2026-11-05-backup-runs.sql` |
| PostgreSQL image | `postgres/Dockerfile`, `postgres/pg_hba.conf` |
| Agent | `backup-agent/Dockerfile`, `agent.sh`, `rclone.conf.template` |
| AppHost wiring | `anamnys-aspire.AppHost/AppHost.cs` (F-46 block) |
| Monitor | `anamnys-aspire.Server/Backup/` (`BackupMonitor`, `BackupMonitorJob`, `BackupOptions`) |
| Restore and drill | `scripts/restore.sh`, `scripts/restore-drill.sh` |
| RestoreCheck | `anamnys-aspire.RestoreCheck/` |
| Drill records | `design/drills/` |
| Tests | `anamnys-aspire.Tests/Backup/`, `anamnys-aspire.Tests/Data/BackupRunsMigrationTests.cs` |
