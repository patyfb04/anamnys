import { useEffect, useId, useRef, useState, type FocusEvent } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Search, X } from "lucide-react";
import TextField from "@anamnys/shared/ui/TextField";
import { patientsApi } from "@anamnys/shared/api/patients";

const MIN_CHARS = 2;
const SEARCH_DEBOUNCE_MS = 300;

const search = (term: string) => patientsApi.search({ search: term, sortBy: "name", sortDir: "asc", page: 1, pageSize: 8 });

// Type-ahead patient filter for the sharing overview. The typed name is PHI: it stays in component state and goes to
// the server in a POST body; only the chosen patient's id reaches the URL.
export default function PatientFilter({ patientId, onChange }: { patientId?: string; onChange: (patientId: string | null) => void }) {
  const { t } = useTranslation();
  const listId = useId();
  const [text, setText] = useState("");
  const [term, setTerm] = useState("");
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const queryClient = useQueryClient();
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const id = setTimeout(() => setTerm(text.trim()), SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(id);
  }, [text]);

  // After a reload only the id is known; fetch the name to show in the field.
  const chosen = useQuery({
    queryKey: ["patient", patientId],
    queryFn: () => patientsApi.get(patientId!),
    enabled: !!patientId,
  });
  const matches = useQuery({
    queryKey: ["sharing-patient-search", term],
    queryFn: () => search(term),
    enabled: !patientId && term.length >= MIN_CHARS,
  });

  const clear = () => {
    setText("");
    setTerm("");
    onChange(null);
  };
  const pick = (id: string) => {
    setText("");
    setOpen(false);
    onChange(id);
  };

  // With a patient chosen the field shows their name; editing it (Delete, Backspace, typing) drops the filter and goes
  // on as a new search from the edited text. One element in both states, so the field keeps focus across the switch.
  const name = patientId && chosen.data ? `${chosen.data.firstName} ${chosen.data.lastName}` : "";

  // Tabbing from the field into the suggestions keeps them open; focus leaving the whole widget closes them.
  const leave = (e: FocusEvent<HTMLElement>) => {
    if (!box.current?.contains(e.relatedTarget as Node | null)) setOpen(false);
  };
  const items = matches.data?.items ?? [];
  const showList = open && term.length >= MIN_CHARS && matches.isSuccess;
  // Enter: the highlighted suggestion (the first by default). Skips the debounce, so a fast typist's
  // Enter searches what is in the field, not what was there 300 ms ago.
  const submit = async () => {
    const now = text.trim();
    if (now.length < MIN_CHARS) return;
    setTerm(now);
    setOpen(true);
    const found = await queryClient.fetchQuery({ queryKey: ["sharing-patient-search", now], queryFn: () => search(now) });
    const target = found.items[now === term ? active : 0];
    if (target) pick(target.id);
  };
  return (
    <div ref={box} className="relative flex gap-2">
      <TextField
        containerClassName="min-w-0 flex-1"
        icon={Search}
        value={patientId ? name : text}
        placeholder={t("sharing.overview.patientSearch")}
        aria-label={t("sharing.overview.patientSearch")}
        role="combobox"
        aria-expanded={showList}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={showList && items[active] ? `${listId}-${active}` : undefined}
        onChange={(e) => {
          setText(e.target.value);
          setActive(0);
          setOpen(true);
          if (patientId) onChange(null);
        }}
        rightElement={
          (patientId || text) && (
            <button type="button" onClick={clear} aria-label={t("sharing.overview.clearPatient")} className="text-outline hover:text-onSurface">
              <X size={20} />
            </button>
          )
        }
        onFocus={() => setOpen(true)}
        onBlur={leave}
        onKeyDown={(e) => {
          if (e.key === "Escape") setOpen(false);
          else if (e.key === "Enter") {
            e.preventDefault();
            void submit();
          } else if (showList && items.length > 0 && (e.key === "ArrowDown" || e.key === "ArrowUp")) {
            e.preventDefault();
            setActive((i) => (i + (e.key === "ArrowDown" ? 1 : items.length - 1)) % items.length);
          }
        }}
      />
      {showList && (
        <ul
          id={listId}
          role="listbox"
          onBlur={leave}
          className="absolute left-0 right-0 top-full z-20 mt-1 max-h-64 overflow-y-auto rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest shadow-md"
        >
          {items.length === 0 && <li className="px-4 py-2 text-body-md text-onSurfaceVariant">{t("sharing.overview.patientNone")}</li>}
          {items.map((p, i) => (
            <li key={p.id} id={`${listId}-${i}`} role="option" aria-selected={i === active} className={i === active ? "bg-pastelLavender" : undefined}>
              {/* No focus change on mousedown, so the input's blur doesn't close the list before the click lands. */}
              <button
                type="button"
                onMouseDown={(e) => e.preventDefault()}
                onClick={() => pick(p.id)}
                className="w-full px-4 py-2 text-left text-body-md text-onSurface hover:bg-pastelLavender"
              >
                {p.firstName} {p.lastName}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
