import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { ChevronDown } from "lucide-react";

interface Props {
  title: string;
  icon: ReactNode;
  action?: ReactNode;
  children: ReactNode;
  className?: string;
  /** When set, the card starts collapsed and remembers its state in this browser under this id. */
  collapsibleId?: string;
}

const storageKey = (id: string) => `anamnys.card.${id}.open`;

function readOpen(id: string | undefined): boolean {
  if (!id) return true;
  try {
    return localStorage.getItem(storageKey(id)) === "1";
  } catch {
    return false;
  }
}

export default function DetailCard({ title, icon, action, children, className, collapsibleId }: Props) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(() => readOpen(collapsibleId));

  const toggle = () => {
    const next = !open;
    setOpen(next);
    try {
      localStorage.setItem(storageKey(collapsibleId!), next ? "1" : "0");
    } catch {
      // Remembering the state is a convenience only.
    }
  };

  const heading = (
    <>
      <span className="text-primary">{icon}</span>
      {title}
    </>
  );

  return (
    <section
      onClick={collapsibleId && !open ? toggle : undefined}
      className={`bg-surfaceContainerLowest rounded-radii-xl border border-outlineVariant p-5 shadow-sm ${collapsibleId && !open ? "cursor-pointer hover:bg-pastelLavender" : ""} ${className ?? ""}`}
    >
      <div
        onClick={collapsibleId && open ? toggle : undefined}
        className={`flex items-center justify-between gap-2 ${open ? "mb-4" : ""} ${collapsibleId && open ? "cursor-pointer" : ""}`}
      >
        <h2 className="text-headline-sm text-onSurface">
          {collapsibleId ? (
            <button
              type="button"
              onClick={(e) => {
                e.stopPropagation();
                toggle();
              }}
              aria-expanded={open}
              title={open ? t("common.collapse") : t("common.expand")}
              className="flex items-center gap-2 rounded-radii-md hover:underline"
            >
              {heading}
              <ChevronDown
                size={18}
                className={`text-onSurfaceVariant transition-transform ${open ? "rotate-180" : ""}`}
              />
            </button>
          ) : (
            <span className="flex items-center gap-2">{heading}</span>
          )}
        </h2>
        {action && <div onClick={(e) => e.stopPropagation()}>{action}</div>}
      </div>
      {open && children}
    </section>
  );
}
