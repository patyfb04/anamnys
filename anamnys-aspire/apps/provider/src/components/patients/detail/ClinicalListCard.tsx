import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Pencil, Plus, Trash2 } from "lucide-react";
import { ApiError } from "@anamnys/shared/api/client";
import DetailCard from "./DetailCard";

type Errors = Record<string, string[]>;

export interface ItemAction<TItem> {
  label: (item: TItem) => string;
  run: (item: TItem) => Promise<void>;
}

interface Props<TItem extends { id: string }, TDraft> {
  title: string;
  icon: ReactNode;
  emptyLabel: string;
  addLabel: string;
  items: TItem[];
  renderItem: (item: TItem) => ReactNode;
  emptyDraft: () => TDraft;
  toDraft: (item: TItem) => TDraft;
  renderFields: (draft: TDraft, setDraft: (draft: TDraft) => void, errors: Errors) => ReactNode;
  onAdd: (draft: TDraft) => Promise<void>;
  onUpdate: (item: TItem, draft: TDraft) => Promise<void>;
  onRemove: (item: TItem) => Promise<void>;
  // An extra per-item action such as "Resolver" or "Encerrar".
  extraAction?: ItemAction<TItem>;
  readOnly?: boolean;
}

// One clinical list on the detail page (diagnoses, medications, objectives): shows the
// items, edits one at a time in place, and adds new ones. Server validation errors show
// under the matching field.
export default function ClinicalListCard<TItem extends { id: string }, TDraft>(props: Props<TItem, TDraft>) {
  const { t } = useTranslation();
  const [editingId, setEditingId] = useState<string | "new" | null>(null);
  const [draft, setDraft] = useState<TDraft | null>(null);
  const [errors, setErrors] = useState<Errors>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const startAdd = () => {
    setEditingId("new");
    setDraft(props.emptyDraft());
    setErrors({});
  };

  const startEdit = (item: TItem) => {
    setEditingId(item.id);
    setDraft(props.toDraft(item));
    setErrors({});
  };

  const cancel = () => {
    setEditingId(null);
    setDraft(null);
    setErrors({});
  };

  const run = async (action: () => Promise<void>, onDone?: () => void) => {
    setBusy(true);
    setError(null);
    try {
      await action();
      onDone?.();
    } catch (e) {
      if (e instanceof ApiError && e.errors) setErrors(e.errors);
      else setError(t("patients.detail.actionFailed"));
    } finally {
      setBusy(false);
    }
  };

  const save = (item?: TItem) =>
    run(() => (item ? props.onUpdate(item, draft as TDraft) : props.onAdd(draft as TDraft)), cancel);

  const editor = (item?: TItem) => (
    <div className="flex flex-col gap-3 p-3 rounded-radii-lg bg-surfaceContainerLow">
      {props.renderFields(draft as TDraft, setDraft, errors)}
      <div className="flex justify-end gap-2">
        <button type="button" onClick={cancel} className="px-3 py-1.5 text-label-lg text-onSurfaceVariant hover:text-onSurface">
          {t("common.cancel")}
        </button>
        <button
          type="button"
          disabled={busy}
          onClick={() => save(item)}
          className="px-4 py-1.5 bg-primary text-onPrimary rounded-radii-md text-label-lg hover:opacity-90 disabled:opacity-60"
        >
          {t("common.save")}
        </button>
      </div>
    </div>
  );

  return (
    <DetailCard
      title={props.title}
      icon={props.icon}
      action={
        !props.readOnly && editingId === null ? (
          <button type="button" onClick={startAdd} className="inline-flex items-center gap-1 text-label-lg text-primary hover:underline">
            <Plus size={16} />
            {props.addLabel}
          </button>
        ) : null
      }
    >
      {error && <p className="mb-2 text-label-md text-error">{error}</p>}
      {props.items.length === 0 && editingId !== "new" && (
        <p className="text-body-md text-onSurfaceVariant">{props.emptyLabel}</p>
      )}
      <ul className="flex flex-col gap-2">
        {props.items.map((item) =>
          editingId === item.id ? (
            <li key={item.id}>{editor(item)}</li>
          ) : (
            <li key={item.id} className="group flex items-start gap-2">
              <div className="flex-1 min-w-0">{props.renderItem(item)}</div>
              {!props.readOnly && editingId === null && (
                <div className="flex items-center gap-1 shrink-0">
                  {props.extraAction && (
                    <button
                      type="button"
                      disabled={busy}
                      onClick={() => run(() => props.extraAction!.run(item))}
                      className="px-2 py-1 text-label-md text-primary hover:underline"
                    >
                      {props.extraAction.label(item)}
                    </button>
                  )}
                  <IconButton label={t("common.edit")} onClick={() => startEdit(item)}>
                    <Pencil size={16} />
                  </IconButton>
                  <IconButton label={t("common.remove")} danger onClick={() => run(() => props.onRemove(item))}>
                    <Trash2 size={16} />
                  </IconButton>
                </div>
              )}
            </li>
          ),
        )}
        {editingId === "new" && <li>{editor()}</li>}
      </ul>
    </DetailCard>
  );
}

function IconButton({
  label,
  danger,
  onClick,
  children,
}: {
  label: string;
  danger?: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      onClick={onClick}
      className={`p-1.5 rounded-radii-full text-onSurfaceVariant hover:bg-surfaceContainer ${danger ? "hover:text-error" : "hover:text-primary"}`}
    >
      {children}
    </button>
  );
}
