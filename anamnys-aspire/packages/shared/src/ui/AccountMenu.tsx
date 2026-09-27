import { useState } from "react";
import { Link } from "@tanstack/react-router";
import { LogOut, ShieldCheck, UserRound } from "lucide-react";
import { useTranslation } from "react-i18next";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import type { AccountRealm } from "@anamnys/shared/api/profile";

const itemClass =
  "w-full flex items-center gap-2.5 px-3.5 py-2.5 text-body-lg text-onSurface hover:bg-surfaceContainerLow";

// Signed-in user's avatar: clicking it opens a menu with their name/email, links to
// their own profile and security pages, and Sign Out. `realm` picks which BFF session
// sign-out ends.
export default function AccountMenu({ realm }: { realm: AccountRealm }) {
  const { t } = useTranslation();
  const { user, logout } = useAuthStore();
  const [open, setOpen] = useState(false);
  const fallbackName = t(realm === "patient" ? "common.patient" : "common.provider");
  const initial = (user?.name?.trim() || fallbackName)[0].toUpperCase();

  return (
    <div className="relative">
      <button
        onClick={() => setOpen(true)}
        aria-label={user?.name || fallbackName}
        className="w-9 h-9 rounded-full bg-primaryContainer text-onPrimaryContainer text-label-lg flex items-center justify-center"
      >
        {initial}
      </button>

      {open && (
        <>
          <div className="fixed inset-0 z-10" onClick={() => setOpen(false)} />
          <div className="absolute right-0 mt-2 w-64 bg-surfaceContainerLowest rounded-radii-lg border border-outlineVariant shadow-lg py-2 z-20">
            <div className="px-3.5 pt-1.5 pb-2.5">
              <div className="text-label-lg text-onSurface truncate">{user?.name || fallbackName}</div>
              {user?.email && <div className="text-body-md text-onSurfaceVariant truncate mt-0.5">{user.email}</div>}
            </div>
            <div className="h-px bg-outlineVariant mb-1" />
            <Link to="/account/profile" onClick={() => setOpen(false)} className={itemClass}>
              <UserRound size={18} />
              {t("accountMenu.profile")}
            </Link>
            <Link to="/account/security" onClick={() => setOpen(false)} className={itemClass}>
              <ShieldCheck size={18} />
              {t("accountMenu.security")}
            </Link>
            <div className="h-px bg-outlineVariant my-1" />
            <button
              onClick={() => {
                setOpen(false);
                logout(realm);
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
