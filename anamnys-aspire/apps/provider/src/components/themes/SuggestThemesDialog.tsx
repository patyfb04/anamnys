import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Loader2 } from "lucide-react";
import { themesApi } from "@anamnys/shared/api/themes";
import Modal from "@/components/patients/Modal";

interface Props {
  noteId: string;
  onClose: () => void;
}

// Lists the model's suggestions; nothing is saved until the provider ticks terms and confirms.
export default function SuggestThemesDialog({ noteId, onClose }: Props) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [selected, setSelected] = useState<Set<string>>(new Set());

  const query = useQuery({
    queryKey: ["note-themes", noteId, "suggest"],
    // Closing the dialog unmounts the query, and react-query aborts the signal: the server stops the run.
    queryFn: ({ signal }) => themesApi.suggest(noteId, signal),
    gcTime: 0,
    staleTime: 0,
    retry: false,
    refetchOnWindowFocus: false,
  });

  // The first analysis after a server start may wait for the model to load.
  const [slow, setSlow] = useState(false);
  useEffect(() => {
    if (!query.isPending) return;
    const timer = setTimeout(() => setSlow(true), 8000);
    return () => clearTimeout(timer);
  }, [query.isPending]);

  const accept = useMutation({
    mutationFn: async () => {
      const results = await Promise.allSettled([...selected].map((id) => themesApi.add(noteId, id, true)));
      // A term tagged meanwhile (already_tagged) is already what the provider wants.
      const failed = results.filter(
        (r) => r.status === "rejected" && (r.reason as { code?: string } | undefined)?.code !== "already_tagged",
      );
      if (failed.length > 0) throw new Error("add_failed");
    },
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: ["note-themes", noteId], exact: true });
      void queryClient.invalidateQueries({ queryKey: ["notes"] });
    },
    onSuccess: onClose,
  });

  const toggle = (id: string) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const terms = query.data ?? [];
  return (
    <Modal
      title={t("themes.suggest.title")}
      onClose={onClose}
      footer={
        <>
          <button
            type="button"
            onClick={onClose}
            className="rounded-radii-md px-4 py-2 text-label-lg text-primary hover:underline"
          >
            {t("common.cancel")}
          </button>
          <button
            type="button"
            disabled={selected.size === 0 || accept.isPending}
            onClick={() => accept.mutate()}
            className="rounded-radii-lg bg-primary px-4 py-2 text-label-lg text-onPrimary disabled:opacity-60"
          >
            {t("themes.suggest.accept", { count: selected.size })}
          </button>
        </>
      }
    >
      <div className="flex flex-col gap-3 p-5">
        {query.isPending && (
          <div className="flex flex-col gap-1">
            <p className="flex items-center gap-2 text-body-md text-onSurfaceVariant">
              <Loader2 size={18} className="animate-spin text-primary" aria-hidden />
              {t("themes.suggest.loading")}
            </p>
            {slow && <p className="text-body-sm text-onSurfaceVariant">{t("themes.suggest.loadingSlow")}</p>}
          </div>
        )}
        {query.isError && (
          <p role="alert" className="text-body-md text-error">
            {t("themes.errors.suggest_failed")}
          </p>
        )}
        {query.isSuccess && terms.length === 0 && (
          <p className="text-body-md text-onSurfaceVariant">{t("themes.suggest.none")}</p>
        )}
        {query.isSuccess && terms.length > 0 && (
          <>
            <p className="text-body-md text-onSurfaceVariant">{t("themes.suggest.hint")}</p>
            <ul className="flex flex-col gap-1">
              {terms.map((term) => (
                <li key={term.id}>
                  <label className="flex cursor-pointer items-center gap-3 rounded-radii-md px-3 py-2 text-body-md text-onSurface hover:bg-pastelLavender">
                    <input
                      type="checkbox"
                      checked={selected.has(term.id)}
                      onChange={() => toggle(term.id)}
                      className="h-4 w-4 accent-primary"
                    />
                    {term.label}
                  </label>
                </li>
              ))}
            </ul>
          </>
        )}
        {accept.isError && (
          <p role="alert" className="text-body-sm text-error">
            {t("themes.errors.generic")}
          </p>
        )}
      </div>
    </Modal>
  );
}
