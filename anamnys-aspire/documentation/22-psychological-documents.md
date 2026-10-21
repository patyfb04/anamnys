# Chapter 22: Psychological Documents (F-05)

How the product writes, checks, signs and exports psychological documents under Res. CFP
006/2019. Last checked against the code on 2026-10-19 (branch `feature/psychological-documents`,
delivery 1). Design: `design/specs/2026-10-19-psychological-documents-design.md`.

This chapter is the long version of the rules. The code is short; the reasoning behind what the
validator asks for, and what it deliberately does not, is what a future change has to respect.

---

## 1. Kinds and delivery status

Res. CFP 006/2019 art. 8 defines six kinds of document. All six rulesets are seeded
(`cfp-006-2019.1` in `ConformityRulesets`, migration `design/migrations/2026-10-19-documents.sql`),
but only three can be written today.

| Kind | Code | Article | Delivery |
|---|---|---|---|
| Declaração | `declaracao` | Art. 9º | 1 (available) |
| Atestado psicológico | `atestado` | Art. 10 | 1 (available) |
| Relatório psicológico | `relatorio` | Art. 11 | 1 (available) |
| Relatório multiprofissional | `relatorio_multiprofissional` | Art. 12 | 2 (**required**) |
| Laudo psicológico | `laudo` | Art. 13 | 2 (**required**) |
| Parecer psicológico | `parecer` | Art. 14 | 2 (**required**) |

`DocumentCatalog.AvailableKinds` lists the enabled kinds; asking to create any other answers `409
kind_not_available`.

**Delivery 2 is required, not optional.** F-05 is only done when every kind of art. 8 can be
written and checked. Laudo and parecer are the documents of psychological assessment (and require
references, arts. 13 §7 and 14 §6); the relatório multiprofissional is the shared-work document
and needs a co-authorship model (art. 12). Their rulesets already exist, so delivery 2 adds forms,
PDF layouts and co-authorship, not the validator. Notes for whoever builds it:

- The catalog already carries a confirmation text for the parecer prohibition
  `resultado_de_avaliacao_ou_intervencao_psicologica`.
