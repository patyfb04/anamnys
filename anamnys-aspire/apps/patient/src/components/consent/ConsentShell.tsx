import type { ReactNode } from "react";

// Shared frame of the public consent pages (no login): a centred card on the page background.
export default function ConsentShell({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="min-h-screen flex items-start justify-center bg-surfaceContainerLow px-4 py-8 sm:items-center">
      <div className="w-full max-w-2xl bg-surfaceContainerLowest rounded-radii-xl border border-outlineVariant p-6 shadow-sm">
        <h1 className="text-headline-sm text-onSurface">{title}</h1>
        {children}
      </div>
    </div>
  );
}
