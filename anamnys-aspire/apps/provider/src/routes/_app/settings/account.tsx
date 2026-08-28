import { createFileRoute } from "@tanstack/react-router";

export const Route = createFileRoute("/_app/settings/account")({
  component: AccountSettingsPage,
});

function AccountSettingsPage() {
  return <div className="p-8 text-center text-onSurfaceVariant text-body-lg">Account settings — coming soon.</div>;
}
