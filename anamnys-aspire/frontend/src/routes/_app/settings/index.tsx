import { createFileRoute } from "@tanstack/react-router";

export const Route = createFileRoute("/_app/settings/")({
  component: SettingsPage,
});

function SettingsPage() {
  return <div className="p-8 text-center text-onSurfaceVariant text-body-lg">Settings — coming soon.</div>;
}
