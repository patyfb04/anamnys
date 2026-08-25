import { useState } from "react";
import { LogOut } from "lucide-react";
import { useTranslation } from "react-i18next";
import { useAuthStore } from "@/lib/store/authStore";

// Provider avatar in the shared TopBar: clicking it opens a small menu with the signed-in
// user's name/email and a Sign Out action.
export default function AccountMenu() {
  const { t } = useTranslation();
  const { user, logout } = useAuthStore();
  const [open, setOpen] = useState(false);
  const initial = user?.name?.trim()?.[0]?.toUpperCase() ?? "D";

  return (
    <div className="relative">
      <button
        onClick={() => setOpen(true)}
        aria-label={t("settings.signOut")}
        className="w-9 h-9 rounded-full bg-primaryContainer text-onPrimaryContainer text-label-lg flex items-center justify-center"
      >
        {initial}
      </button>

      {open && (
        <>
          <div className="fixed inset-0 z-10" onClick={() => setOpen(false)} />
          <div className="absolute right-0 mt-2 w-56 bg-surfaceContainerLowest rounded-radii-lg border border-outlineVariant shadow-lg py-2 z-20">
            <div className="px-3.5 pt-1.5 pb-2.5">
              <div className="text-label-lg text-onSurface truncate">{user?.name ?? t("common.provider")}</div>
              {user?.email && <div className="text-body-md text-onSurfaceVariant truncate mt-0.5">{user.email}</div>}
            </div>
            <div className="h-px bg-outlineVariant mb-1" />
            <button
              onClick={() => {
                setOpen(false);
                logout();
              }}
              className="w-full flex items-center gap-2.5 px-3.5 py-2.5 text-body-lg text-error hover:bg-surfaceContainerLow"
            >
              <LogOut size={18} />
              {t("settings.signOut")}
            </button>
          </div>
        </>
      )}
    </div>
  );
}
