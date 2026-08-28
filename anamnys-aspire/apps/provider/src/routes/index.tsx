import { createFileRoute, redirect } from "@tanstack/react-router";

// The provider app has no public landing page — the marketing site owns that.
// Entering at "/" goes straight to the dashboard; the _app layout redirects to
// /login when there is no session.
export const Route = createFileRoute("/")({
  beforeLoad: () => {
    throw redirect({ to: "/patients" });
  },
});
