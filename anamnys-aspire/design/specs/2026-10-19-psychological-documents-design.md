# Psychological Documents and the CFP 006/2019 Validator (F-05, delivery 1)

**Date:** 2026-10-19
**Status:** Implemented (delivery 1); delivery 2 (laudo, parecer, relatório multiprofissional) implemented, see `design/specs/2026-10-20-psychological-documents-delivery-2-design.md`
**Scope:** Backlog F-05 with flow 05. The provider writes a psychological document for a patient
through a form built from the document kind's required elements (Res. CFP 006/2019), sees a live
conformity check, confirms the kind's prohibitions, signs it (blocked when elements are missing,
unless she chose advisory mode), exports it as a PDF, and corrects a signed document through a new
linked version. Delivery 1 covers **declaração, atestado and relatório**.

**Delivery 2 is required, not optional:** laudo, parecer and relatório multiprofissional must be
built to close F-05 — they are the documents of psychological assessment and of shared work, the
rulesets for all six kinds are seeded in delivery 1, and the backlog item is only done when every
kind of art. 8 can be written and checked. Delivery 2 adds their forms, PDF layouts and the
co-authorship model of the relatório multiprofissional (art. 12). It is tracked in the status file
("Next") and in Notion.

Delivery 2 notes:

- The catalog already carries a confirmation text for the parecer prohibition
  `resultado_de_avaliacao_ou_intervencao_psicologica`.
- `DocumentCatalog.Prohibition(code)` is kind-agnostic: shared prohibition codes reused by laudo and
  relatório multiprofissional need kind-aware confirmation texts (today they read "do relatório").
- `nome_autor_inscricao_crp_e_titulacao`, `nomes_profissionais` and `inscricoes_profissionais` are
  Provider placeholders; their CRP handling must be designed together with co-authorship.

Out of scope: delivery protocol signed by the requester (art. 16 §1, belongs to F-07), retention
date (`RetentionUntil`, F-06), ICP-Brasil signature, sharing (F-17/F-18), sealing into
`DocumentHashes` (F-45).

---

## 1. Context

- Tables exist with no code: `ClinicalDocuments` (`PatientId`, `ProviderId`, `NoteId`, `Kind`,
  `Body`, `SignedAt`, `RetentionUntil`, `SupersededBy`, `CreatedAt`; kinds `declaracao`,
  `atestado`, `relatorio`, `relatorio_multiprofissional`, `laudo`, `parecer`),
  `ConformityRulesets` (`DocumentKind`, `Version`, `Elements` jsonb array, `Prohibitions` jsonb
  array, `SourceRef`, `ArticleRef`, `EffectiveFrom`), `ConformityChecks` (`DocumentId`,
  `RulesetId`, `MissingElements`, `ForbiddenFound`, `Mode` blocking/advisory, `Passed`,
  `CheckedAt`). `AuditEntries` accepts a `DocumentId`.
- The deleted backend's `backend/db/02_seed_reference.sql` (commit `250b0442…`) holds the six
  rulesets `cfp-006-2019.1`, derived article by article, with comments on what was left out on
  purpose (e.g. references are mandatory only for laudo and parecer; optional clauses never become
  requirements).
- The note PDF export already provides QuestPDF rendering, the SeaweedFS store and one-hour
  presigned links.

## 2. Decisions

| Topic | Decision |
|---|---|
| Editing model | A form per kind: every element the provider must write is its own field. The validator checks field by field; it never interprets free text. |
| Prohibitions | Explicit confirmations by the provider (text stored with the document), required to sign in both modes. Prohibitions the system can guarantee by construction are not asked (see §3). |
| Mode | `blocking` by default (cannot sign while elements are missing); `advisory` selectable in Ajustes (signing with missing elements needs an extra confirmation). The mode in force is stored in the check. |
| Storage | `ClinicalDocuments.Fields jsonb` keyed by element code; `Body` holds the assembled text used by the PDF: declaração = one paragraph per labelled provider element (typed line breaks kept), atestado = one continuous paragraph (line breaks become spaces, art. 10 §5º), `prazo_validade` last. |
| Corrections | "Corrigir" creates a new draft copied from the signed document and sets the original's `SupersededBy`; the original stays unchanged. |
| Scope | Delivery 1: declaração, atestado, relatório. All six rulesets seeded. Delivery 2 required (laudo, parecer, relatório multiprofissional). |

## 3. Element origins

Every ruleset element has one origin. Only `provider` elements become form fields.

| Element | Origin | Source |
|---|---|---|
| `titulo_*` (declaração, atestado psicológico, relatório psicológico) | `pdf` | Kind title printed by the PDF. |
| `nome_pessoa_atendida`, `nome_pessoa_ou_instituicao_atendida` | `system` | Patient full name. |
| `identificacao` (relatório) | `system` | The identification block (patient, author, CRP) the PDF prints. |
| `nome_psicologo`, `inscricao_crp`, `nome_autor_e_inscricao_crp` | `system` | Provider name and CRP; **missing when the profile has no CRP**. |
| `local_emissao` | `system` | Provider's new profile field "Cidade de atendimento"; **missing when empty**. |
| `data_emissao` | `system` | Signing date. |
| `carimbo` | `pdf` | Name + CRP block next to the electronic signature. |
| `assinatura` | `pdf` | Electronic signature block on the last page. |
| `laudas_numeradas_rubricadas` | `pdf` | Every page footer: "Página X de Y" + name and CRP (in place of the initials). |
| `prazo_validade` | `provider` | Always the last paragraph (art. 17). |
| Declaração: `finalidade`, `local_dias_horarios_e_duracao_do_acompanhamento` | `provider` | |
| Atestado: `nome_solicitante`, `finalidade`, `descricao_condicoes_psicologicas` | `provider` | |
| Relatório: `nome_solicitante`, `finalidade`, `descricao_demanda`, `procedimento`, `referencial_teorico`, `pessoas_ouvidas_numero_de_encontros_e_duracao`, `analise`, `conclusao` | `provider` | |

