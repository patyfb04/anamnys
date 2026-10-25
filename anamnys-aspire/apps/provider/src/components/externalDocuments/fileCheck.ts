import type { ExternalDocumentItem, ExternalDocumentKind } from "@anamnys/shared/lib/types";

export const KINDS: ExternalDocumentKind[] = ["laudo", "receita", "exame", "encaminhamento", "relatorio_externo", "outro"];
export const MAX_BYTES = 20 * 1024 * 1024;
export const ACCEPT = ".pdf,.jpg,.jpeg,.png,application/pdf,image/jpeg,image/png";

const EXTENSIONS = ["pdf", "jpg", "jpeg", "png"];
const MIME_TYPES = ["application/pdf", "image/jpeg", "image/png"];

// Same limits the server enforces (it also checks the magic bytes); this only spares a useless upload.
// Returns a pt.json error code, or null when the file looks acceptable.
export function checkFile(file: File): "empty" | "unsupported_type" | "too_large" | null {
  const ext = file.name.includes(".") ? file.name.split(".").pop()!.toLowerCase() : "";
  if (!EXTENSIONS.includes(ext) || (file.type !== "" && !MIME_TYPES.includes(file.type))) return "unsupported_type";
  if (file.size === 0) return "empty";
  if (file.size > MAX_BYTES) return "too_large";
  return null;
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function hasPending(items: ExternalDocumentItem[] | undefined): boolean {
  return (items ?? []).some((d) => d.scanStatus === "pending");
}
