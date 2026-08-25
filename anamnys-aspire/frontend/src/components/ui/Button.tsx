import type { ButtonHTMLAttributes, ReactNode } from "react";
import { Loader2 } from "lucide-react";
import clsx from "clsx";

export type ButtonVariant = "primary" | "outline" | "ghost";

interface Props extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, "className"> {
  title: string;
  variant?: ButtonVariant;
  loading?: boolean;
  icon?: ReactNode;
  fullWidth?: boolean;
  rounded?: "full" | "md";
  className?: string;
}

// Call-to-action button in the three variants used across the mockups: solid primary, outlined
// primary, and text-only ghost. Pill-shaped by default; pass rounded="md" for the less-rounded
// corner used on the Landing/Welcome page's hero buttons.
export default function Button({
  title,
  variant = "primary",
  loading = false,
  icon,
  fullWidth = true,
  rounded = "full",
  disabled,
  className,
  ...rest
}: Props) {
  const isDisabled = disabled || loading;

  return (
    <button
      disabled={isDisabled}
      className={clsx(
        "flex items-center justify-center gap-2 py-3.5 px-6 transition-all duration-200",
        rounded === "full" ? "rounded-radii-full" : "rounded-radii-md",
        fullWidth && "self-stretch w-full",
        variant === "primary" && "bg-primary hover:shadow-lg hover:shadow-primary/30 hover:-translate-y-0.5",
        variant === "outline" &&
          "bg-transparent border-[1.5px] border-primary hover:bg-surfaceContainerLow",
        variant === "ghost" && "bg-transparent hover:bg-surfaceContainerLow",
        "active:scale-[0.97]",
        isDisabled && "opacity-50 cursor-not-allowed",
        className
      )}
      {...rest}
    >
      {loading ? (
        <Loader2 className={clsx("animate-spin", variant === "primary" ? "text-onPrimary" : "text-primary")} size={18} />
      ) : (
        <>
          {icon}
          <span className={clsx("text-label-lg text-[15px]", variant === "primary" ? "text-onPrimary" : "text-primary")}>
            {title}
          </span>
        </>
      )}
    </button>
  );
}