- `DocumentCatalog.Prohibition(code)` is kind-agnostic. Shared prohibition codes reused by laudo
  and relatório multiprofissional need kind-aware confirmation texts (today they read "do
  relatório").
- `nome_autor_inscricao_crp_e_titulacao`, `nomes_profissionais` and `inscricoes_profissionais` are
  Provider placeholders in the catalog; their CRP handling must be designed together with
  co-authorship.

---

## 2. The editing model

A form per kind: every element the provider must write is its own field, stored in
`ClinicalDocuments."Fields"` (jsonb keyed by element code). The validator checks field by field
and **never interprets free text**. A provider element is present when its trimmed text is not
empty; nothing else is judged.

`Body` holds the text assembled from the fields; it is what the PDF prints for declaração and
atestado. The relatório renders its sections from the locked `Fields`, not from `Body`.

- Declaração: one paragraph per provider element, "Label: value.", separated by a blank line; line breaks the provider typed inside a value are kept (the PDF renders each paragraph separately).
- Atestado: inline labels, **one continuous paragraph, no line break** — typed line breaks become spaces (Res. CFP 006/2019, art. 10 §5º); the editor shows a hint about it (see section 4).
- Relatório: headed sections.
- `prazo_validade` is always the last paragraph (art. 17), whatever the kind.

---

## 3. Element origins

Every element of a ruleset has exactly one origin. Only `provider` elements become form fields.

| Origin | Meaning | Elements |
|---|---|---|
| `system` | Filled from data the product already has; read-only in the editor | `nome_pessoa_atendida`, `nome_pessoa_ou_instituicao_atendida` (patient full name); `identificacao` (patient, author, CRP block); `nome_psicologo`, `inscricao_crp`, `nome_autor_e_inscricao_crp` (provider name and CRP, **missing when the profile has no CRP**); `local_emissao` (profile "Cidade de atendimento", **missing when empty**); `data_emissao` (signing date) |
| `pdf` | Produced by the PDF layout, nothing to write | `titulo_*`; `carimbo` (name and CRP next to the signature); `assinatura`; `laudas_numeradas_rubricadas` (footer, section 7) |
| `provider` | The provider writes it | Declaração: `finalidade`, `local_dias_horarios_e_duracao_do_acompanhamento`. Atestado: `nome_solicitante`, `finalidade`, `descricao_condicoes_psicologicas`. Relatório: `nome_solicitante`, `finalidade`, `descricao_demanda`, `procedimento`, `referencial_teorico`, `pessoas_ouvidas_numero_de_encontros_e_duracao`, `analise`, `conclusao`. All kinds: `prazo_validade` |

Hints shown under each field cite only the article that the seeded ruleset itself cites: the
kind's article (Art. 9º, 10 or 11), Art. 17 for `prazo_validade` and Art. 5º §8º for
`laudas_numeradas_rubricadas`. A hint never invents a reference. Unknown element codes are treated
as provider-written, so a ruleset change can never make a check pass silently.

---

## 4. Prohibitions

The resolution forbids things; a validator that reads free text cannot reliably detect them, so
the product does not try. Each prohibition is handled in one of two ways.

**Confirmation.** The provider ticks an explicit statement, the text is stored with the document
(`Confirmations`), and **all of them are required to sign in both modes**. The texts, verbatim:

| Kind | Code | Text |
|---|---|---|
| Declaração | `sintomas_situacoes_ou_estados_psicologicos` | Confirmo que esta declaração não descreve sintomas, situações ou estados psicológicos. |
| Atestado | `informacao_alem_do_solicitado` | Confirmo que o atestado se restringe à informação solicitada. |
| Relatório | `afirmacao_sem_fonte_ou_sustentacao` | Confirmo que toda afirmação do relatório tem fonte ou sustentação técnica. |
| Relatório | `descricao_literal_das_sessoes` | Confirmo que o relatório não transcreve literalmente as sessões. |
| Parecer (delivery 2) | `resultado_de_avaliacao_ou_intervencao_psicologica` | Confirmo que este parecer não resulta de processo de avaliação ou intervenção psicológica. |

**By construction.** Atestado `quebra_de_paragrafo_sem_preenchimento` (art. 10 §5º): the atestado
body is assembled as continuous text with line breaks removed, so a paragraph break cannot exist.
No confirmation is asked because the system can guarantee it.

`ConformityChecks."ForbiddenFound"` therefore **stays empty by design**: prohibitions are
confirmations, not detection. Do not fill it with guesses.

---

## 5. Modes

`Providers."DocumentCheckMode"`, set in Ajustes ("Conferência de documentos"):

| Mode | Signing with missing elements |
|---|---|
| `blocking` (default) | Refused: `409 missing_elements` with the list. |
| `advisory` | Allowed only with `acknowledgeMissing`; the provider is told what is missing and confirms. |

In both modes the confirmations are required, and signing needs the CRP (`422 missing_crp`) and
the city of practice (`422 missing_city`). Every signature writes a `ConformityChecks` row with
the ruleset, `MissingElements`, the `Mode` in force and `Passed`. An advisory signature with
missing elements is stored with `Passed = false`, so the record shows it was signed incomplete on
purpose.

The profile gains "Cidade de atendimento" (`Providers."PracticeCity"`); it is the document's
`local_emissao`. The mode has its own `GET|PUT providers/me/document-settings`
(`{ documentCheckMode }`).

---

## 6. Signing, immutability and correction

- A draft is editable (`PUT`, whole-field save). Each save audits per field.
- **Signing** re-checks, then stores `SignedAt`, `SignedByCrp`, the assembled `Body`, the check and
  a snapshot of what the PDF prints (`SignedCity`, `SignedByName`, `SignedPatientName`), so later
  profile or patient edits never change a signed document. From then on the document is **immutable**: any save answers `409 locked`.
- **Correction**: "Corrigir" creates a **new draft** copied from the signed document and marks the
  original superseded immediately (`SupersededBy`). The original stays unchanged and links to its
  replacement. The confirmations are **not** copied; they must be given again, because they are
  statements about the new text.
- An archived patient answers `409 archived` for create and correct only; save and sign are not
  refused, as for notes.
- Provider scoping: a document of another provider is a `404`.

Enforcement is in the application (`ClinicalDocumentService`), as for signed notes; there is no
database trigger. Every mutation takes a row lock and writes its audit entries in the same
transaction.

### 6.1 Audit

`AuditEntries` (with `DocumentId`) records: `document_created`; `document_saved` once per changed
field with the previous and the new value; `confirmations`; `document_signed`;
`document_corrected`; `document_exported`; `document_link_issued`.

---

## 7. The PDF

`DocumentPdf` (QuestPDF) with the note export's storage and links:

- Footer on **every page**: "{name} · CRP {crp}" and "Página X de Y". The name and CRP stand in
  place of the initials of art. 5º §8º (numbered pages, initialled except the last, signature on
  the last page); the signature block closes the last page.
- Date line: "{city}, {d de MMMM de yyyy}".
- Stored once at `documents/{providerId}/{id}.pdf` through `INoteFileStore` (SHA-256 recorded),
  file name `documento-{CODE}.pdf`. Export is idempotent; each request issues a **one-hour**
  presigned link and is audited.

Routes under `/api/phi`: `GET providers/me/document-kinds`; `GET|POST
providers/me/patients/{id}/documents`; `GET|PUT providers/me/documents/{id}`; `POST
providers/me/documents/{id}/sign`, `/correct`, `/export`.

---

## 8. UI

- **Patient record, card "Documentos"**: kind, status (rascunho, assinado, substituído), dates, and
  "Novo documento" with the kind picker.
- **Editor `/documents/$documentId`**: system values read-only; provider fields with hint and
  article; "Prazo de validade" last; panel "Conferência" with a check or cross per element
  (refreshed on save), the confirmations and "Assinar". Signed: read-only, "Exportar PDF" /
  "Baixar PDF", "Corrigir". A "?" help panel summarises the rules in plain language.
- **Ajustes**: "Conferência de documentos". **Perfil**: "Cidade de atendimento".

---

## 9. What the old seed left out on purpose

The reference seed of the deleted backend (commit `250b0442`, `backend/db/02_seed_reference.sql`)
derived the rulesets article by article and recorded what it excluded. These decisions carry over
to the migration and must not be reverted.

- **References are mandatory only for laudo and parecer.** Art. 5º §7º asks for theoretical and
  technical references "observando a especificidade do documento", but only arts. 13 §7º and 14
  §6º make them obligatory. Requiring them in a relatório would fail a correct document; that was
  the worst defect of the earlier draft.
- **Optional clauses never become requirements.** Arts. 10 §7º, 11 §6º III, 13 §6º III and 14 §5º
  II (the confidentiality caveat, extrajudicial character and non-liability for later use) are
  "é facultado à(ao) psicóloga(o)". Optional cannot become a validator requirement.
- **Rules that cross every kind appear in every list** instead of staying implicit: art. 5º §8º
  (numbered pages, initialled up to the penultimate, signature on the last; in a one-page
  document the requirement satisfies itself, so the validator must not flag missing initials on
  a one-page declaração) and art. 17 (validity period in the last paragraph, the item most often
  missing in practice).
- **Not elements of the document:** art. 15 (minimum custody of 5 years, which matches
  `RetentionRules."GuardYears" >= 5`) and art. 16 §1º (delivery protocol signed by the requester).
  The protocol is evidence of delivery, not a document element, so it belongs to the F-07 flow and
  not to these lists.
- **The earlier `draft-1` ruleset** diverged from the norm in four non-conservative ways: it
  required a requester in declaração, required references in relatório, invented a "metodologia"
  in laudo, and saw no prohibition. The old seed deleted it only when no check referred to it; the
  current migration does not touch it.

---

## 10. Adding or changing a ruleset or a kind

1. Always a **new migration** in `design/migrations/` (mirrored into `anamnys-db-script.sql`),
   never an edit of an old one. A ruleset change is a new `Version`, so existing checks keep
   pointing at the rules they were made under.
2. Derive the list from the resolution text and record the article in `ArticleRef`. Optional
   clauses stay out (section 9).
3. Give every new element an origin and a pt-BR label in `DocumentCatalog`; an element absent from
   the catalog is treated as provider-written.
4. A new kind also needs a form, an assembly in `DocumentBody`, its confirmations (kind-aware
   texts) and a PDF layout, and must be added to `AvailableKinds`.
5. Add or adjust the tests in `anamnys-aspire.Tests/Documents/` and `Data/DocumentsMigrationTests`.
6. Out of scope until their own features: delivery protocol (F-07), retention date
   `RetentionUntil` (F-06), ICP-Brasil signature, sharing (F-17, F-18), sealing into
   `DocumentHashes` (F-45).
