import { useEffect, useMemo, useState } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { patientsApi } from "@anamnys/shared/api/patients";
import type {
  PatientFilters,
  PatientSearchRequest,
  PatientSortBy,
  SortDir,
} from "@anamnys/shared/lib/types";

const PAGE_SIZE = 25;
const SEARCH_DEBOUNCE_MS = 300;

// Search, filters, sort and page for the patient list. Held in component state on
// purpose, never in the URL: name and email are PHI (no PHI in URLs).
export function usePatientSearch() {
  const [search, setSearchText] = useState("");
  const [debouncedSearch, setDebouncedSearch] = useState("");
  const [filters, setFiltersState] = useState<PatientFilters>({});
  const [sortBy, setSortBy] = useState<PatientSortBy>("nextVisit");
  const [sortDir, setSortDir] = useState<SortDir>("asc");
  const [page, setPage] = useState(1);

  useEffect(() => {
    const timer = setTimeout(() => {
      setDebouncedSearch(search.trim());
      setPage(1);
    }, SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [search]);

  const setFilters = (next: PatientFilters) => {
    setFiltersState(next);
    setPage(1);
  };

  const setSort = (nextSortBy: PatientSortBy, nextSortDir: SortDir) => {
    setSortBy(nextSortBy);
    setSortDir(nextSortDir);
    setPage(1);
  };

  // Clicking the active column flips its direction; another column starts ascending.
  const toggleSort = (column: PatientSortBy) => {
    if (column === sortBy) setSort(column, sortDir === "asc" ? "desc" : "asc");
    else setSort(column, "asc");
  };

  const request = useMemo<PatientSearchRequest>(
    () => ({
      ...compactFilters(filters),
      search: debouncedSearch || undefined,
      sortBy,
      sortDir,
      page,
      pageSize: PAGE_SIZE,
    }),
    [filters, debouncedSearch, sortBy, sortDir, page],
  );

  const query = useQuery({
    queryKey: ["patients", "search", request],
    queryFn: () => patientsApi.search(request),
    placeholderData: keepPreviousData,
  });

  return {
    search,
    setSearch: setSearchText,
    filters,
    setFilters,
    activeFilterCount: countActiveFilters(filters),
    hasCriteria: debouncedSearch !== "" || countActiveFilters(filters) > 0,
    sortBy,
    sortDir,
    setSort,
    toggleSort,
    page,
    setPage,
    query,
  };
}

function compactFilters(filters: PatientFilters): PatientFilters {
  const blankToUndefined = (value?: string) => (value?.trim() ? value.trim() : undefined);
  return {
    name: blankToUndefined(filters.name),
    email: blankToUndefined(filters.email),
    noteStatus: filters.noteStatus?.length ? filters.noteStatus : undefined,
    lastVisitFrom: blankToUndefined(filters.lastVisitFrom),
    lastVisitTo: blankToUndefined(filters.lastVisitTo),
    nextVisitFrom: blankToUndefined(filters.nextVisitFrom),
    nextVisitTo: blankToUndefined(filters.nextVisitTo),
    archived: filters.archived || undefined,
  };
}

// Each date range counts once, whichever of its ends is set.
export function countActiveFilters(filters: PatientFilters): number {
  const f = compactFilters(filters);
  return [
    f.name,
    f.email,
    f.noteStatus,
    f.lastVisitFrom || f.lastVisitTo,
    f.nextVisitFrom || f.nextVisitTo,
    f.archived,
  ].filter(Boolean).length;
}
