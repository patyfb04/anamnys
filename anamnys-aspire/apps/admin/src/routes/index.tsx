import { useEffect } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import { hasAuthError } from "@anamnys/shared/lib/authError";
import AuthErrorNotice from "@anamnys/shared/ui/AuthErrorNotice";
import AdminTopBar from "@/components/AdminTopBar";
import AdminLanding from "@/components/AdminLanding";
import PendingApproval from "@/components/PendingApproval";

export const Route = createFileRoute("/")({
  component: AdminHome,
});

// Keep in sync with StaffRoles.All in anamnys-aspire.Server/Auth/FirstLoginProvisioner.cs
const STAFF_ROLES = ["owner", "support", "ops"];

function AdminHome() {
  const { user, isLoading, loadUser } = useAuthStore();
  const authError = hasAuthError();

  useEffect(() => {
    void loadUser("owner");
  }, [loadUser]);

  // No auto-redirect to login: anonymous visitors choose between Entrar and Criar conta.
  if (authError) return <AuthErrorNotice />;
  if (isLoading) return null;

  return (
    <div className="min-h-screen flex flex-col bg-surfaceContainerLow">
      <AdminTopBar user={user} />
      <main className="flex-1 flex items-center justify-center p-4">
        {!user ? (
          <AdminLanding />
        ) : !user.roles.some((r) => STAFF_ROLES.includes(r)) ? (
          <PendingApproval email={user.email} />
        ) : (
          <div className="text-center">
            <h1 className="text-headline-lg text-onSurface mb-2">Anamnys Internal</h1>
            <p className="text-body-lg text-onSurfaceVariant">
              Signed in as {user.email} ({user.roles.join(", ")}).
            </p>
          </div>
        )}
      </main>
    </div>
  );
}
