import { createFileRoute } from "@tanstack/react-router";

export const Route = createFileRoute("/_app/settings/account")({
  component: AccountSettingsPage,
});

// Keycloak owns passwords and TOTP enrolment now, so account management is a
// link out to its own account console rather than a page rebuilt here.
//
// TODO: this URL is hardcoded to the local dev Keycloak instance and is wrong
// in any non-dev deployment (Keycloak's own origin isn't necessarily known to,
// or resolvable by, the browser there). There's no plumbing yet to hand the
// SPA a Keycloak base URL at build or runtime — once there is, source this
// from that config (e.g. an env var the server injects, along the lines of
// VITE_KEYCLOAK_BASE_URL) instead of the literal below.
const KEYCLOAK_ACCOUNT_CONSOLE_URL = "http://localhost:8080/realms/anamnys-providers/account/";

function AccountSettingsPage() {
  return (
    <div className="p-8 text-center text-onSurfaceVariant text-body-lg">
      <p className="mb-4">Manage your password and two-factor authentication in Keycloak.</p>
      <a
        href={KEYCLOAK_ACCOUNT_CONSOLE_URL}
        target="_blank"
        rel="noopener noreferrer"
        className="text-primary underline"
      >
        Open account console
      </a>
    </div>
  );
}
