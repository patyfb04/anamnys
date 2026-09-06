import { useEffect } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import { hasAuthError } from "@anamnys/shared/lib/authError";
import AuthErrorNotice from "@anamnys/shared/ui/AuthErrorNotice";

export const Route = createFileRoute("/")({
  component: AdminHome,
});

function AdminHome() {
  const { user, isLoading, login, loadUser } = useAuthStore();
  const authError = hasAuthError();

  useEffect(() => {
    void loadUser();
  }, [loadUser]);

  useEffect(() => {
    // Guarded on authError: a provisioning refusal (an owners-realm token with
    // none of the staff roles, a disabled account) would otherwise re-challenge
    // immediately, succeed at Keycloak, and fail here again, forever.
    if (!authError && !isLoading && !user) login("owner", window.location.pathname);
  }, [authError, isLoading, user, login]);

  if (authError) return <AuthErrorNotice />;

  if (isLoading || !user) return null;

  return (
    <div className="min-h-screen bg-surfaceContainerLow p-8">
      <h1 className="text-headline-lg text-onSurface mb-2">Anamnys Internal</h1>
      <p className="text-body-lg text-onSurfaceVariant">
        Signed in as {user.email} ({user.roles.join(", ") || "no roles"}).
      </p>
    </div>
  );
}
