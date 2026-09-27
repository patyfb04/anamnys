import { Link, useLocation } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import {
  LayoutDashboard,
  Users,
  Calendar,
  Mic,
  FileEdit,
  FileInput,
  BarChart3,
  Share2,
  ShieldCheck,
  CreditCard,
  type LucideIcon,
} from "lucide-react";
// Imported, not "/logo1.png": that file lives in apps/web/public, so it only resolves
// at site root in production and 404s on the other apps' own dev servers.
import logo from "../assets/logo1.png";
import { featureFlagsMockApi, type DashboardNavFeature } from "@anamnys/shared/api-mock/featureFlags";

interface NavItem {
  href: string;
  Icon: LucideIcon;
  labelKey: string;
  matchPrefix: string;
  feature?: DashboardNavFeature; // undefined = always on (real, shipped route)
}

const NAV_ITEMS: NavItem[] = [
  { href: "/dashboard", Icon: LayoutDashboard, labelKey: "nav.dashboard", matchPrefix: "/dashboard" },
  { href: "/patients", Icon: Users, labelKey: "nav.patients", matchPrefix: "/patients" },
  { href: "/calendar", Icon: Calendar, labelKey: "nav.calendar", matchPrefix: "/calendar", feature: "calendar" },
  { href: "/live-session", Icon: Mic, labelKey: "nav.liveSession", matchPrefix: "/live-session", feature: "liveSession" },
  { href: "/note-editor", Icon: FileEdit, labelKey: "nav.noteEditor", matchPrefix: "/note-editor", feature: "noteEditor" },
  {
    href: "/document-intake",
    Icon: FileInput,
    labelKey: "nav.documentIntake",
    matchPrefix: "/document-intake",
    feature: "documentIntake",
  },
  { href: "/analytics", Icon: BarChart3, labelKey: "nav.analytics", matchPrefix: "/analytics", feature: "analytics" },
  {
    href: "/secure-sharing",
    Icon: Share2,
    labelKey: "nav.secureSharing",
    matchPrefix: "/secure-sharing",
    feature: "secureSharing",
  },
  {
    href: "/compliance-vault",
    Icon: ShieldCheck,
    labelKey: "nav.complianceVault",
    matchPrefix: "/compliance-vault",
    feature: "complianceVault",
  },
  { href: "/billing", Icon: CreditCard, labelKey: "nav.billing", matchPrefix: "/billing", feature: "billing" },
];

// Fixed left rail shown at md+ (desktop), replacing the mobile TopBar/AppTabBar shell. Mirrors
// specs/UI/Dashboard's SideNavBar, but only lists the 4 shipped routes unconditionally — the
// other 6 are gated behind api-mock/featureFlags so they can be staged in ahead of release
// without a code change once each feature actually ships.
export default function Sidenav() {
  const { pathname } = useLocation();
  const { t } = useTranslation();

  const { data: flags } = useQuery({
    queryKey: ["feature-flags"],
    queryFn: () => featureFlagsMockApi.getFlags(),
    staleTime: Infinity,
  });

  const items = NAV_ITEMS.filter((item) => !item.feature || flags?.[item.feature]);

  return (
    <nav className="hidden md:flex h-screen flex-col py-6 border-r border-outlineVariant bg-surfaceContainerLow fixed left-0 top-0 z-40 w-72">
      <div className="px-6 mb-8">
        <div className="w-[170px] h-[34px]">
          <img src={logo} alt="Anamnys" width={170} height={34} className="w-full h-full object-contain" />
        </div>
        <p className="text-label-md text-onSurfaceVariant mt-1 text-center">{t("auth.login.subtitle")}</p>
      </div>
      <div className="flex-1 overflow-y-auto space-y-1">
        {items.map((item) => {
          const isActive = pathname.startsWith(item.matchPrefix);
          return (
            <Link
              key={item.href}
              to={item.href}
              className={`flex items-center gap-4 rounded-radii-md px-4 py-3 mx-2 transition-colors duration-200 ${
                isActive
                  ? "bg-primaryContainer text-onPrimaryContainer"
                  : "text-onSurfaceVariant hover:bg-secondaryContainer hover:text-onSecondaryContainer"
              }`}
            >
              <item.Icon size={20} />
              <span className="text-label-lg">{t(item.labelKey)}</span>
            </Link>
          );
        })}
      </div>
    </nav>
  );
}
