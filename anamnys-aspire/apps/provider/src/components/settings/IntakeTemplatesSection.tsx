import { useState, type ReactNode } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Archive, Copy, Eye, EyeOff, Loader2, Pencil } from "lucide-react";
import { intakeApi } from "@anamnys/shared/api/intake";
import type { IntakeTemplateDetail, IntakeTemplateSummary } from "@anamnys/shared/lib/types";
import Badge from "@anamnys/shared/ui/Badge";
import { toast } from "@anamnys/shared/ui/Toaster";
import ConfirmDialog from "@/components/patients/ConfirmDialog";
import IntakeTemplateEditor from "@/components/intake/IntakeTemplateEditor";
import { errorCode, targetLabelKey } from "@/components/intake/intakeModel";

const TEMPLATES_KEY = ["intake-templates"];

// Settings → Prontuário → "Modelos de anamnese" (design/specs/2026-11-12-structured-intake-design.md §5): the Anamnys
// template (view + Adaptar), the provider's own templates (edit, archive) and which one new intakes use.
export default function IntakeTemplatesSection() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const query = useQuery({ queryKey: TEMPLATES_KEY, queryFn: intakeApi.templates });
  const [editing, setEditing] = useState<IntakeTemplateDetail | null>(null);
  const [archiving, setArchiving] = useState<IntakeTemplateSummary | null>(null);

  const refresh = () => queryClient.invalidateQueries({ queryKey: TEMPLATES_KEY });

  const adapt = useMutation({
    mutationFn: (id: string) => intakeApi.adapt(id),
    onSuccess: (created) => {
      void refresh();
      queryClient.setQueryData(["intake-template", created.id], created);
      toast.success(t("intake.settings.adapted"));
      setEditing(created);
    },
    onError: (e) => toast.error(errorCode(e) === "name_taken" ? t("intake.settings.nameTaken") : t("intake.settings.adaptFailed")),
  });

  const edit = useMutation({
    mutationFn: (id: string) =>
      queryClient.fetchQuery({ queryKey: ["intake-template", id], queryFn: () => intakeApi.template(id) }),
    onSuccess: setEditing,
    onError: () => toast.error(t("intake.settings.loadFailed")),
  });

  const setDefault = useMutation({
    mutationFn: (template: IntakeTemplateSummary) => intakeApi.setDefault(template.isDefault ? null : template.id),
    onSuccess: (defaultTemplateId) => {
      queryClient.setQueryData(TEMPLATES_KEY, (old: typeof query.data) => (old ? { ...old, defaultTemplateId } : old));
      toast.success(t("intake.settings.defaultSaved"));
    },
    onError: () => {
      toast.error(t("intake.settings.defaultFailed"));
      void refresh();
    },
  });

  const archive = useMutation({
    mutationFn: (id: string) => intakeApi.archiveTemplate(id),
    onSuccess: () => {
      setArchiving(null);
      void refresh();
      toast.success(t("intake.settings.archived"));
    },
    onError: () => {
      setArchiving(null);
      toast.error(t("intake.settings.archiveFailed"));
    },
  });

  const templates = (query.data?.templates ?? []).filter((x) => !x.archived);
  const system = templates.filter((x) => x.isDefault);
  const own = templates.filter((x) => !x.isDefault);
  const busy = adapt.isPending || edit.isPending;

  const row = (x: IntakeTemplateSummary) => {
    const isPreferred = query.data?.defaultTemplateId === x.id;
    return (
      <TemplateRow key={x.id} template={x}>
        {isPreferred ? (
          <Badge label={t("intake.settings.default")} tone="mint" />
        ) : (
          <button type="button" disabled={setDefault.isPending} onClick={() => setDefault.mutate(x)} className={linkButton}>
            {t("intake.settings.useAsDefault")}
          </button>
        )}
        {!x.isDefault && (
          <button type="button" disabled={busy} onClick={() => edit.mutate(x.id)} className={linkButton}>
            <Pencil size={14} aria-hidden />
            {t("intake.settings.edit")}
          </button>
        )}
        <button type="button" disabled={busy} onClick={() => adapt.mutate(x.id)} className={linkButton}>
          {adapt.isPending && adapt.variables === x.id ? <Loader2 size={14} className="animate-spin" aria-hidden /> : <Copy size={14} aria-hidden />}
          {t("intake.settings.adapt")}
        </button>
        {!x.isDefault && (
          <button type="button" onClick={() => setArchiving(x)} className={`${linkButton} text-onSurfaceVariant`}>
            <Archive size={14} aria-hidden />
            {t("intake.settings.archive")}
          </button>
        )}
      </TemplateRow>
    );
  };

  return (
    <section className="bg-surfaceContainerLowest border border-outlineVariant rounded-radii-md p-6 flex flex-col gap-4">
      <div>
        <h2 className="text-title-md text-onSurface">{t("intake.settings.title")}</h2>
        <p className="text-body-md text-onSurfaceVariant">{t("intake.settings.description")}</p>
      </div>
      {query.isError ? (
        <p className="text-body-md text-error">{t("intake.settings.loadFailed")}</p>
      ) : query.isPending ? null : (
        <>
          <div className="flex flex-col gap-2">
            <h3 className="text-label-lg text-onSurfaceVariant">{t("intake.settings.systemTemplate")}</h3>
            {system.map(row)}
          </div>
          <div className="flex flex-col gap-2">
            <h3 className="text-label-lg text-onSurfaceVariant">{t("intake.settings.ownTemplates")}</h3>
            {own.length === 0 ? <p className="text-body-md text-onSurfaceVariant">{t("intake.settings.noOwn")}</p> : own.map(row)}
          </div>
        </>
      )}

      {editing && <IntakeTemplateEditor key={editing.id} template={editing} onClose={() => setEditing(null)} />}
      <ConfirmDialog
        open={archiving !== null}
        onOpenChange={(open) => !open && setArchiving(null)}
        title={t("intake.settings.archiveTitle")}
        description={t("intake.settings.archiveBody", { name: archiving?.name ?? "" })}
        confirmLabel={t("intake.settings.archive")}
        pending={archive.isPending}
        onConfirm={() => archiving && archive.mutate(archiving.id)}
      />
    </section>
  );
}

