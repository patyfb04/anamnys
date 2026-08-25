import { Link, useLocation } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import {
  Users,
  Calendar,
  Mic,
  FileEdit,
  FileInput,
  BarChart3,
  Share2,
  ShieldCheck,
  CreditCard,
  Settings,
  type LucideIcon,
} from "lucide-react";
import { featureFlagsMockApi, type DashboardNavFeature } from "@/api-mock/featureFlags";
import { useAuthStore } from "@/lib/store/authStore";

interface NavItem {
  href: string;
  Icon: LucideIcon;
  labelKey: string;
  matchPrefix: string;
  feature?: DashboardNavFeature; // undefined = always on (real, shipped route)
}

const NAV_ITEMS: NavItem[] = [
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
  { href: "/settings", Icon: Settings, labelKey: "nav.settings", matchPrefix: "/settings" },
];

// Fixed left rail shown at md+ (desktop), replacing the mobile TopBar/AppTabBar shell. Mirrors
// specs/UI/Dashboard's SideNavBar, but only lists the 4 shipped routes unconditionally — the
// other 6 are gated behind api-mock/featureFlags so they can be staged in ahead of release
// without a code change once each feature actually ships.
export default function Sidenav() {
  const { pathname } = useLocation();
  const { t } = useTranslation();
  const { user } = useAuthStore();

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
          <img src="/logo1.png" alt="Anamnys" width={170} height={34} className="w-full h-full object-contain" />
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
      {user && (
        <div className="px-6 mt-4">
          <Link to="/settings/account" className="flex items-center gap-3">
            <div className="w-10 h-10 rounded-full bg-primaryContainer text-onPrimaryContainer flex items-center justify-center text-label-lg">
              {user.name.slice(0, 1).toUpperCase()}
            </div>
            <div>
              <p className="text-label-lg text-onSurface">{user.name}</p>
              <p className="text-label-md text-onSurfaceVariant">{t("nav.viewProfile")}</p>
            </div>
          </Link>
        </div>
      )}
    </nav>
  );
}
