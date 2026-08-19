"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { useAuthStore } from "@/lib/store/authStore";
import TopBar from "@/components/ui/TopBar";
import AppTabBar from "@/components/ui/AppTabBar";

export default function AppLayout({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const { user, isLoading } = useAuthStore();

  useEffect(() => {
    if (!isLoading && !user) router.replace("/login");
  }, [isLoading, user, router]);

  if (isLoading || !user) return null;

  return (
    <div className="min-h-screen flex flex-col bg-surfaceContainerLow">
      <TopBar />
      <main className="flex-1">{children}</main>
      <AppTabBar />
    </div>
  );
}