const linkButton =
  "inline-flex items-center gap-1 rounded-radii-md px-2 py-1 text-label-md text-primary hover:bg-pastelLavender disabled:opacity-60";

// One template: name and version, its actions, and the questions on demand (read-only).
function TemplateRow({ template, children }: { template: IntakeTemplateSummary; children: ReactNode }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const detail = useQuery({
    queryKey: ["intake-template", template.id],
    queryFn: () => intakeApi.template(template.id),
    enabled: open,
  });

  return (
    <div className="flex flex-col gap-3 rounded-radii-lg border border-outlineVariant p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="min-w-0">
          <p className="break-words text-label-lg text-onSurface">{template.name}</p>
          <p className="text-body-sm text-onSurfaceVariant">{t("intake.settings.version", { version: template.version })}</p>
        </div>
        <div className="flex flex-wrap items-center gap-1">
          <button type="button" onClick={() => setOpen((v) => !v)} aria-expanded={open} className={linkButton}>
            {open ? <EyeOff size={14} aria-hidden /> : <Eye size={14} aria-hidden />}
            {open ? t("intake.settings.hide") : t("intake.settings.view")}
          </button>
          {children}
        </div>
      </div>
      {open &&
        (detail.isError ? (
          <p className="text-body-md text-error">{t("intake.settings.loadFailed")}</p>
        ) : !detail.data ? null : (
          <div className="flex flex-col gap-4">
            {detail.data.sections.map((s) => (
              <div key={s.key} className="flex flex-col gap-1">
                <h4 className="text-label-lg text-primary">{s.title}</h4>
                <ol className="flex flex-col gap-1">
                  {s.questions.map((q) => (
                    <li key={q.id} className={`flex flex-wrap items-center gap-2 text-body-md ${q.disabled ? "text-onSurfaceVariant line-through" : "text-onSurface"}`}>
                      <span>{q.label}</span>
                      <span className="text-body-sm text-onSurfaceVariant">({t(`intake.types.${q.answerType}`)})</span>
                      {q.required && <Badge label={t("intake.settings.requiredBadge")} tone="pink" />}
                      {q.disabled && <Badge label={t("intake.settings.disabledBadge")} />}
                      {q.targetField && (
                        <Badge label={t("intake.settings.destinationBadge", { target: t(targetLabelKey(q.targetField)) })} tone="blue" />
                      )}
                    </li>
                  ))}
                </ol>
              </div>
            ))}
          </div>
        ))}
    </div>
  );
}
