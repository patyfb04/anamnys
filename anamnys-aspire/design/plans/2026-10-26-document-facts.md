# Document Reading and Declared Facts (F-08) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Read clean external documents (text or OCR), extract declared facts with page and sentence, and let the provider confirm/correct/discard them.

**Architecture:** `DocumentReading/` (PdfPig, PDFium render, Tesseract OCR, read queue/worker) and `DocumentFacts/` (grammar, prompt, Llama extractor sharing the model gate, validators, service, endpoints); excerpt refs extended to documents; provider UI panel and record block.

**Tech Stack:** .NET 10, EF Core + Npgsql, PdfPig, a PDFium .NET binding, Tesseract .NET binding, LLamaSharp (GBNF), React 19 + TanStack, i18next.

**Spec:** `design/specs/2026-10-26-document-facts-design.md` (binding).

## Global Constraints

- Branch `feature/document-facts`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). Fixture classes only when no AppHost/Server/aspire.exe/Tests process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. Never start/stop Aspire.
- In-process processing only: no document, page text or image leaves the server; native libraries must be packaged (NuGet) and work on Windows dev and Linux containers.
- Edit/Write only; CRLF for new `.cs`; keep endings.
- Provider scoping everywhere; no PHI (page text, fact values, file names) in URLs, logs, audit values or object keys.
- The model is shared: one gate for suggestion and extraction.
- Copy in `pt.json`; `hover:underline` / `hover:bg-pastelLavender`; `npm run build` / `npm run lint` pass.

---

### Task 1: Data and reading pipeline

**Files:** migration `design/migrations/2026-10-26-document-facts.sql` + bootstrap + chains (after `2026-10-25-external-documents.sql`), entities/mapping, `anamnys-aspire.Server/DocumentReading/*`, hook in the scan worker (clean → enqueue read), tests `anamnys-aspire.Tests/DocumentReading/*`, `Data/DocumentFactsMigrationTests.cs`, `.http` not needed yet.

- [ ] Spec §2 and §3 reading bullets (PdfPig, PDFium, Tesseract with data download + SHA, page classification, limits, queue/worker, retries, startup re-enqueue). Pick maintained NuGet packages; record versions and licenses in the report.

### Task 2: Fact extraction, review and endpoints

**Files:** `anamnys-aspire.Server/DocumentFacts/*`, excerpt service/endpoint extension for `document`, external document list/search fields, `Program.cs`, `.http`, tests `anamnys-aspire.Tests/DocumentFacts/*` (+ fake extractor).

- [ ] Spec §3 facts bullets, §6 tests; real-model probe on `dev-seed/sample-documents/*.pdf` (temporary, removed) with timings and facts in the report.

### Task 3: Provider app

**Files:** `apps/provider/src/components/externalDocuments/*` (status, facts panel), patient record block, `packages/shared/src/api/*`, `types.ts`, `pt.json`.

- [ ] Spec §4.

### Task 4: Documentation

**Files:** `documentation/27-document-facts.md`, `documentation/20-environment-configuration.md`, `documentation/00-index.md`, status, gaps.
