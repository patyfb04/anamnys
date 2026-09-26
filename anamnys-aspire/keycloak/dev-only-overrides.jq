# Applied only when INCLUDE_DEV_SEED=true (a local `aspire run`/`aspire start`), the
# opposite gate from strip-dev-seed.jq. Nothing here may ever reach a non-dev image —
# see Dockerfile's INCLUDE_DEV_SEED branch.
#
# verifyEmail: true (the realm JSON's own committed value, untouched here) needs
# somewhere to actually send mail. No environment has a real SMTP relay wired up yet —
# production still needs one, tracked separately. The ${DEV_SMTP_*} placeholders are
# Keycloak's own import-time ${VAR} substitution (the same mechanism
# ${ANAMNYS_APP_ORIGIN} and the client secrets elsewhere in this realm already rely
# on) — left untouched here, resolved from whatever AppHost.cs put in the container's
# environment. By default that's Mailpit (a throwaway SMTP catcher + web UI; "mailpit"
# resolves the same way "postgres" and "keycloak" do, as this container's own network
# alias for that Aspire resource) — see AppHost.cs's useResend comment for the
# per-developer, opt-in alternative of pointing this at a real Resend account instead.
.smtpServer = {
  host: "${DEV_SMTP_HOST}",
  port: "${DEV_SMTP_PORT}",
  from: "${DEV_SMTP_FROM}",
  fromDisplayName: "Anamnys (dev)",
  ssl: "false",
  starttls: "${DEV_SMTP_STARTTLS}",
  auth: "${DEV_SMTP_AUTH}",
  user: "${DEV_SMTP_USER}",
  password: "${DEV_SMTP_PASSWORD}"
}
# The login theme's header links back to the marketing site via client.baseUrl. The
# committed value is ${ANAMNYS_APP_ORIGIN}/, which in dev is the server's own endpoint —
# but apps/web runs on its own pinned Vite port (5275) during a dev run, so point there.
| .clients |= map(
    if has("baseUrl") then .baseUrl = "http://localhost:5275/" else . end
  )
