"use client";

import Image from "next/image";
import { Bell } from "lucide-react";
import AccountMenu from "@/components/AccountMenu";

export default function TopBar({ title }: { title?: string }) {
  return (
    <div className="h-16 flex items-center justify-between px-4 bg-surface border-b border-surfaceVariant">
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
  );
}
