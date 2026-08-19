"use client";

import { useTranslation } from "react-i18next";

// Shared body for every marketing nav link that doesn't have a real page yet (Product/
// Solutions/Pricing/Resources — Privacy Policy, Terms of Service, and Contact/Support all
// have real content now). titleKey lets each route stay a plain server component that just
// names which i18n key to render, rather than every page needing "use client" itself.
export default function MarketingPlaceholder({ titleKey }: { titleKey: string }) {
  const { t } = useTranslation();
  return (
    <div className="max-w-2xl mx-auto px-4 py-24 text-center">
      <h1 className="text-headline-lg text-[32px] text-primary mb-3">{t(titleKey)}</h1>
      <p className="text-body-lg text-onSurfaceVariant">{t("welcome.comingSoon")}</p>
    </div>
  );
}
