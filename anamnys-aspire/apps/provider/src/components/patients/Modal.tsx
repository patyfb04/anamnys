import { useEffect, useRef, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { X } from "lucide-react";

interface Props {
  title: string;
  onClose: () => void;
  children: ReactNode;
  footer?: ReactNode;
}

// Mount only while open. Centered panel at md+, full screen below. Esc and the backdrop
// call onClose; the caller decides whether closing needs a confirmation.
export default function Modal({ title, onClose, children, footer }: Props) {
  const { t } = useTranslation();
  const panelRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  useEffect(() => {
    panelRef.current?.focus();
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = previousOverflow;
    };
  }, []);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center md:p-6">
      <div className="absolute inset-0 bg-black/40" onClick={onClose} aria-hidden />
      <div
        ref={panelRef}
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className="relative flex flex-col w-full h-full bg-surfaceContainerLowest outline-none md:h-auto md:max-h-[90vh] md:max-w-3xl md:rounded-radii-xl md:shadow-xl"
      >
        <div className="flex items-center justify-between px-5 py-4 border-b border-outlineVariant">
          <h2 className="text-headline-sm text-onSurface">{title}</h2>
          <button
            type="button"
            onClick={onClose}
            aria-label={t("common.close")}
            className="p-1.5 rounded-radii-full text-onSurfaceVariant hover:bg-surfaceContainer"
          >
            <X size={20} />
          </button>
        </div>
        <div className="flex-1 overflow-y-auto">{children}</div>
        {footer && <div className="flex justify-end gap-2 px-5 py-3 border-t border-outlineVariant">{footer}</div>}
      </div>
    </div>
  );
}
