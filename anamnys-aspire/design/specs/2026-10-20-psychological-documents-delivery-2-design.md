# Psychological Documents, delivery 2: laudo, parecer, relatório multiprofissional (F-05)

**Date:** 2026-10-20
**Status:** Approved for implementation
**Builds on:** `design/specs/2026-10-19-psychological-documents-design.md` (delivery 1) and
`documentation/22-psychological-documents.md`. Everything not restated here works as in delivery 1
(form per kind, conformity check, confirmations, blocking/advisory mode, signing snapshot,
correction by new version, PDF export, documents list).

Delivery 2 enables the three remaining kinds of Res. CFP 006/2019 art. 8. With it F-05 is done.

## 1. Decisions (agreed with the user)

| Topic | Decision |
|---|---|
| Co-authorship (relatório multiprofissional, art. 12) | The provider types the team in the document: name, category and registry of each professional, and that professional's analysis. Only the provider (the psychologist) signs in Anamnys; art. 12 requires only `assinatura_do_psicologo`. Co-authors have no account. |
| Parecer subject (art. 14) | Bound to a patient like every other document. The person named as the subject is the patient. A parecer about an institution or a non-patient is a recorded gap. |
| Titulação (parecer, art. 14 §2º V) | A required field of each parecer, typed per document. |
| Items (laudo, parecer) | `secoes_em_forma_de_itens` is guaranteed by construction: the PDF numbers the items. |
| References | One provider field, one reference per line; printed as the last numbered item. |
| Team storage | New column `ClinicalDocuments."Team" jsonb DEFAULT '[]' NOT NULL` (array of `{name, category, registry, analysis}`), because the team does not fit the flat `Fields` map. |
| Branch | `feature/psychological-documents`, uncommitted until the user reviews. |

## 2. Element origins

Rulesets are already seeded (`cfp-006-2019.1`). `DocumentCatalog.AvailableKinds` becomes all six
kinds. Elements already mapped in delivery 1 keep their origin.

### Laudo (art. 13)

| Element | Origin |
|---|---|
| `secoes_em_forma_de_itens`, `titulo_laudo_psicologico`, `carimbo`, `assinatura`, `laudas_numeradas_rubricadas` | `pdf` |
| `identificacao`, `nome_pessoa_ou_instituicao_atendida`, `nome_autor_e_inscricao_crp`, `nome_psicologo`, `inscricao_crp`, `local_emissao`, `data_emissao` | `system` (as delivery 1) |
| `nome_solicitante`, `finalidade`, `descricao_demanda`, `procedimento`, `referencial_teorico_metodologico`, `pessoas_ouvidas_numero_de_encontros_e_duracao`, `analise`, `conclusao`, `referencias`, `prazo_validade` | `provider` |

### Parecer (art. 14)

| Element | Origin |
|---|---|
| `secoes_em_forma_de_itens`, `titulo_parecer_psicologico`, `carimbo`, `assinatura`, `laudas_numeradas_rubricadas` | `pdf` |
| `identificacao`, `nome_psicologo`, `inscricao_crp`, `local_emissao`, `data_emissao` | `system` |
| `nome_pessoa_ou_instituicao_objeto_do_questionamento` | **changes to `system`**: the patient's full name |
| `nome_autor_inscricao_crp_e_titulacao` | `provider`, labelled "Titulação da(o) autora(or)"; name and CRP come from the profile. Missing when the text is empty **or** the profile has no CRP. |
| `nome_solicitante`, `finalidade`, `descricao_demanda`, `analise`, `conclusao`, `referencias`, `prazo_validade` | `provider` |

### Relatório multiprofissional (art. 12)

