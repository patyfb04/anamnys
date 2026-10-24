import { useEffect, useState } from "react";
import { createFileRoute, Link } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import { hasAuthError } from "@anamnys/shared/lib/authError";
import AuthErrorNotice from "@anamnys/shared/ui/AuthErrorNotice";
import { adminThemesApi, type AdminThemeTerm } from "@anamnys/shared/api/adminThemes";
import AdminTopBar from "@/components/AdminTopBar";
import AdminLanding from "@/components/AdminLanding";
import PendingApproval from "@/components/PendingApproval";
import VocabularyTree from "@/components/vocabulary/VocabularyTree";
import {
  AddTermDialog,
  MergeTermsDialog,
  RenameTermDialog,
  RetireTermDialog,
  SplitTermDialog,
} from "@/components/vocabulary/TermDialogs";
import { VOCABULARY_KEY } from "@/components/vocabulary/vocabularyKey";
import { EDITOR_ROLES, STAFF_ROLES } from "@/staffRoles";

export const Route = createFileRoute("/vocabulary")({
  component: VocabularyPage,
});

type DialogState =
  | { kind: "add"; parentId?: string }
  | { kind: "rename"; term: AdminThemeTerm }
  | { kind: "retire"; term: AdminThemeTerm }
  | { kind: "split"; term: AdminThemeTerm }
  | { kind: "merge" };

function VocabularyPage() {
  const { user, isLoading, loadUser } = useAuthStore();
  const authError = hasAuthError();

  useEffect(() => {
    void loadUser("owner");
  }, [loadUser]);

  if (authError) return <AuthErrorNotice />;
  if (isLoading) return null;

  const isStaff = !!user && user.roles.some((r) => STAFF_ROLES.includes(r));
  const canEdit = !!user && user.roles.some((r) => EDITOR_ROLES.includes(r));

  return (
    <div className="min-h-screen flex flex-col bg-surfaceContainerLow">
      <AdminTopBar user={user} />
      {!user ? (
        <main className="flex-1 flex items-center justify-center p-4">
          <AdminLanding />
        </main>
      ) : !isStaff ? (
        <main className="flex-1 flex items-center justify-center p-4">
          <PendingApproval email={user.email} />
        </main>
      ) : (
        <main className="flex-1 p-4 md:p-8">
          <Vocabulary canEdit={canEdit} />
        </main>
      )}
    </div>
  );
}

function Vocabulary({ canEdit }: { canEdit: boolean }) {
  const { t } = useTranslation();
  const [dialog, setDialog] = useState<DialogState | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const query = useQuery({ queryKey: VOCABULARY_KEY, queryFn: adminThemesApi.list });
  const terms = query.data ?? [];
  const close = () => {
    setDialog(null);
    setSelected(new Set());
  };
  const toggle = (id: string) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  const sources = terms.filter((x) => selected.has(x.id));

  return (
    <div className="flex flex-col gap-4">
      <Link to="/" className="self-start text-label-lg text-primary hover:underline">
        {t("admin.vocabulary.back")}
      </Link>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-headline-lg text-onSurface">{t("admin.vocabulary.title")}</h1>
        {canEdit && (
          <div className="flex gap-3">
            <button
              type="button"
              disabled={selected.size < 2}
              onClick={() => setDialog({ kind: "merge" })}
              className="rounded-radii-lg border border-primary px-4 py-2 text-label-lg text-primary hover:bg-pastelLavender disabled:opacity-50 disabled:hover:bg-transparent"
            >
              {t("admin.vocabulary.merge.open", { count: selected.size })}
            </button>
            <button
              type="button"
              onClick={() => setDialog({ kind: "add" })}
              className="rounded-radii-lg bg-primary px-4 py-2 text-label-lg text-onPrimary hover:opacity-90"
            >
              {t("admin.vocabulary.add.open")}
            </button>
          </div>
        )}
      </div>
      <p className="text-body-md text-onSurfaceVariant">{t("admin.vocabulary.help")}</p>
      {!canEdit && <p className="text-body-md text-onSurfaceVariant">{t("admin.vocabulary.readOnly")}</p>}

      {query.isPending && <p className="text-body-md text-onSurfaceVariant">{t("admin.vocabulary.loading")}</p>}
      {query.isError && (
        <p role="alert" className="text-body-md text-error">
          {t("admin.vocabulary.errors.load")}
        </p>
      )}
      {query.isSuccess && (
        <VocabularyTree
          terms={terms}
          canEdit={canEdit}
          selected={selected}
          onToggle={toggle}
          onAddChild={(term) => setDialog({ kind: "add", parentId: term.id })}
          onRename={(term) => setDialog({ kind: "rename", term })}
          onRetire={(term) => setDialog({ kind: "retire", term })}
          onSplit={(term) => setDialog({ kind: "split", term })}
        />
      )}

      {dialog?.kind === "add" && <AddTermDialog terms={terms} parentId={dialog.parentId} onClose={close} />}
      {dialog?.kind === "rename" && <RenameTermDialog term={dialog.term} onClose={close} />}
      {dialog?.kind === "retire" && <RetireTermDialog term={dialog.term} onClose={close} />}
      {dialog?.kind === "split" && <SplitTermDialog term={dialog.term} onClose={close} />}
      {dialog?.kind === "merge" && sources.length >= 2 && (
        <MergeTermsDialog terms={terms} sources={sources} onClose={close} />
      )}
    </div>
  );
}
