import { Link } from "@tanstack/react-router";
import { AtSign, Share2 } from "lucide-react";
import { useTranslation } from "react-i18next";

const LINKS: { href: string; labelKey: string }[] = [
  { href: "/privacy-policy", labelKey: "welcome.footerPrivacy" },
  { href: "/terms-of-service", labelKey: "welcome.footerTerms" },
  { href: "/faq", labelKey: "welcome.footerFAQ" },
  { href: "/contact-support", labelKey: "welcome.footerContact" },
];

// Shared light-gray footer strip for every public marketing page — matches specs/UI/Landing
// Page/code.html's <footer> (bg-surface-container, full-bleed, content capped at max-w-7xl).
export default function MarketingFooter() {
  const { t } = useTranslation();

  return (
    <footer className="w-full bg-surfaceContainer border-t border-outlineVariant px-4 md:px-12 py-8">
      <div className="max-w-7xl mx-auto flex flex-col md:flex-row items-center justify-between gap-4">
        <div className="w-30 h-6.5 shrink-0">
          <img
            src="/logo1.png"
            alt="Anamnys"
            width={120}
            height={26}
            className="w-full h-full object-contain grayscale opacity-80"
          />
        </div>

        <div className="flex flex-wrap justify-center gap-4">
          {LINKS.map((link) => (
            <Link
              key={link.href}
              to={link.href}
              className="text-body-md text-onSurfaceVariant hover:text-primary transition-colors opacity-80 hover:opacity-100"
            >
              {t(link.labelKey)}
            </Link>
          ))}
        </div>

        <div className="flex items-center gap-4 shrink-0">
          <p className="text-body-md text-onSurfaceVariant/70 whitespace-nowrap">
            {t("welcome.footerCopyright")}
          </p>
          <AtSign size={20} className="text-onSurfaceVariant" />
          <Share2 size={20} className="text-onSurfaceVariant" />
        </div>
      </div>
    </footer>
  );
}
