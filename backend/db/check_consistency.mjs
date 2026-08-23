#!/usr/bin/env node
// =============================================================================
//  Anamnys — conferência de consistência entre o esquema e o que fala sobre ele
//
//    node check_consistency.mjs [--json]
//
//  O SQL é a fonte da verdade DO MODELO DE DADOS: é o único artefato executado
//  contra um PostgreSQL real com cada restrição testada. Tudo o mais — o
//  documento HTML, o PDF, as páginas do Notion, o ERD do dbdiagram — descreve
//  este esquema, e descrição envelhece calada.
//
//  A DOCUMENTAÇÃO OFICIAL É O NOTION, em "Arquitetura e Fluxo de Dados". Este
//  script NÃO A ALCANÇA: ele lê arquivos locais, e ler o Notion exigiria token e
//  rede, o que é decisão de infraestrutura e não de documentação. Ou seja, a
//  cobertura aqui é dos documentos gerados em specs/docs/ e do README — as
//  páginas do Notion continuam dependendo de alguém reparar. Enquanto for assim,
//  o jeito de não derivar é atualizar o Notion NO MESMO passo em que o esquema
//  muda, e usar este script para provar que o resto acompanhou.
//
//  ESTE SCRIPT EXISTE POR CAUSA DE UM ERRO REAL. O documento de arquitetura
//  passou semanas afirmando "53 entidades" depois de o modelo crescer para 70.
//  Ninguém percebeu, porque nada comparava um documento com o outro. Números
//  errados em documento técnico não parecem errados — parecem números.
//
//  Sai com código 1 quando encontra divergência, para poder rodar em CI.
// =============================================================================

import fs from 'fs';
import path from 'path';

const DIR = path.dirname(new URL(import.meta.url).pathname);
const rd = f => { try { return fs.readFileSync(f, 'utf8'); } catch { return null; } };

// ── 1. A verdade, extraída do esquema ────────────────────────────────────────
const schema = rd(path.join(DIR, '01_schema.sql'));
const seed   = rd(path.join(DIR, '02_seed_reference.sql'));
if (!schema || !seed) {
  console.error('não achei 01_schema.sql / 02_seed_reference.sql ao lado deste script');
  process.exit(2);
}

// Sem os comentários. Uma versão citada num comentário que EXPLICA a remoção
// não é uma versão em uso — confundir as duas gera alarme falso, e verificador
// que grita à toa é verificador que ninguém lê.
const seedCode = seed.replace(/--[^\n]*/g, '');

