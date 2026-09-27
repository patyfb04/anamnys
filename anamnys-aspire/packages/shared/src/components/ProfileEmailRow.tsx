import { useTranslation } from "react-i18next";
import { accountActionUrl, type AccountRealm } from "@anamnys/shared/api/profile";

// The email is the Keycloak login, so it is not an editable field here: changing it is
// Keycloak's UPDATE_EMAIL action, which confirms the new address before switching.
export default function ProfileEmailRow({ realm, email }: { realm: AccountRealm; email: string | null }) {
  const { t } = useTranslation();

  return (
    <div className="mb-5">
      <div className="text-label-sm text-onSurfaceVariant mb-1.5">{t("accountPages.profile.email")}</div>
      <div className="flex items-center justify-between gap-3">
        <span className="text-body-lg text-onSurface truncate">{email ?? "—"}</span>
        <a href={accountActionUrl(realm, "UPDATE_EMAIL")} className="text-label-lg text-primary shrink-0 hover:opacity-80">
          {t("accountPages.profile.changeEmail")}
        </a>
      </div>
      <p className="mt-1 text-body-md text-onSurfaceVariant">{t("accountPages.profile.changeEmailHint")}</p>
    </div>
  );
}
