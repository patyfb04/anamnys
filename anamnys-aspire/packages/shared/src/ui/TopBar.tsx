import { Bell, Settings, HelpCircle } from "lucide-react";
import { Link } from "@tanstack/react-router";
import logo from "../assets/logo1.png";
import { useTranslation } from "react-i18next";
import AccountMenu from "./AccountMenu";

export default function TopBar({ title }: { title?: string }) {
  const { t } = useTranslation();
  return (
    <>
      {/* Mobile row: logo/title left, icons right. Hidden at md+ once Sidenav carries the logo. */}
      <div className="md:hidden h-16 flex items-center justify-between px-4 bg-surface border-b border-surfaceVariant">
        <div className="flex items-center gap-3">
          {title ? (
            <span className="text-headline-sm text-primary">{title}</span>
          ) : (
            <div className="w-[140px] h-7">
              <img
                src={logo}
                alt="Anamnys"
                width={140}
                height={28}
                className="w-full h-full object-contain"
              />
            </div>
          )}
        </div>
        <div className="flex items-center gap-1.5">
          <button className="p-2 rounded-full hover:bg-surfaceContainerLow">
            <Bell size={22} className="text-primary" />
          </button>
          <AccountMenu realm="provider" />
        </div>
      </div>

      {/* Desktop row: icons right. No global search here — the patient list has its own,
          server-backed search. AccountMenu is here too: it is the only place to reach account
          security and sign-out. Settings is the gear icon (and AppTabBar on mobile). */}
      <div className="hidden md:flex h-16 items-center justify-end gap-4 px-6 bg-surface/80 backdrop-blur-md border-b border-surfaceVariant sticky top-0 z-30">
        <div className="flex items-center gap-1.5 shrink-0">
          <button className="p-2 rounded-full text-onSurfaceVariant hover:text-primary hover:bg-surfaceContainerLow transition-colors">
            <Bell size={22} />
          </button>
          <Link
            to="/settings"
            aria-label={t("nav.settings")}
            className="p-2 rounded-full text-onSurfaceVariant hover:text-primary hover:bg-surfaceContainerLow transition-colors"
          >
            <Settings size={22} />
          </Link>
          <button className="p-2 rounded-full text-onSurfaceVariant hover:text-primary hover:bg-surfaceContainerLow transition-colors">
            <HelpCircle size={22} />
          </button>
          <AccountMenu realm="provider" />
        </div>
      </div>
    </>
  );
}
