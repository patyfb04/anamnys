import { KeyRound, Smartphone, type LucideIcon } from "lucide-react";
import { useTranslation } from "react-i18next";
import AccountPage from "@anamnys/shared/components/AccountPage";
import { accountActionUrl, type AccountRealm } from "@anamnys/shared/api/profile";

// Password and 2FA are Keycloak's; each card starts the matching Keycloak action,
// which renders with the Anamnys login theme and returns here.
export default function SecurityPage({ realm }: { realm: AccountRealm }) {
  const { t } = useTranslation();

  return (
    <AccountPage title={t("accountMenu.security")} subtitle={t("accountPages.security.subtitle")}>
      <SecurityRow
        Icon={KeyRound}
        title={t("accountPages.security.passwordTitle")}
        body={t("accountPages.security.passwordBody")}
        action={t("accountPages.security.passwordAction")}
        href={accountActionUrl(realm, "UPDATE_PASSWORD")}
      />
      <div className="h-px bg-outlineVariant my-5" />
      <SecurityRow
        Icon={Smartphone}
        title={t("accountPages.security.totpTitle")}
        body={t("accountPages.security.totpBody")}
        action={t("accountPages.security.totpAction")}
        href={accountActionUrl(realm, "CONFIGURE_TOTP")}
      />
    </AccountPage>
  );
}

function SecurityRow({
  Icon,
  title,
  body,
  action,
  href,
}: {
  Icon: LucideIcon;
  title: string;
  body: string;
  action: string;
  href: string;
}) {
  return (
    <div className="flex flex-col sm:flex-row sm:items-center gap-4">
      <Icon size={24} className="text-primary shrink-0" />
      <div className="flex-1">
        <div className="text-label-lg text-onSurface">{title}</div>
        <div className="text-body-md text-onSurfaceVariant mt-0.5">{body}</div>
      </div>
      <a
        href={href}
        className="shrink-0 text-center rounded-radii-md border border-primary text-primary px-4 py-2 text-label-lg hover:bg-primary/5"
      >
        {action}
      </a>
    </div>
  );
}
