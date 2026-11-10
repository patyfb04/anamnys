interface Props {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label: string;
  disabled?: boolean;
}

// On/off switch with an accessible name; the visible text sits next to it in the caller.
export default function Switch({ checked, onChange, label, disabled }: Props) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-radii-full transition-colors disabled:opacity-50 ${checked ? "bg-primary" : "bg-outlineVariant"}`}
    >
      <span className={`inline-block size-5 rounded-radii-full bg-surfaceContainerLowest shadow transition-transform ${checked ? "translate-x-5" : "translate-x-0.5"}`} />
    </button>
  );
}
