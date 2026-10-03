import { useCallback, useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { Filter, Search, UserPlus } from "lucide-react";
import TextField from "@anamnys/shared/ui/TextField";
import { usePatientSearch } from "@/hooks/usePatientSearch";
import PatientsTable from "@/components/patients/PatientsTable";
import PatientCards from "@/components/patients/PatientCards";
import PatientSortSelect from "@/components/patients/PatientSortSelect";
import PatientFilterPanel from "@/components/patients/PatientFilterPanel";
import PaginationFooter from "@/components/patients/PaginationFooter";
import PatientFormModal from "@/components/patients/PatientFormModal";

export const Route = createFileRoute("/_app/patients/")({
  component: PatientListPage,
});

// Provider's patient list: server-side search, filters, sortable columns and paging.
// See design/specs/2026-09-27-patient-list-design.md.
function PatientListPage() {
  const navigate = useNavigate();
  const { t } = useTranslation();
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [creating, setCreating] = useState(false);
  const closeFilters = useCallback(() => setFiltersOpen(false), []);
  const {
    search,
    setSearch,
    filters,
    setFilters,
    activeFilterCount,
    hasCriteria,
    sortBy,
    sortDir,
    setSort,
    toggleSort,
    page,
    setPage,
    query,
  } = usePatientSearch();

  const openPatient = (patientId: string) => navigate({ to: "/patients/$patientId", params: { patientId } });
  const clearAll = () => {
    setSearch("");
    setFilters({});
  };

  const data = query.data;

  return (
    <div className="p-4 md:p-8">
      <div className="flex flex-col gap-4 md:flex-row md:items-end md:justify-between mb-6">
        <div>
          <h1 className="text-headline-lg text-onSurface">{t("patients.list.title")}</h1>
          <p className="text-body-md text-onSurfaceVariant mt-1">{t("patients.list.subtitle")}</p>
        </div>
        <div className="flex gap-3">
          <div className="relative">
            <button
              type="button"
              onClick={() => setFiltersOpen((open) => !open)}
              aria-expanded={filtersOpen}
              className="flex items-center gap-2 px-4 py-2 border border-outlineVariant rounded-radii-md text-label-lg text-onSurface bg-surfaceContainerLowest hover:bg-surfaceContainerLow transition-colors"
            >
              <Filter size={18} />
              {t("patients.list.filter")}
              {activeFilterCount > 0 && (
                <span className="min-w-5 h-5 px-1 rounded-radii-full bg-primary text-onPrimary text-label-md flex items-center justify-center">
                  {activeFilterCount}
                </span>
              )}
            </button>
            {filtersOpen && <PatientFilterPanel filters={filters} onApply={setFilters} onClose={closeFilters} />}
          </div>
          <button
            type="button"
            onClick={() => setCreating(true)}
            className="flex items-center gap-2 px-4 py-2 bg-primary text-onPrimary rounded-radii-md text-label-lg shadow-sm hover:opacity-90 transition-opacity"
          >
            <UserPlus size={18} />
            {t("patients.list.addPatient")}
          </button>
        </div>
      </div>

      <TextField
        icon={Search}
        placeholder={t("patients.list.searchPlaceholder")}
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        containerClassName="mb-4"
        aria-label={t("patients.list.searchPlaceholder")}
      />

      <div className="md:hidden mb-3">
        <PatientSortSelect sortBy={sortBy} sortDir={sortDir} onChange={setSort} />
      </div>

      <div className="bg-surfaceContainerLowest rounded-radii-xl border border-outlineVariant overflow-hidden shadow-sm">
        {query.isPending ? (
          <LoadingRows />
        ) : query.isError ? (
          <p className="text-error text-center py-12">{t("patients.list.failedToLoad")}</p>
        ) : data && data.items.length === 0 ? (
          <EmptyState
            hasCriteria={hasCriteria}
            onClear={clearAll}
            onAdd={() => setCreating(true)}
          />
        ) : data ? (
          <div className={query.isPlaceholderData ? "opacity-60 transition-opacity" : "transition-opacity"}>
            <div className="hidden md:block overflow-x-auto">
              <PatientsTable items={data.items} sortBy={sortBy} sortDir={sortDir} onSort={toggleSort} onOpen={openPatient} />
            </div>
            <div className="md:hidden">
              <PatientCards items={data.items} onOpen={openPatient} />
            </div>
            <PaginationFooter page={page} pageSize={data.pageSize} totalCount={data.totalCount} onPage={setPage} />
          </div>
        ) : null}
      </div>

      {creating && (
        <PatientFormModal
          mode={{ kind: "create" }}
          onClose={() => setCreating(false)}
          onSaved={(patientId, inviteError) => {
            setCreating(false);
            navigate({ to: "/patients/$patientId", params: { patientId }, state: { inviteError } });
          }}
        />
      )}
    </div>
  );
}

function LoadingRows() {
  return (
    <div className="divide-y divide-outlineVariant" aria-busy="true">
      {Array.from({ length: 5 }, (_, i) => (
        <div key={i} className="flex items-center gap-3 px-4 py-4 animate-pulse">
          <div className="w-10 h-10 rounded-radii-full bg-surfaceContainerHigh" />
          <div className="flex-1 flex flex-col gap-2">
            <div className="h-3 w-40 rounded-radii-sm bg-surfaceContainerHigh" />
            <div className="h-3 w-24 rounded-radii-sm bg-surfaceContainer" />
          </div>
        </div>
      ))}
    </div>
  );
}

function EmptyState({ hasCriteria, onClear, onAdd }: { hasCriteria: boolean; onClear: () => void; onAdd: () => void }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col items-center text-center gap-3 px-4 py-12">
      <p className="text-headline-sm text-onSurface">
        {hasCriteria ? t("patients.list.noResults") : t("patients.list.emptyTitle")}
      </p>
      {!hasCriteria && <p className="text-body-md text-onSurfaceVariant">{t("patients.list.emptyBody")}</p>}
      <button
        type="button"
        onClick={hasCriteria ? onClear : onAdd}
        className="text-label-lg text-primary hover:underline"
      >
        {hasCriteria ? t("patients.list.clearFilters") : t("patients.list.addPatient")}
      </button>
    </div>
  );
}
