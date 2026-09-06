import { createFileRoute } from "@tanstack/react-router";

export const Route = createFileRoute("/_app/settings/account")({
  component: AccountSettingsPage,
});

// Keycloak owns passwords and TOTP enrolment now, so account management is a
// link out to its own account console rather than a page rebuilt here.
function AccountSettingsPage() {
  return (
    <div className="p-8 text-center text-onSurfaceVariant text-body-lg">
      <p className="mb-4">Manage your password and two-factor authentication in Keycloak.</p>
      <a
        href="http://localhost:8080/realms/anamnys-providers/account/"
        target="_blank"
        rel="noopener noreferrer"
        className="text-primary underline"
      >
        Open account console
      </a>
    </div>
  );
}
