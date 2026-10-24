import { useTranslation } from "react-i18next";
import type { AdminThemeTerm } from "@anamnys/shared/api/adminThemes";
import Badge, { type BadgeTone } from "@anamnys/shared/ui/Badge";

const STATUS_TONE: Record<AdminThemeTerm["status"], BadgeTone> = {
  active: "mint",
  retired: "neutral",
  superseded: "pink",
};

const ACTION = "text-label-md text-primary hover:underline";

interface Handlers {
  canEdit: boolean;
  selected: Set<string>;
  onToggle: (id: string) => void;
  onAddChild: (term: AdminThemeTerm) => void;
  onRename: (term: AdminThemeTerm) => void;
  onRetire: (term: AdminThemeTerm) => void;
  onSplit: (term: AdminThemeTerm) => void;
}

export default function VocabularyTree({ terms, ...handlers }: Handlers & { terms: AdminThemeTerm[] }) {
  const roots = terms.filter((x) => x.parentId === null);
  const childrenOf = (id: string) => terms.filter((x) => x.parentId === id);

  return (
    <ul className="flex flex-col rounded-radii-lg border border-outlineVariant bg-surfaceContainerLowest divide-y divide-outlineVariant">
      {roots.map((root) => (
        <li key={root.id}>
          <TermRow term={root} terms={terms} {...handlers} />
          {childrenOf(root.id).map((child) => (
            <TermRow key={child.id} term={child} terms={terms} child {...handlers} />
          ))}
        </li>
      ))}
    </ul>
  );
}

function TermRow({
  term,
  terms,
  child = false,
  canEdit,
  selected,
  onToggle,
  onAddChild,
  onRename,
  onRetire,
  onSplit,
}: Handlers & { term: AdminThemeTerm; terms: AdminThemeTerm[]; child?: boolean }) {
  const { t } = useTranslation();
  const active = term.status === "active";
  const successors = term.successorIds
    .map((id) => terms.find((x) => x.id === id)?.label)
    .filter((label): label is string => !!label);
  const since = new Date(`${term.introducedOn}T00:00:00`).toLocaleDateString("pt-BR");

  return (
    <div className={`flex flex-wrap items-center gap-x-4 gap-y-2 px-4 py-3 ${child ? "pl-10 bg-surfaceContainerLow" : ""}`}>
      {canEdit && (
        <input
          type="checkbox"
          disabled={!active}
          checked={selected.has(term.id)}
          onChange={() => onToggle(term.id)}
          aria-label={t("admin.vocabulary.select", { label: term.label })}
          className="h-4 w-4 accent-primary"
        />
      )}
      <div className="min-w-0 flex-1 basis-60">
        <p className={`text-body-lg ${active ? "text-onSurface" : "text-onSurfaceVariant"}`}>{term.label}</p>
        <p className="text-body-sm text-onSurfaceVariant">
          {term.code} · {t("admin.vocabulary.introducedOn", { date: since })}
          {successors.length > 0 && ` · ${t("admin.vocabulary.successors", { labels: successors.join(", ") })}`}
        </p>
      </div>
      <Badge label={t(`admin.vocabulary.status.${term.status}`)} tone={STATUS_TONE[term.status]} />
      <span className="text-body-md text-onSurfaceVariant">{t("admin.vocabulary.tagCount", { count: term.activeTagCount })}</span>
      {canEdit && active && (
        <div className="flex flex-wrap gap-3">
          {!child && (
            <button type="button" className={ACTION} onClick={() => onAddChild(term)}>
              {t("admin.vocabulary.actions.addChild")}
            </button>
          )}
          <button type="button" className={ACTION} onClick={() => onRename(term)}>
            {t("admin.vocabulary.actions.rename")}
          </button>
          <button type="button" className={ACTION} onClick={() => onSplit(term)}>
            {t("admin.vocabulary.actions.split")}
          </button>
          <button type="button" className={ACTION} onClick={() => onRetire(term)}>
            {t("admin.vocabulary.actions.retire")}
          </button>
        </div>
      )}
    </div>
  );
}
