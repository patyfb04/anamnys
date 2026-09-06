import { useEffect } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";

export const Route = createFileRoute("/")({
  component: AdminHome,
});

function AdminHome() {
  const { user, isLoading, login, loadUser } = useAuthStore();

  useEffect(() => {
    void loadUser();
  }, [loadUser]);

  useEffect(() => {
    if (!isLoading && !user) login("owner", window.location.pathname);
  }, [isLoading, user, login]);

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
