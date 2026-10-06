import type { ReactNode } from "react";

// Shared frame for the account pages ("Dados pessoais", "Acesso e segurança").
export default function AccountPage({
  title,
  subtitle,
  children,
}: {
  title: string;
  subtitle: string;
  children: ReactNode;
}) {
  return (
    <div className="p-4 md:p-8">
      <h1 className="text-headline-md text-onSurface">{title}</h1>
      <p className="mt-1 mb-6 text-body-lg text-onSurfaceVariant">{subtitle}</p>
      <div className="bg-surfaceContainerLowest rounded-radii-lg border border-outlineVariant p-5 md:p-6">
        {children}
      </div>
    </div>
  );
}
