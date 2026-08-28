import type { ReactNode } from "react";
import { Check } from "lucide-react";
import clsx from "clsx";

interface Props {
  checked: boolean;
  onToggle: () => void;
  children?: ReactNode;
  className?: string;
}

// Square checkbox with a checkmark, matching the SignUp mockup's agreement checkbox. Label is
// passed as children so callers can mix plain and styled/linked text.
export default function Checkbox({ checked, onToggle, children, className }: Props) {
  return (
    <button
      type="button"
      role="checkbox"
      aria-checked={checked}
      onClick={onToggle}
      className={clsx("flex items-start gap-2.5 text-left", className)}
    >
      <span
        className={clsx(
          "mt-0.5 w-5 h-5 rounded-radii-sm border-[1.5px] flex items-center justify-center shrink-0",
          checked ? "bg-primary border-primary" : "border-outlineVariant"
        )}
      >
        {checked && <Check size={14} className="text-onPrimary" />}
      </span>
      {children && <span className="flex-1">{children}</span>}
    </button>
  );
}
