import { useEffect } from "react";
import { createFileRoute, Outlet } from "@tanstack/react-router";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import TopBar from "@anamnys/shared/ui/TopBar";
import AppTabBar from "@anamnys/shared/ui/AppTabBar";
import Sidenav from "@anamnys/shared/ui/Sidenav";

export const Route = createFileRoute("/_app")({
  component: AppLayout,
});

function AppLayout() {
  const { user, isLoading, login } = useAuthStore();

  useEffect(() => {
    // Not navigate({ to: "/login" }) — there is no login route in this app
    // any more. The BFF redirects to Keycloak, which renders the theme.
    if (!isLoading && !user) login("provider", window.location.pathname);
  }, [isLoading, user, login]);

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
