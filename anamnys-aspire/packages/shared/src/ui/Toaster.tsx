import { Toaster as Sonner } from "sonner";

export { toast } from "sonner";

// Mounted once per app root; call `toast.success(...)` / `toast.error(...)` from anywhere.
// Toast text must never carry PHI — confirmations only ("Dados atualizados."), not record content.
export default function Toaster() {
  return (
    <Sonner
      position="top-right"
      closeButton
      toastOptions={{
        classNames: {
          toast: "!bg-surfaceContainerLowest !border-outlineVariant !text-onSurface !font-public-sans !rounded-xl",
          success: "[&_[data-icon]]:!text-primary",
          error: "!text-error [&_[data-icon]]:!text-error",
        },
      }}
    />
  );
}
