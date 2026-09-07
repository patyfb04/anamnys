# Whether dev-only realm content (the seeded dev.provider login and the
# localhost:* redirect/logout origins used by each SPA's Vite proxy) is
# included in the imported realms. Must default to false: this Dockerfile is
# the one build path Aspire uses for both `aspire start` (dev) and
# `aspire publish`/`deploy` (everywhere else), so anything not explicitly
# gated here ships to every environment identically. AppHost.cs passes
# --build-arg INCLUDE_DEV_SEED=true only when the app is running in
# Development.
ARG INCLUDE_DEV_SEED=false

# Filtering happens in its own stage, before Keycloak's importer ever sees the
# files, rather than as a runtime check: a runtime check would mean the
# credential and origins are live in the image (and briefly in a running
# Keycloak) even if something later refuses to use them. jq isn't present on
# the UBI-based Keycloak image, hence the separate Alpine stage.
FROM alpine:3 AS realms
RUN apk add --no-cache jq
COPY realms/ /in/
ARG INCLUDE_DEV_SEED
COPY strip-dev-seed.jq /strip-dev-seed.jq
# set -e (default in `sh -c`, but not implied inside a for-loop without it)
# so a jq failure on any one file aborts the build instead of only affecting
# the loop's last-iteration exit status — a truncated/empty realm file must
# never make it into the image silently.
RUN set -e; mkdir -p /out && for f in /in/*.json; do \
      if [ "$INCLUDE_DEV_SEED" = "true" ]; then \
        cp "$f" "/out/$(basename "$f")"; \
      else \
        jq -f /strip-dev-seed.jq "$f" > "/out/$(basename "$f")"; \
      fi; \
    done

FROM quay.io/keycloak/keycloak:26.6

COPY --from=realms /out/ /opt/keycloak/data/import/
COPY ./theme/keycloak-theme-anamnys.jar /opt/keycloak/providers/
