"use client";

import Image from "next/image";
import { Bell, Search, Settings, HelpCircle } from "lucide-react";
import { useTranslation } from "react-i18next";
import AccountMenu from "@/components/AccountMenu";

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
              <Image
                src="/logo1.png"
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
          <AccountMenu />
        </div>
      </div>

      {/* Desktop row: search bar left, icons right, padding matched to main's md:p-6 so the
          search box and icon cluster line up with the greeting/tiles below. No AccountMenu here
          — Sidenav's profile footer already covers desktop, matching the spec's own header
          (its avatar is md:hidden there too). */}
      <div className="hidden md:flex h-16 items-center justify-between gap-4 px-6 bg-surface/80 backdrop-blur-md border-b border-surfaceVariant sticky top-0 z-30">
        <div className="relative w-full max-w-md">
          <Search size={18} className="absolute left-3 top-1/2 -translate-y-1/2 text-onSurfaceVariant" />
          <input
            type="text"
            placeholder={t("patients.list.searchPlaceholder")}
            className="w-full h-10 bg-surfaceContainerLow border border-outlineVariant focus:border-primary focus:border-2 rounded-radii-md py-2 pl-10 pr-4 text-onSurface placeholder:text-onSurfaceVariant outline-none text-body-md transition-all"
          />
        </div>
        <div className="flex items-center gap-1.5 shrink-0">
          <button className="p-2 rounded-full text-onSurfaceVariant hover:text-primary hover:bg-surfaceContainerLow transition-colors">
            <Bell size={22} />
          </button>
          <button className="p-2 rounded-full text-onSurfaceVariant hover:text-primary hover:bg-surfaceContainerLow transition-colors">
            <Settings size={22} />
          </button>
          <button className="p-2 rounded-full text-onSurfaceVariant hover:text-primary hover:bg-surfaceContainerLow transition-colors">
            <HelpCircle size={22} />
          </button>
        </div>
      </div>
    </>
  );
}
