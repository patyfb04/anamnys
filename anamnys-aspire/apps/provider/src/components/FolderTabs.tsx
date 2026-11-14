import type { ReactNode } from "react";

interface Props<T extends string> {
  tabs: { id: T; label: ReactNode }[];
  active: T;
  onSelect: (id: T) => void;
  label: string;
  children: ReactNode;
}

// Folder-style tabs: the active tab joins the bordered panel below it (patient page, settings).
export default function FolderTabs<T extends string>({ tabs, active, onSelect, label, children }: Props<T>) {
  return (
    <div className="flex flex-col">
      <div role="tablist" aria-label={label} className="flex flex-wrap items-end gap-1">
        {tabs.map(({ id, label: tabLabel }) => (
          <button
            key={id}
            type="button"
            role="tab"
            aria-selected={active === id}
            onClick={() => onSelect(id)}
            className={`relative -mb-px inline-flex items-center gap-2 rounded-t-radii-lg border border-outlineVariant px-4 py-2 text-label-lg ${
              active === id
                ? "z-10 border-b-surfaceContainerLow bg-surfaceContainerLow text-onSurface"
                : "bg-surfaceContainerHighest text-onSurfaceVariant hover:bg-pastelLavender"
            }`}
          >
            {tabLabel}
          </button>
        ))}
      </div>
      <div role="tabpanel" className="flex flex-col gap-6 rounded-radii-xl rounded-tl-none border border-outlineVariant bg-surfaceContainerLow p-4 md:p-6">
        {children}
      </div>
    </div>
  );
}