Prohibitions:

| Kind | Prohibition | Handling |
|---|---|---|
| Declaração | `sintomas_situacoes_ou_estados_psicologicos` | Confirmation: "Confirmo que esta declaração não descreve sintomas, situações ou estados psicológicos." |
| Atestado | `quebra_de_paragrafo_sem_preenchimento` | By construction: the atestado text is printed as continuous text (line breaks removed). |
| Atestado | `informacao_alem_do_solicitado` | Confirmation: "Confirmo que o atestado se restringe à informação solicitada." |
| Relatório | `afirmacao_sem_fonte_ou_sustentacao` | Confirmation: "Confirmo que toda afirmação do relatório tem fonte ou sustentação técnica." |
| Relatório | `descricao_literal_das_sessoes` | Confirmation: "Confirmo que o relatório não transcreve literalmente as sessões." |

A provider element is present when its trimmed text is not empty.

## 4. Data

Migration `design/migrations/2026-10-19-documents.sql` (idempotent, all-or-nothing):

- Seed the six rulesets from the old file verbatim (`ON CONFLICT ("DocumentKind","Version") DO
  NOTHING`).
- `ClinicalDocuments` gains `"Fields" jsonb DEFAULT '{}' NOT NULL`, `"Confirmations" jsonb DEFAULT
  '[]' NOT NULL`, `"RulesetId" uuid` (FK), `"SignedByCrp" text`, `"PdfObjectKey"`, `"PdfSha256"`,
  `"ExportedAt"`, `"UpdatedAt" timestamptz`; constraints: signed ⇔ `SignedByCrp` set; PDF columns
  all-or-nothing and only on a signed document.
- `Providers` gains `"PracticeCity" text` and `"DocumentCheckMode" text DEFAULT 'blocking' NOT
  NULL` (`blocking` | `advisory`).
- `anamnys-db-script.sql` gets the same.

## 5. Server

- `DocumentCatalog` — element origin map, pt-BR labels and hints (with article), prohibition texts,
  the kinds enabled in delivery 1.
- `DocumentConformity.Check(kind, ruleset, fields, confirmations, provider, patient)` → missing
  elements (provider and system), missing confirmations. Pure function.
- `ClinicalDocumentService` — create draft (patient of provider, not archived), save draft
  (locked once signed), sign (re-check; blocking mode refuses missing elements; confirmations
  always required; CRP and city required; writes `ConformityChecks` with mode, `SignedAt`,
  `SignedByCrp`, assembled `Body`), correct (new draft + `SupersededBy`), list, get. Every mutation
  takes a row lock and writes `AuditEntries` (`DocumentId`) in the same transaction.
- `DocumentPdf` + export reusing `INoteFileStore` (object key `documents/{providerId}/{id}.pdf`,
  file name `documento-{CODE}.pdf`, one-hour link, audited).
- Routes under `/api/phi`: `GET providers/me/document-kinds`; `GET|POST
  providers/me/patients/{id}/documents`; `GET|PUT providers/me/documents/{id}`;
  `POST providers/me/documents/{id}/sign`, `/correct`, `/export`. Errors `409 { code, message }`:
  `archived`, `locked`, `missing_elements` (with the list), `confirmations_required`,
  `kind_not_available`; `422` `missing_crp`, `missing_city`.
- Profile endpoint (`providers/me/profile`) accepts and returns `practiceCity`; the mode has its
  own `GET|PUT providers/me/document-settings` (`{ documentCheckMode }`), like the booking policy
  behind Ajustes.

## 6. Provider app

- Patient record: card "Documentos" (kind, status rascunho/assinado/substituído, dates) and "Novo
  documento" (kind picker: the three delivery-1 kinds).
- Editor `/documents/$documentId`: system values read-only; provider fields with hint and article;
  "Prazo de validade" last; panel "Conferência" with ✓/✗ per element (refreshed on save), the
  confirmations, and "Assinar" (blocking: disabled until complete; advisory: extra confirmation when
  incomplete). Signed: read-only, "Exportar PDF"/"Baixar PDF", "Corrigir"; superseded documents
  link to their replacement. A "?" help panel summarises the rules in plain language.
- Ajustes: "Conferência de documentos" (bloquear / apenas avisar). Perfil: "Cidade de atendimento".

## 7. Documentation

- `documentation/22-psychological-documents.md`: kinds, element origins, prohibitions, modes,
  correction, what was left out on purpose (from the old seed comments), delivery 2.
- Gaps file; status file ("Next": F-05 delivery 2 right after this one); Notion F-05 annotation.

## 8. Testing

- Conformity (pure): for each delivery-1 kind, each provider element missing is reported; system
  elements reported missing only when CRP or city is absent; confirmations required; atestado text
  continuous.
- Service (real DB): create/save/sign in blocking and advisory modes; signed is locked; correction
  chain keeps the original; provider scoping; archived patient; audit rows; `ConformityChecks` row
  with mode and missing list.
- Export: generated once, presigned, PDF bytes start with `%PDF`.
- Migration: six rulesets seeded; idempotent.
- Endpoint: 401 without session; manual browser check.
