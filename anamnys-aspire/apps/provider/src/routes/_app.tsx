import { useEffect } from "react";
import { createFileRoute, Outlet } from "@tanstack/react-router";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import { hasAuthError } from "@anamnys/shared/lib/authError";
import AuthErrorNotice from "@anamnys/shared/ui/AuthErrorNotice";
import TopBar from "@anamnys/shared/ui/TopBar";
import AppTabBar from "@anamnys/shared/ui/AppTabBar";
import Sidenav from "@anamnys/shared/ui/Sidenav";

export const Route = createFileRoute("/_app")({
  component: AppLayout,
});

function AppLayout() {
  const { user, isLoading, login } = useAuthStore();
  // Read once, not per render: the BFF puts it on the landing URL and nothing
  // in this app removes it.
  const authError = hasAuthError();

  useEffect(() => {
    // Not navigate({ to: "/login" }) — there is no login route in this app
    // any more. The BFF redirects to Keycloak, which renders the theme.
    // Guarded on authError: challenging again after a provisioning refusal
    // succeeds at Keycloak and fails here again, forever.
    if (!authError && !isLoading && !user) login("provider", window.location.pathname);
  }, [authError, isLoading, user, login]);

  if (authError) return <AuthErrorNotice />;

  if (isLoading || !user) return null;

  return (
    <div className="min-h-screen flex flex-col bg-surfaceContainerLow">
      <Sidenav />
      <div className="flex flex-col flex-1 md:ml-72">
        <TopBar />
        <main className="flex-1">
          <Outlet />
        </main>
        <AppTabBar />
      </div>
    </div>
  );
}
