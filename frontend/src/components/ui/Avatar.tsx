// Rotation used to deterministically color initials by name — same 5 pastel pairs as the
// design tokens' AVATAR_PALETTE, expressed as Tailwind class names.
const AVATAR_PALETTE = [
  { bg: "bg-pastelLavender", fg: "text-onPastelLavender" },
  { bg: "bg-pastelBlue", fg: "text-onPastelBlue" },
  { bg: "bg-pastelMint", fg: "text-onPastelMint" },
  { bg: "bg-pastelPink", fg: "text-onPastelPink" },
  { bg: "bg-pastelTeal", fg: "text-onPastelTeal" },
];

interface Props {
  name: string; // full name; initials are derived from the first letters of each word
  size?: number;
  className?: string;
}

// Deterministically picks a color from the pastel avatar palette based on the name, so the
// same patient always renders with the same color across screens.
function colorForName(name: string) {
  let hash = 0;
  for (let i = 0; i < name.length; i++) {
    hash = (hash * 31 + name.charCodeAt(i)) >>> 0;
  }
  return AVATAR_PALETTE[hash % AVATAR_PALETTE.length];
}

function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return "?";
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

// Circular initials avatar, colored per-name from the shared pastel palette.
export default function Avatar({ name, size = 44, className }: Props) {
  const { bg, fg } = colorForName(name);
  return (
    <div
      className={`flex items-center justify-center rounded-full ${bg} ${className ?? ""}`}
      style={{ width: size, height: size }}
    >
      <span className={`text-label-lg ${fg}`} style={{ fontSize: size * 0.36 }}>
        {initials(name)}
      </span>
    </div>
  );
}
