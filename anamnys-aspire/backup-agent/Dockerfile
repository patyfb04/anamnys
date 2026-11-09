# The backup agent (design/specs/2026-11-05-backup-restore-design.md §2.3): full backups with WAL-G, object copies
# with rclone, retention, and one BackupRuns row per run. AppHost.cs adds it only when backup is enabled.
#
# WAL-G and rclone are pinned to exact releases and checked by SHA-256 (the values published next to each asset,
# verified when pinned). To upgrade: change the version and its SHA-256 together.

FROM debian:trixie-20261020-slim AS fetch
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl unzip \
    && rm -rf /var/lib/apt/lists/*
ARG WALG_VERSION=v3.0.9
ARG WALG_ASSET=wal-g-pg-22.04-amd64
ARG WALG_SHA256=4f03ee4679db7f660bfd1b2b8291ac5df247cd2ec48074b60c112f4897299f24
ARG RCLONE_VERSION=v1.75.2
ARG RCLONE_SHA256=349ac8fba6ff65d6247043f1750cdcb518ec5d500ef91463a10d37c0ccdf3702
RUN set -eu; \
    curl -fsSL -o /tmp/wal-g.tar.gz "https://github.com/wal-g/wal-g/releases/download/${WALG_VERSION}/${WALG_ASSET}.tar.gz"; \
    echo "${WALG_SHA256}  /tmp/wal-g.tar.gz" | sha256sum -c -; \
    tar -xzf /tmp/wal-g.tar.gz -C /tmp; \
    install -m 0755 "/tmp/${WALG_ASSET}" /usr/local/bin/wal-g; \
    curl -fsSL -o /tmp/rclone.zip "https://github.com/rclone/rclone/releases/download/${RCLONE_VERSION}/rclone-${RCLONE_VERSION}-linux-amd64.zip"; \
    echo "${RCLONE_SHA256}  /tmp/rclone.zip" | sha256sum -c -; \
    unzip -q /tmp/rclone.zip -d /tmp; \
    install -m 0755 "/tmp/rclone-${RCLONE_VERSION}-linux-amd64/rclone" /usr/local/bin/rclone

FROM debian:trixie-20261020-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates jq postgresql-client tzdata \
    && rm -rf /var/lib/apt/lists/*
COPY --from=fetch /usr/local/bin/wal-g /usr/local/bin/wal-g
COPY --from=fetch /usr/local/bin/rclone /usr/local/bin/rclone
COPY rclone.conf.template /etc/backup-agent/rclone.conf.template
COPY agent.sh /usr/local/bin/backup-agent
RUN chmod 0755 /usr/local/bin/backup-agent \
    && useradd --system --uid 10001 --home-dir /var/lib/backup-agent --create-home backup-agent \
    && wal-g --version && rclone version
# The full backup runs at 03:00 in this zone.
ENV TZ=America/Sao_Paulo
USER backup-agent
WORKDIR /var/lib/backup-agent
ENTRYPOINT ["/usr/local/bin/backup-agent"]