const truth = {
  tables:    [...schema.matchAll(/^create table "([A-Za-z]+)"/gm)].map(m => m[1]),
  views:     [...schema.matchAll(/^create view "([A-Za-z]+)"/gm)].map(m => m[1]),
  triggers:  [...schema.matchAll(/^create trigger "([A-Za-z_]+)"/gm)].map(m => m[1]),
  // Tipos de documento aceitos, lidos da restrição e não de uma lista à mão.
  docKinds:  (schema.match(/"ClinicalDocuments_Kind_ck"[\s\S]*?check \("Kind" in \(([\s\S]*?)\)\)/) || [,''])[1]
               .match(/'([a-z_]+)'/g)?.map(s => s.replace(/'/g, '')) || [],
  // Só o que o INSERT realmente grava.
  rulesetVersions: [...new Set(
    [...(seedCode.match(/insert into "ConformityRulesets"[\s\S]*?on conflict/) || [''])[0]
        .matchAll(/'(cfp-[0-9.\-a-z]+|draft-\d+)'/g)].map(m => m[1]))],
  instruments: [...new Set([...seedCode.matchAll(/^\s*\('([A-Z0-9-]{2,12})',\s*'/gm)].map(m => m[1]))],
  enabledInstruments: [...new Set(
    [...(seedCode.match(/insert into "Instruments"[\s\S]*?on conflict/) || [''])[0]
        .matchAll(/^\s*\('([A-Z0-9-]{2,12})',\s*'/gm)].map(m => m[1]))],
};
truth.tableCount = truth.tables.length;

// ── 2. O que cada documento derivado afirma ──────────────────────────────────
// Cada regra diz: onde procurar, o que o padrão captura, e qual é o valor certo.
const DERIVED = [
  ['../../../specs/docs/anamnys-modelo-dados.html', 'HTML · Modelo de Dados'],
  ['../../../specs/docs/anamnys-arquitetura.html',  'HTML · Arquitetura'],
  ['../../../specs/docs/anamnys-fluxos.html',       'HTML · Fluxos'],
  ['../anamnys-modelo-dados.html',         'fonte · Modelo de Dados'],
  ['../anamnys-arquitetura.html',          'fonte · Arquitetura'],
  ['../anamnys-fluxos.html',               'fonte · Fluxos'],
  ['README.md',                               'README do banco'],
  ['notion-modelo-dados.md',                  'Notion · Modelo de Dados (cópia local)'],
  ['notion-overview.md',                      'Notion · Overview (cópia local)'],
];

const problems = [];
const seen = [];

for (const [rel, label] of DERIVED) {
  const file = path.resolve(DIR, rel);
  const txt = rd(file);
  if (txt === null) continue;          // documento ausente não é divergência
  seen.push(label);

  // a) contagem de entidades / tabelas
  // Minúsculas de propósito: "04 ENTIDADES" é separador de seção, não afirmação.
  // E o número não pode vir colado a outro dígito ou hífen, senão "F-04" entra.
  for (const m of txt.matchAll(/(?<![\d-])(\d{2,3})\s+(?:entidades|tabelas)\b/g)) {
    const n = Number(m[1]);
    if (n !== truth.tableCount) {
      problems.push(`${label}: diz "${m[0]}" — o esquema tem ${truth.tableCount} tabelas`);
    }
  }

  // b) versão do conjunto de regras de conformidade.
  // Positivo, como em (d): o documento pode citar "draft-1" ao CONTAR a
  // história da correção. O que denuncia deriva é falar do assunto e não
  // conhecer a versão em uso.
  if (/draft-1|ConformityRuleset|conformity_ruleset/.test(txt)) {
    const missing = truth.rulesetVersions.filter(v => !txt.includes(v));
    if (missing.length) {
      problems.push(`${label}: fala das regras de conformidade e não menciona ${missing.join(', ')}`);
    }
  }

  // c) número de modalidades de documento psicológico
  for (const m of txt.matchAll(/(cinco|seis|quatro)\s+tipos\s+da\s+Res\.?\s*CFP\s*006/gi)) {
    const word = { quatro: 4, cinco: 5, seis: 6 }[m[1].toLowerCase()];
    if (word !== truth.docKinds.length) {
      problems.push(`${label}: diz "${m[0]}" — a restrição aceita ${truth.docKinds.length} tipos`);
    }
  }

  // d) catálogo de instrumentos desatualizado.
  // Positivo, não negativo: um documento que EXPLICA a mudança precisa poder
  // citar o instrumento antigo sem ser acusado. O que denuncia deriva é o
  // documento falar de instrumentos e não conhecer os que estão no catálogo.
  if (/PHQ-9/.test(txt)) {
    const missing = truth.enabledInstruments.filter(c => !txt.includes(c));
    if (missing.length) {
      problems.push(`${label}: fala de instrumentos e não menciona ${missing.join(', ')} — o catálogo mudou`);
    }
  }

  // e) tabelas novas que nenhum documento derivado menciona
  if (/modelo|entidades|Modelo de Dados/i.test(label)) {
    // Compara só letras e dígitos em minúsculas, porque o mesmo conceito aparece
    // como ProviderInstrumentOptIns no SQL e provider_instrument_optin no ERD.
    const flat = txt.toLowerCase().replace(/[^a-z0-9]/g, '');
    for (const t of ['ProviderInstrumentOptIns']) {
      const key = t.toLowerCase().replace(/s$/, '');
      if (!flat.includes(key)) {
        problems.push(`${label}: não menciona a tabela "${t}"`);
      }
    }
  }
}

// ── 3. Coerências internas do próprio esquema ────────────────────────────────
// Instrumento habilitado exige pronunciamento do CFP — a restrição garante no
// banco, mas o seed pode estar internamente incoerente antes de rodar.
const enabledBlock = (seedCode.match(/insert into "Instruments"[\s\S]*?on conflict/) || [''])[0];
if (/'sem_classificacao'|'nao_avaliado'/.test(enabledBlock)) {
  problems.push('seed: instrumento sem pronunciamento do CFP no bloco do catálogo padrão');
}
if (truth.rulesetVersions.includes('draft-1')) {
  problems.push('seed: "draft-1" voltou a ser inserido no arquivo de conformidade');
}

// ── 4. Relatório ─────────────────────────────────────────────────────────────
const asJson = process.argv.includes('--json');
if (asJson) {
  console.log(JSON.stringify({ truth, problems, checked: seen }, null, 2));
} else {
  console.log(`fonte da verdade: 01_schema.sql`);
  console.log(`  tabelas ${truth.tableCount} · visões ${truth.views.length} · gatilhos ${truth.triggers.length}`);
  console.log(`  tipos de documento: ${truth.docKinds.length} (${truth.docKinds.join(', ')})`);
  console.log(`  conformidade: ${truth.rulesetVersions.join(', ')}`);
  console.log(`  instrumentos: ${truth.instruments.length} (catálogo: ${truth.enabledInstruments.join(', ')})`);
  console.log(`\ndocumentos conferidos: ${seen.length ? seen.join(' · ') : '(nenhum encontrado)'}`);
  if (!problems.length) {
    console.log('\nnenhuma divergência.');
  } else {
    console.log(`\n${problems.length} divergência(s):\n`);
    for (const p of problems) console.log(`  ✗ ${p}`);
  }
}

process.exit(problems.length ? 1 : 0);
