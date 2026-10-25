// Dev-only: writes fictitious external documents (PDF) for manual upload tests of F-07 and for the
// OCR work of F-08. Every person, clinic and registration number here is invented.
//   node dev-seed/make-sample-documents.mjs   → dev-seed/sample-documents/*.pdf
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const outDir = join(dirname(fileURLToPath(import.meta.url)), "sample-documents");
mkdirSync(outDir, { recursive: true });

// Minimal single-font PDF writer. Text is encoded as WinAnsi (Latin-1), so Portuguese accents work.
function pdf(pages) {
  const esc = (s) => s.replace(/\\/g, "\\\\").replace(/\(/g, "\\(").replace(/\)/g, "\\)");
  const objects = [];
  const add = (body) => objects.push(body) && objects.length;
  const font = add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
  const bold = add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
  const pagesId = objects.length + 1 + pages.length * 2;
  const kids = [];
  for (const lines of pages) {
    let y = 800;
    const ops = ["BT"];
    for (const line of lines) {
      const [text, style] = typeof line === "string" ? [line, "n"] : line;
      const size = style === "h" ? 15 : style === "b" ? 11 : 10.5;
      y -= style === "gap" ? 10 : style === "h" ? 26 : 15;
      if (style === "gap") continue;
      ops.push(`/${style === "n" ? "F1" : "F2"} ${size} Tf 1 0 0 1 56 ${y} Tm (${esc(text)}) Tj`);
    }
    ops.push("ET");
    const stream = Buffer.from(ops.join("\n"), "latin1");
    const content = add({ stream });
    kids.push(add(`<< /Type /Page /Parent ${pagesId} 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 ${font} 0 R /F2 ${bold} 0 R >> >> /Contents ${content} 0 R >>`));
  }
  add(`<< /Type /Pages /Kids [${kids.map((k) => `${k} 0 R`).join(" ")}] /Count ${kids.length} >>`);
  const catalog = add(`<< /Type /Catalog /Pages ${pagesId} 0 R >>`);

  const parts = [Buffer.from("%PDF-1.4\n%\xe2\xe3\xcf\xd3\n", "latin1")];
  const offsets = [];
  let length = parts[0].length;
  objects.forEach((body, i) => {
    offsets.push(length);
    const chunk = typeof body === "string"
      ? Buffer.from(`${i + 1} 0 obj\n${body}\nendobj\n`, "latin1")
      : Buffer.concat([
          Buffer.from(`${i + 1} 0 obj\n<< /Length ${body.stream.length} >>\nstream\n`, "latin1"),
          body.stream,
          Buffer.from("\nendstream\nendobj\n", "latin1"),
        ]);
    parts.push(chunk);
    length += chunk.length;
  });
  const xref = [`xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`, ...offsets.map((o) => `${String(o).padStart(10, "0")} 00000 n \n`)].join("");
  parts.push(Buffer.from(`${xref}trailer\n<< /Size ${objects.length + 1} /Root ${catalog} 0 R >>\nstartxref\n${length}\n%%EOF\n`, "latin1"));
  return Buffer.concat(parts);
}

