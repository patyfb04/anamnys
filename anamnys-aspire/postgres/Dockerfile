# PostgreSQL with WAL-G for continuous archiving (design/specs/2026-11-05-backup-restore-design.md §2.1).
# AppHost.cs builds this image only when backup is enabled; with backup off it keeps Aspire's own postgres:18.3 image
# (the tag of Aspire.Hosting.PostgreSQL 13.5.3; the base below must match it, AppHostBackupModelTests checks the tag).
# Even when built, the image behaves exactly like postgres:18.3 unless the AppHost passes the archive settings and the
# WAL-G environment: nothing here changes the entrypoint, the configuration or the data directory.
#
# WAL-G is pinned to an exact release and checked by SHA-256 (the value published next to the asset, verified when
# pinned). To upgrade: change WALG_VERSION and WALG_SHA256 together.

FROM debian:trixie-20261020-slim AS fetch
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*
ARG WALG_VERSION=v3.0.9
ARG WALG_ASSET=wal-g-pg-22.04-amd64
ARG WALG_SHA256=4f03ee4679db7f660bfd1b2b8291ac5df247cd2ec48074b60c112f4897299f24
RUN set -eu; \
    curl -fsSL -o /tmp/wal-g.tar.gz "https://github.com/wal-g/wal-g/releases/download/${WALG_VERSION}/${WALG_ASSET}.tar.gz"; \
    echo "${WALG_SHA256}  /tmp/wal-g.tar.gz" | sha256sum -c -; \
    tar -xzf /tmp/wal-g.tar.gz -C /tmp; \
    install -m 0755 "/tmp/${WALG_ASSET}" /usr/local/bin/wal-g

FROM postgres:18.3
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=fetch /usr/local/bin/wal-g /usr/local/bin/wal-g
# Used only when the AppHost passes -c hba_file=/etc/postgresql/anamnys/pg_hba.conf (backup enabled): the image's own
# rules (password auth for every host, as Aspire initializes the cluster) plus replication connections, which the
# backup agent's remote `wal-g backup-push` (BASE_BACKUP protocol) needs.
COPY pg_hba.conf /etc/postgresql/anamnys/pg_hba.conf
RUN wal-g --version
