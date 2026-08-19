"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { Home, Users, Mic, Settings, LucideIcon } from "lucide-react";
import { useTranslation } from "react-i18next";

interface Tab {
  href: string;
  Icon: LucideIcon;
  labelKey: string;
  matchPrefix: string;
}

const TABS: Tab[] = [
  { href: "/home", Icon: Home, labelKey: "nav.home", matchPrefix: "/home" },
  { href: "/patients", Icon: Users, labelKey: "nav.patients", matchPrefix: "/patients" },
  { href: "/patients/new", Icon: Mic, labelKey: "nav.newNote", matchPrefix: "/notes" },
  { href: "/settings", Icon: Settings, labelKey: "nav.settings", matchPrefix: "/settings" },
];

// Custom bottom tab bar matching the mockups: inactive tabs are plain icon+label, the active
// tab gets a primary-container pill, and the "New Note" tab is always rendered as an elevated
// filled-primary mic button.
export default function AppTabBar() {
  const pathname = usePathname();
  const { t } = useTranslation();

  return (
    <div className="flex justify-around items-end bg-surface border-t border-surfaceVariant pt-2.5 pb-3">
      {TABS.map((tab) => {
        const isActive = pathname.startsWith(tab.matchPrefix);
        const isNewNote = tab.labelKey === "nav.newNote";
        const label = t(tab.labelKey);

        if (isNewNote) {
          return (
            <Link key={tab.href} href={tab.href} className="flex-1 flex flex-col items-center">
              <span className="w-11 h-11 rounded-full bg-primary flex items-center justify-center -mt-[22px] shadow-lg shadow-primary/30">
                <tab.Icon size={22} className="text-onPrimary" />
              </span>
              <span className="text-label-md text-onSurfaceVariant normal-case mt-1">{label}</span>
            </Link>
          );
        }

        return (
          <Link key={tab.href} href={tab.href} className="flex-1 flex flex-col items-center">
            <span className={`flex flex-col items-center gap-0.5 px-4 py-1.5 rounded-radii-full ${isActive ? "bg-primaryContainer" : ""}`}>
              <tab.Icon size={20} className={isActive ? "text-onPrimaryContainer" : "text-onSurfaceVariant"} />
              <span className={`text-label-md normal-case ${isActive ? "text-onPrimaryContainer" : "text-onSurfaceVariant"}`}>{label}</span>
            </span>
          </Link>
        );
      })}
    </div>
  );
}