const docs = {
  "fernanda-barbosa-laudo-psiquiatrico.pdf": [[
    ["CLÍNICA HORIZONTE DE SAÚDE MENTAL", "h"],
    "Rua das Acácias, 120 - São Paulo/SP - (11) 3000-0000",
    ["", "gap"],
    ["LAUDO PSIQUIÁTRICO", "b"],
    ["", "gap"],
    "Paciente: Fernanda Barbosa",
    "Data de nascimento: 02/10/1980",
    "Solicitante: a própria paciente, para acompanhamento psicoterápico.",
    ["", "gap"],
    ["Histórico", "b"],
    "Paciente refere episódios recorrentes de revivescência de evento traumático (assalto em 2025),",
    "com evitação de transporte público, hipervigilância e insônia inicial.",
    ["", "gap"],
    ["Conclusão diagnóstica", "b"],
    "CID-10 F43.1 - Transtorno de estresse pós-traumático.",
    ["", "gap"],
    ["Conduta", "b"],
    "Sertralina 50 mg, 1 comprimido pela manhã.",
    "Prazosina 1 mg, 1 comprimido ao deitar.",
    "Encaminhamento para psicoterapia com foco em trauma.",
    ["", "gap"],
    "São Paulo, 14 de setembro de 2026.",
    ["", "gap"],
    "Dr. Ricardo Menezes Lobo",
    "Psiquiatra - CRM-SP 000000 (fictício)",
  ]],
  "fernanda-barbosa-receita.pdf": [[
    ["RECEITUÁRIO", "h"],
    "Dr. Ricardo Menezes Lobo - Psiquiatra - CRM-SP 000000 (fictício)",
    ["", "gap"],
    "Paciente: Fernanda Barbosa",
    ["", "gap"],
    ["Uso oral", "b"],
    "1. Sertralina 50 mg ............................................ 30 comprimidos",
    "   Tomar 1 comprimido pela manhã.",
    "2. Prazosina 1 mg ............................................. 30 comprimidos",
    "   Tomar 1 comprimido ao deitar.",
    ["", "gap"],
    "São Paulo, 14 de setembro de 2026.",
  ]],
  "thiago-andrade-relatorio-fonoaudiologico.pdf": [[
    ["CENTRO DE FONOAUDIOLOGIA VOZ CLARA", "h"],
    ["", "gap"],
    ["RELATÓRIO FONOAUDIOLÓGICO", "b"],
    ["", "gap"],
    "Paciente: Thiago Andrade",
    "Finalidade: subsidiar o acompanhamento psicológico.",
    ["", "gap"],
    "Avaliação realizada em três sessões (agosto de 2026). Linguagem compreensiva adequada.",
    "Observa-se disfluência típica em situações de exposição social, com aumento de bloqueios",
    "ao falar em público. Não há sinais de alteração neurológica.",
    ["", "gap"],
    ["Recomendações", "b"],
    "Terapia fonoaudiológica semanal por 12 semanas; integração com a psicoterapia.",
    ["", "gap"],
    "Campinas, 2 de setembro de 2026.",
    ["", "gap"],
    "Mariana Duarte Siqueira",
    "Fonoaudióloga - CRFa 2-00000 (fictício)",
  ]],
  "thiago-andrade-encaminhamento.pdf": [[
    ["UNIDADE BÁSICA DE SAÚDE JARDIM AURORA", "h"],
    ["", "gap"],
    ["ENCAMINHAMENTO", "b"],
    ["", "gap"],
    "Encaminho o paciente Thiago Andrade para avaliação e acompanhamento psicológico.",
    "Motivo: ansiedade em situações sociais, com prejuízo no trabalho.",
    "CID-10 F40.1 - Fobia social (hipótese diagnóstica).",
    ["", "gap"],
    "Campinas, 20 de agosto de 2026.",
    ["", "gap"],
    "Dra. Helena Prado Castilho",
    "Médica de família - CRM-SP 111111 (fictício)",
  ]],
  "vinicius-andrade-exame-laboratorial.pdf": [[
    ["LABORATÓRIO CLÍNICO VITA ANÁLISES", "h"],
    ["", "gap"],
    ["RESULTADO DE EXAME", "b"],
    ["", "gap"],
    "Paciente: Vinícius Andrade",
    "Coleta: 05/10/2026     Liberação: 06/10/2026",
    ["", "gap"],
    ["TSH (hormônio tireoestimulante)", "b"],
    "Resultado: 2,1 mUI/L     Valor de referência: 0,4 a 4,0 mUI/L",
    ["", "gap"],
    ["Vitamina B12", "b"],
    "Resultado: 180 pg/mL     Valor de referência: 200 a 900 pg/mL (abaixo da referência)",
    ["", "gap"],
    "Responsável técnico: Dr. Paulo Arantes Reis - CRF-SP 22222 (fictício)",
  ]],
  "vinicius-andrade-relatorio-psiquiatrico.pdf": [
    [
      ["CONSULTÓRIO DR. OTÁVIO RANGEL", "h"],
      ["", "gap"],
      ["RELATÓRIO MÉDICO PSIQUIÁTRICO", "b"],
      ["", "gap"],
      "Paciente: Vinícius Andrade",
      "Solicitante: psicóloga responsável pelo acompanhamento.",
      ["", "gap"],
      ["Evolução", "b"],
      "Em acompanhamento desde março de 2026 por episódios depressivos recorrentes.",
      "Na última consulta refere melhora do humor, porém mantém desânimo matinal e",
      "dificuldade de concentração no trabalho.",
      ["", "gap"],
      ["Diagnóstico", "b"],
      "CID-10 F33.1 - Transtorno depressivo recorrente, episódio atual moderado.",
    ],
    [
      ["Medicação em uso", "b"],
      "Bupropiona 150 mg, 1 comprimido pela manhã (início em 10/03/2026).",
      "Escitalopram 10 mg suspenso em 01/09/2026 por efeitos adversos.",
      ["", "gap"],
      ["Plano", "b"],
      "Manter bupropiona; reavaliação em 60 dias; manter psicoterapia semanal.",
      ["", "gap"],
      "Campinas, 30 de setembro de 2026.",
      ["", "gap"],
      "Dr. Otávio Rangel Pires",
      "Psiquiatra - CRM-SP 333333 (fictício)",
    ],
  ],
};

for (const [name, pages] of Object.entries(docs)) {
  writeFileSync(join(outDir, name), pdf(pages));
  console.log(name);
}
