import { useEffect, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { X } from "lucide-react";

interface Props {
  title: string;
  onClose: () => void;
  children: ReactNode;
  footer: ReactNode;
}

export default function Modal({ title, onClose, children, footer }: Props) {
  const { t } = useTranslation();

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [onClose]);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center md:p-6">
      <div className="absolute inset-0 bg-black/40" onClick={onClose} aria-hidden />
      <div
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className="relative flex flex-col w-full h-full bg-surfaceContainerLowest md:h-auto md:max-h-[90vh] md:max-w-xl md:rounded-radii-xl md:shadow-xl"
      >
        <div className="flex items-center justify-between px-5 py-4 border-b border-outlineVariant">
          <h2 className="text-headline-sm text-onSurface">{title}</h2>
          <button
            type="button"
            onClick={onClose}
            aria-label={t("common.close")}
            className="p-1.5 rounded-radii-full text-onSurfaceVariant hover:bg-pastelLavender"
          >
            <X size={20} />
          </button>
        </div>
        <div className="flex-1 overflow-y-auto">{children}</div>
        <div className="flex justify-end gap-2 px-5 py-3 border-t border-outlineVariant">{footer}</div>
      </div>
    </div>
  );
}
