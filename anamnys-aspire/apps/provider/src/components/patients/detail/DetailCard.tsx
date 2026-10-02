import type { ReactNode } from "react";

interface Props {
  title: string;
  icon: ReactNode;
  action?: ReactNode;
  children: ReactNode;
  className?: string;
}

export default function DetailCard({ title, icon, action, children, className }: Props) {
  return (
    <section
      className={`bg-surfaceContainerLowest rounded-radii-xl border border-outlineVariant p-5 shadow-sm ${className ?? ""}`}
    >
      <div className="flex items-center justify-between gap-2 mb-4">
        <h2 className="flex items-center gap-2 text-headline-sm text-onSurface">
          <span className="text-primary">{icon}</span>
          {title}
        </h2>
        {action}
      </div>
      {children}
    </section>
  );
}