| Element | Origin |
|---|---|
| `titulo_relatorio_multiprofissional`, `carimbo`, `laudas_numeradas_rubricadas`, `assinatura_do_psicologo` | `pdf` |
| `identificacao`, `nome_pessoa_ou_instituicao_atendida`, `local_emissao`, `data_emissao` | `system` |
| `nomes_autores_categorias_e_registros_profissionais`, `nomes_profissionais`, `inscricoes_profissionais` | **change to `system`**, from the provider (name, "Psicologia", CRP) plus the team. Missing when the profile has no CRP, or the team is empty, or any member lacks name, category or registry. |
| `analise_identificada_por_profissional_e_categoria` | **changes to `system`**, from the team. Missing when the team is empty or any member's analysis is empty. |
| `nome_solicitante`, `finalidade`, `descricao_demanda`, `procedimento`, `procedimentos_privativos_da_psicologia_em_secao_separada`, `analise` (the psychologist's own analysis), `conclusao`, `prazo_validade` | `provider` |

Team member limits: at most 10 members; name ≤ 200, category ≤ 100, registry ≤ 50, analysis ≤
`MaxFieldLength` (20 000) characters. Members are trimmed; a member with every field empty is
dropped on save.

## 3. Prohibitions and confirmation texts

`DocumentCatalog.Prohibition(code)` becomes kind-aware: `Prohibition(kind, code)`, the text names
the kind.

| Kind | Code | Confirmation |
|---|---|---|
| Relatório | `afirmacao_sem_fonte_ou_sustentacao` | Confirmo que toda afirmação do relatório tem fonte ou sustentação técnica. (unchanged) |
| Relatório | `descricao_literal_das_sessoes` | Confirmo que o relatório não transcreve literalmente as sessões. (unchanged) |
| Laudo | `afirmacao_sem_fonte_ou_sustentacao` | Confirmo que toda afirmação do laudo tem fonte ou sustentação técnica. |
| Laudo | `descricao_literal_das_sessoes` | Confirmo que o laudo não transcreve literalmente as sessões. |
| Relatório multiprofissional | `afirmacao_sem_fonte_ou_sustentacao` | Confirmo que toda afirmação do relatório multiprofissional tem fonte ou sustentação técnica. |
| Relatório multiprofissional | `descricao_literal_das_sessoes` | Confirmo que o relatório multiprofissional não transcreve literalmente as sessões. |
| Parecer | `resultado_de_avaliacao_ou_intervencao_psicologica` | Confirmo que este parecer não resulta de processo de avaliação ou intervenção psicológica. |

Delivery-1 kinds keep their texts byte for byte (stored confirmations are codes, so nothing
migrates).

## 4. Data

Migration `design/migrations/2026-10-20-documents-team.sql` (idempotent, one transaction):
`ALTER TABLE "ClinicalDocuments" ADD COLUMN IF NOT EXISTS "Team" jsonb DEFAULT '[]' NOT NULL`, plus
a check constraint `jsonb_typeof("Team") = 'array'` added only if absent. `anamnys-db-script.sql`
gets the same. Entity `ClinicalDocument.Team` (`List<TeamMember>`, record `TeamMember(string Name,
string Category, string Registry, string Analysis)`), mapped like `Fields`.

## 5. Server

- `SaveDocumentRequest` gains `List<TeamMemberRequest>? Team`; validated (limits above, 400
  ValidationProblem keyed `team[i].name` etc.). Saving a team on a kind other than
  `relatorio_multiprofissional` is a 400.
- `ConformityInput` gains the team; `DocumentConformity` applies the rules of section 2.
- Sign: unchanged flow; the team is part of the locked document. Correct: the new draft copies
  `Team` with `Fields`.
- `DocumentDetail` returns `team`. `document-kinds` returns the three new kinds.
- Body (`DocumentBody.Assemble`) for the three kinds: numbered item blocks (laudo, parecer) or
  headed blocks (multiprofissional), the same structure the PDF prints, prazo de validade last.
- Audit, row lock and provider scoping as delivery 1. No new routes.

## 6. PDF

Laudo items (art. 13):
1. Identificação — pessoa atendida, autora(or) e CRP, solicitante, finalidade.
2. Descrição da demanda.
3. Procedimento — procedimento; "Referencial teórico-metodológico"; "Pessoas ouvidas, número de
   encontros e duração" as labelled paragraphs.
4. Análise.
5. Conclusão.
6. Referências — one per line.

Then the prazo de validade as the last paragraph, city/date and the signature block.

Parecer items (art. 14):
1. Identificação — pessoa objeto do questionamento, autora(or), CRP e titulação, solicitante,
   finalidade.
2. Descrição da demanda.
3. Análise.
4. Conclusão.
5. Referências.

Then the prazo de validade, city/date and signature.

Relatório multiprofissional (headed, not numbered):
- Identificação: pessoa atendida, solicitante, finalidade, and the authors, one line each:
  "{name} — {category} — {registry}", the provider first as "{name} — Psicologia — CRP {crp}".
- Descrição da demanda.
- Procedimento.
- Procedimentos privativos da Psicologia.
- Análise: the provider's analysis headed "{name} (Psicologia)", then each member's analysis
  headed "{name} ({category})".
- Conclusão.
- Prazo de validade, city/date, the psychologist's signature block.

The footer is unchanged. The signing snapshot (`SignedByName`, `SignedPatientName`, `SignedCity`,
`SignedByCrp`) is used as in delivery 1.

## 7. Provider app

- "Novo documento" (patient page, documents list) shows the six kinds from `document-kinds`.
- Editor: for laudo and parecer a notice "O PDF organiza este documento em itens numerados (art. 13
  / art. 14)." For the relatório multiprofissional a "Equipe" block between the psychologist's
  analysis and the conclusion: one card per member (Nome, Categoria, Registro, Análise), "Adicionar
  profissional" (up to 10), "Remover"; read-only once signed; saved with the fields.
- The "?" help panel gets a plain-language entry for each new kind.
- Conformity panel shows the team elements with ✓/✗ like any other element.

## 8. Documentation

Chapter 22 (kinds, origins, team, items, confirmation texts, delivery 2 done), gaps file (parecer
about an institution or non-patient; co-authors without account or their own signature), status
file (F-05 done), Notion F-05 annotation.

## 9. Testing

- Conformity (pure): each new kind's provider elements reported when missing; parecer titulação
  missing without CRP; team empty / member incomplete / member analysis empty reported on the right
  elements; kind-specific confirmation texts; delivery-1 texts unchanged.
- Body and PDF (pure): numbered items for laudo and parecer, references one per line, prazo last;
  multiprofissional authors line and per-professional analysis headings; PDF bytes start with
  `%PDF`.
- Service (real DB, Aspire stopped): save and sign each new kind; team saved, returned, locked once
  signed, copied by correction; team on another kind refused; validation limits.
- Migration: column and constraint present; idempotent.
- Endpoint: 400 on an invalid team.
