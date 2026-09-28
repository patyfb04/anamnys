import { useTranslation } from "react-i18next";
import Card from "@anamnys/shared/ui/Card";
import Button from "@anamnys/shared/ui/Button";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";

// Terminal until someone grants a role in the Keycloak console. Sign-out is the only
// action: the role only reaches the session through a fresh login. Renders inside
// routes/index.tsx's centered <main>; no min-h-screen of its own. The top bar also
// has a Sair button, but this one stays as the primary in-card action.
export default function PendingApproval({ email }: { email: string }) {
  const { t } = useTranslation();
  const logout = useAuthStore((s) => s.logout);

  return (
    <Card className="w-full max-w-md text-center">
      <h1 className="text-headline-md text-onSurface mb-2">{t("admin.pending.title")}</h1>
      <p className="text-body-md text-onSurfaceVariant mb-1">{email}</p>
      <p className="text-body-lg text-onSurfaceVariant mb-8">{t("admin.pending.body")}</p>
      <Button title={t("admin.pending.signOut")} variant="outline" onClick={() => logout("owner")} />
    </Card>
  );
}
