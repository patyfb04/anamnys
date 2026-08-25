import type { ReactNode } from "react";

export type BadgeTone = "lavender" | "teal" | "pink" | "mint" | "blue" | "neutral" | "primary";

const TONES: Record<BadgeTone, string> = {
  lavender: "bg-pastelLavender text-onPastelLavender",
  teal: "bg-pastelTeal text-onPastelTeal",
  pink: "bg-pastelPink text-onPastelPink",
  mint: "bg-pastelMint text-onPastelMint",
  blue: "bg-pastelBlue text-onPastelBlue",
  neutral: "bg-surfaceContainerHighest text-onSurfaceVariant",
  primary: "bg-primaryFixed text-onPrimaryFixedVariant",
};

interface Props {
  label: string;
  tone?: BadgeTone;
  icon?: ReactNode;
  className?: string;
}

// Small pill-shaped status/label chip used for patient status, note status, and specialty tags.
export default function Badge({ label, tone = "neutral", icon, className }: Props) {
  return (
    <span
      className={`inline-flex items-center gap-1 self-start rounded-radii-full px-2.5 py-1 text-label-md uppercase ${TONES[tone]} ${className ?? ""}`}
    >
      {icon}
      {label}
    </span>
  );
}
