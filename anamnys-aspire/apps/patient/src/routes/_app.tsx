import { useEffect } from "react";
import { createFileRoute, Outlet } from "@tanstack/react-router";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import { hasAuthError } from "@anamnys/shared/lib/authError";
import AuthErrorNotice from "@anamnys/shared/ui/AuthErrorNotice";
import PatientTopBar from "@/components/PatientTopBar";

export const Route = createFileRoute("/_app")({
  component: AppLayout,
});

// Same auth gate as apps/provider's _app layout: no session means a full-page
// navigation to the BFF's login, which hands off to Keycloak. Guarded on authError
// so a provisioning refusal shows a notice instead of re-challenging forever.
function AppLayout() {
  const { user, isLoading, login } = useAuthStore();
  const authError = hasAuthError();

  useEffect(() => {
    if (!authError && !isLoading && !user) login("patient", window.location.pathname);
  }, [authError, isLoading, user, login]);

  if (authError) return <AuthErrorNotice />;

  if (isLoading || !user) return null;

  return (
    <div className="min-h-screen flex flex-col bg-surfaceContainerLow">
      <PatientTopBar />
      <main className="flex-1">
        <Outlet />
      </main>
    </div>
  );
}
