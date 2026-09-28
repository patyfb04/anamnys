import { LogOut } from "lucide-react";
import { useTranslation } from "react-i18next";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import type { AuthUser } from "@anamnys/shared/lib/types";
import logo from "@anamnys/shared/assets/logo1.png";

// Shown on every admin screen (anonymous, pending, active) — this app has no
// auto-redirect to login, so an anonymous visitor needs Entrar/Criar conta here,
// and a signed-in one needs Sair. Not AccountMenu: it links to /account/profile
// and /account/security, which this app does not have.
export default function AdminTopBar({ user }: { user: AuthUser | null }) {
  const { t } = useTranslation();
  const logout = useAuthStore((s) => s.logout);

  return (
    <header className="sticky top-0 z-30 bg-surface border-b border-surfaceVariant px-4 md:px-6">
      <div className="h-16 max-w-5xl mx-auto flex items-center justify-between gap-4">
        <div className="flex items-center gap-3 shrink-0">
          <img src={logo} alt="Anamnys" width={140} height={30} className="w-[140px] h-[30px] object-contain" />
          <span className="text-label-md text-onSurfaceVariant">{t("admin.topBar.internal")}</span>
        </div>

        {user ? (
          <div className="flex items-center gap-3 shrink-0">
            <span className="hidden md:inline text-body-md text-onSurfaceVariant truncate">{user.email}</span>
            <button
              onClick={() => logout("owner")}
              className="flex items-center gap-1.5 text-label-lg text-primary hover:opacity-80 transition-opacity px-2"
            >
              <LogOut size={18} />
              {t("admin.pending.signOut")}
            </button>
          </div>
        ) : (
          <div className="flex items-center gap-2 shrink-0">
            <a
              href="/auth/owner/login?returnUrl=/admin/"
              className="text-label-lg text-primary hover:opacity-80 transition-opacity px-2"
            >
              {t("admin.landing.signIn")}
            </a>
            <a
              href="/auth/owner/register?returnUrl=/admin/"
              className="bg-primaryFixed text-onPrimaryFixedVariant rounded-radii-md px-4 py-2 text-label-lg text-[13px] hover:opacity-80 transition-opacity"
            >
              {t("admin.landing.register")}
            </a>
          </div>
        )}
      </div>
    </header>
  );
}
