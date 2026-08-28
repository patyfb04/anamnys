import { Link, useLocation } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { appUrls } from "@anamnys/shared/lib/appUrls";

const NAV_ITEMS: { key: string; href: string; labelKey: string }[] = [
  { key: "product", href: "/product", labelKey: "welcome.nav.product" },
  { key: "pricing", href: "/pricing", labelKey: "welcome.nav.pricing" },
  { key: "aboutus", href: "/aboutus", labelKey: "welcome.nav.aboutUs" },
];

// Shared top bar for every public marketing page (landing + the Product/Solutions/Pricing/
// Resources pages it links to): logo left, center nav, Sign In / Get Started right — matches
// specs/UI/Landing Page/code.html's <nav>. Sticky so it stays visible while scrolling the
// landing page's long feature list.
export default function MarketingHeader({
  hideLogo = false,
}: {
  // Landing page renders its own big logo above the headline, so the header's small
  // logo would be a redundant duplicate there — hide it and keep a spacer so the nav
  // stays centered.
  hideLogo?: boolean;
}) {
  const { pathname } = useLocation();
  const { t } = useTranslation();
  // The landing page ("/") IS the Product page conceptually — the mockup shows "Product"
  // highlighted while viewing it, so treat both paths as the same active tab.
  const isActive = (href: string) =>
    pathname === href || (href === "/product" && pathname === "/");

  return (
    <header className="sticky top-0 z-50 bg-surface border-b border-surfaceVariant px-4 md:px-12">
      <div className="h-16 max-w-7xl mx-auto flex items-center justify-between gap-4">
        {hideLogo ? (
          <div className="w-[150px] h-[40px] shrink-0" />
        ) : (
          <Link to="/" className="w-[145px] h-[35px] shrink-0">
            <img
              src="/logo1.png"
              alt="Anamnys"
              width={145}
              height={35}
              className="w-full h-full object-contain"
            />
          </Link>
        )}

        <nav className="hidden md:flex items-center gap-6">
          {NAV_ITEMS.map((item) => (
            <Link
              key={item.key}
              to={item.href}
              className={
                isActive(item.href)
                  ? "text-primary font-bold border-b-2 border-primary py-4"
                  : "text-onSurfaceVariant font-medium py-4 hover:text-primary transition-colors"
              }
            >
              {t(item.labelKey)}
            </Link>
          ))}
        </nav>

        <div className="flex items-center gap-2 shrink-0">
          <a
            href={appUrls.providerLogin}
            className="text-label-lg text-primary hover:opacity-80 transition-opacity px-2"
          >
            {t("welcome.signIn")}
          </a>
          <a
            href={appUrls.providerRegister}
            className="bg-primaryFixed text-onPrimaryFixedVariant rounded-radii-md px-4 py-2 text-label-lg text-[13px] hover:opacity-80 transition-opacity"
          >
            {t("welcome.getStarted")}
          </a>
        </div>
      </div>
    </header>
  );
}
