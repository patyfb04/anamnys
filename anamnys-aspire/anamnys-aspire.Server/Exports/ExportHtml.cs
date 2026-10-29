using System.Text;

namespace Anamnys.Server.Exports;

// Static pages that open in any browser without Anamnys: inline CSS, no JavaScript, no external
// resources, links only to files inside the ZIP (spec 2026-10-23 §4).
public static class ExportHtml
{
    private const string Css = """
        body { font-family: Georgia, "Times New Roman", serif; color: #1f2933; background: #ffffff; margin: 0 auto; max-width: 960px; padding: 24px; line-height: 1.5; }
        h1 { font-size: 1.6em; margin-bottom: 0.2em; }
        h2 { font-size: 1.25em; border-bottom: 1px solid #c8ccd4; padding-bottom: 4px; margin-top: 2em; }
        h3 { font-size: 1.05em; margin-bottom: 0.3em; }
        table { border-collapse: collapse; width: 100%; margin: 8px 0; }
        th, td { border: 1px solid #c8ccd4; padding: 4px 8px; text-align: left; vertical-align: top; }
        th { background: #f1f3f6; }
        .muted { color: #5f6b7a; }
        .badge { display: inline-block; border: 1px solid #5f6b7a; border-radius: 4px; padding: 0 6px; font-size: 0.85em; }
        .draft { border-color: #b26b00; color: #8a5300; }
        .superseded { border-color: #9b1c1c; color: #9b1c1c; }
        .item { border: 1px solid #e1e4ea; border-radius: 6px; padding: 8px 12px; margin: 12px 0; page-break-inside: avoid; }
        .text { white-space: pre-wrap; }
        dl { display: grid; grid-template-columns: max-content 1fr; gap: 2px 12px; }
        dt { font-weight: bold; }
        dd { margin: 0; }
        @media print { body { max-width: none; padding: 0; } a { color: inherit; text-decoration: none; } .noprint { display: none; } }
        """;

    // Escapes only the markup characters: accents stay readable in the UTF-8 source.
    private static string E(string? text) =>
        (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

    private static string Page(string title, string body) =>
        $"""
        <!DOCTYPE html>
        <html lang="pt-BR">
        <head>
        <meta charset="utf-8">
        <title>{E(title)}</title>
        <style>
        {Css}
        </style>
        </head>
        <body>
        {body}
        </body>
        </html>
        """;

    public static string Index(AccountData data, PerfilJson profile, IReadOnlyList<PatientExport> patients, IReadOnlyList<TermPdfPlan>? terms = null)
    {
        terms ??= [];
        var sb = new StringBuilder();
        sb.Append($"<h1>Exportação da conta Anamnys</h1>\n");
        sb.Append($"<p class=\"muted\">Gerada em {E(ExportFormat.Display(profile.ExportacaoGeradaEm))}. Arquivo com dados de saúde: guarde-o em local seguro.</p>\n");
        sb.Append("<h2>Conta</h2>\n<dl>\n");
        Row(sb, "Nome", profile.Nome);
        Row(sb, "E-mail", profile.Email);
        Row(sb, "CRP", profile.Crp);
        Row(sb, "Cidade de atendimento", profile.CidadeAtendimento);
        Row(sb, "Pacientes", patients.Count.ToString(ExportFormat.PtBr));
        Row(sb, "Notas", data.Notes.Count.ToString(ExportFormat.PtBr));
        Row(sb, "Documentos", data.Documents.Count.ToString(ExportFormat.PtBr));
        Row(sb, "Agendamentos", data.Appointments.Count.ToString(ExportFormat.PtBr));
        sb.Append("</dl>\n");
        sb.Append("<p>Arquivos da conta: <a href=\"conta/perfil.json\">perfil.json</a>, <a href=\"conta/agenda.csv\">agenda.csv</a>, ")
            .Append("<a href=\"conta/auditoria.csv\">auditoria.csv</a>. Descrição de todos os arquivos: <a href=\"LEIA-ME.txt\">LEIA-ME.txt</a>.</p>\n");

        sb.Append("<h2>Pacientes</h2>\n");
        if (patients.Count == 0)
        {
            sb.Append("<p class=\"muted\">Nenhum paciente cadastrado.</p>\n");
        }
        else
        {
            sb.Append("<table>\n<tr><th>Pasta</th><th>Nome</th><th>Nascimento</th><th>Situação</th><th>Notas</th><th>Documentos</th></tr>\n");
            foreach (var p in patients)
            {
                var j = p.Json;
                sb.Append("<tr>")
                    .Append($"<td><a href=\"pacientes/{E(j.Pasta)}/paciente.html\">{E(j.Pasta)}</a></td>")
                    .Append($"<td><a href=\"pacientes/{E(j.Pasta)}/paciente.html\">{E($"{j.Nome} {j.Sobrenome}".Trim())}</a></td>")
                    .Append($"<td>{(j.Nascimento is { } birth ? E(ExportFormat.Display(birth)) : "")}</td>")
                    .Append($"<td>{(j.Arquivado ? "Arquivado" : "Ativo")}</td>")
                    .Append($"<td>{j.Notas.Count}</td>")
                    .Append($"<td>{j.Documentos.Count}</td>")
                    .Append("</tr>\n");
            }
            sb.Append("</table>\n");
        }

        // Discarded records appear only through their disposal term (F-06).
        if (terms.Count > 0)
        {
            sb.Append("<h2>Termos de descarte</h2>\n");
            sb.Append("<p class=\"muted\">Prontuários descartados após o prazo de guarda. Dados estruturados em ")
                .Append($"<a href=\"{ExportViews.TermsFolder}/termos.json\">termos.json</a>.</p>\n");
            sb.Append("<table>\n<tr><th>Descartado em</th><th>Paciente</th><th>Período</th><th>Termo</th></tr>\n");
            foreach (var t in terms)
            {
                var j = t.Json;
                sb.Append("<tr>")
                    .Append($"<td>{E(ExportFormat.Display(j.DescartadoEm))}</td>")
                    .Append($"<td>{E(j.Paciente)}</td>")
                    .Append($"<td>{E(ExportFormat.Display(j.PeriodoInicio))} a {E(ExportFormat.Display(j.PeriodoFim))}</td>")
                    .Append($"<td><a href=\"{E(j.Pdf)}\">PDF</a></td>")
                    .Append("</tr>\n");
            }
            sb.Append("</table>\n");
        }
        return Page("Exportação Anamnys", sb.ToString());
    }

    public static string Patient(PatientExport export)
    {
        var j = export.Json;
        var name = $"{j.Nome} {j.Sobrenome}".Trim();
        var sb = new StringBuilder();
        sb.Append("<p class=\"noprint\"><a href=\"../../index.html\">&larr; Voltar à lista de pacientes</a></p>\n");
        sb.Append($"<h1>{E(name)}</h1>\n");
        sb.Append($"<p class=\"muted\">Pasta {E(j.Pasta)}{(j.Arquivado ? " · <span class=\"badge\">Arquivado</span>" : "")}</p>\n");

        sb.Append("<h2>Cadastro</h2>\n<dl>\n");
        Row(sb, "Nome", j.Nome);
        Row(sb, "Sobrenome", j.Sobrenome);
        Row(sb, "Nascimento", j.Nascimento is { } birth ? ExportFormat.Display(birth) : null);
        Row(sb, "E-mail", j.Email);
        Row(sb, "Telefone", j.Telefone);
        Row(sb, "Idioma preferido", j.IdiomaPreferido);
        Row(sb, "Cadastrado em", ExportFormat.Display(j.CadastradoEm));
        Row(sb, "Última consulta", j.UltimaConsulta is { } last ? ExportFormat.Display(last) : null);
        Row(sb, "Arquivado em", j.ArquivadoEm is { } archived ? ExportFormat.Display(archived) : null);
        sb.Append("</dl>\n");

        sb.Append("<h2>Diagnósticos</h2>\n");
        Table(sb, ["Descrição", "CID", "Registrado em", "Resolvido em"],
            j.Diagnosticos.Select(d => new[]
            {
                d.Descricao, d.Cid, ExportFormat.DisplayDay(d.RegistradoEm), d.ResolvidoEm is { } r ? ExportFormat.Display(r) : null,
            }));

        sb.Append("<h2>Medicações</h2>\n");
        Table(sb, ["Medicamento", "Dose", "Posologia", "Início", "Fim"],
            j.Medicacoes.Select(m => new[]
            {
                m.Medicamento, m.Dose, m.Posologia, ExportFormat.Display(m.Inicio), m.Fim is { } end ? ExportFormat.Display(end) : null,
            }));

        sb.Append("<h2>Plano terapêutico</h2>\n");
        if (j.Planos.Count == 0)
        {
            sb.Append("<p class=\"muted\">Nenhum registro.</p>\n");
        }
        foreach (var plan in j.Planos)
        {
            sb.Append($"<p>Plano criado em {E(ExportFormat.DisplayDay(plan.CriadoEm))}")
                .Append(plan.EncerradoEm is { } closed ? $", encerrado em {E(ExportFormat.DisplayDay(closed))}" : "")
                .Append("</p>\n");
            Table(sb, ["Objetivo", "Criado em"],
                plan.Objetivos.Select(o => new[] { o.Descricao, ExportFormat.DisplayDay(o.CriadoEm) }));
        }

        sb.Append("<h2>Escalas</h2>\n");
        Table(sb, ["Instrumento", "Pontuação", "Faixa", "Aplicada em"],
            j.Escalas.Select(s => new[]
            {
                $"{s.Instrumento} — {s.NomeInstrumento}", s.Pontuacao.ToString(ExportFormat.PtBr),
                $"{s.PontuacaoMinima}–{s.PontuacaoMaxima}", ExportFormat.DisplayDay(s.AplicadaEm),
            }));

        sb.Append("<h2>Temas</h2>\n");
        Table(sb, ["Tema", "Tema pai", "Notas"],
            j.Notas.SelectMany(n => n.Temas.Select(t => (t, n.Id)))
                .GroupBy(x => (x.t.Codigo, Tema: ThemeLabel(x.t), x.t.TemaPai))
                .OrderByDescending(g => g.Select(x => x.Id).Distinct().Count()).ThenBy(g => g.Key.Tema, StringComparer.Create(ExportFormat.PtBr, true))
                .Select(g => new[] { g.Key.Tema, g.Key.TemaPai, g.Select(x => x.Id).Distinct().Count().ToString(ExportFormat.PtBr) }));

        sb.Append("<h2>Notas</h2>\n");
        if (j.Notas.Count == 0)
        {
            sb.Append("<p class=\"muted\">Nenhum registro.</p>\n");
        }
        foreach (var n in j.Notas)
        {
            var badge = n.Situacao == ExportViews.Signed
                ? "<span class=\"badge\">Assinada</span>"
                : "<span class=\"badge draft\">Rascunho</span>";
            sb.Append("<div class=\"item\">\n")
                .Append($"<h3>{E(ExportFormat.DisplayDay(n.Data))} · {E(n.Formato)} {badge}</h3>\n")
                .Append($"<p class=\"muted\">Nota {E(n.Codigo)}")
                .Append(n.AssinadaEm is { } signed ? $" · assinada em {E(ExportFormat.Display(signed))}, CRP {E(n.CrpAssinatura)}" : "")
                .Append(n.Pdf is { } pdf ? $" · <a href=\"{E(pdf)}\">PDF</a>" : "")
                .Append("</p>\n");
            if (n.Secoes.Count == 0)
            {
                sb.Append("<p class=\"muted\">Sem conteúdo estruturado.</p>\n");
            }
            foreach (var s in n.Secoes)
            {
                sb.Append($"<p><strong>{E(s.Titulo)}</strong></p>\n<p class=\"text\">{(s.Conteudo.Length == 0 ? "<span class=\"muted\">Nada registrado</span>" : E(s.Conteudo))}</p>\n");
            }
            if (n.Temas.Count > 0)
            {
                sb.Append($"<p class=\"muted\">Temas: {E(string.Join(", ", n.Temas.Select(ThemeLabel)))}</p>\n");
            }
            sb.Append("</div>\n");
        }

        sb.Append("<h2>Documentos psicológicos</h2>\n");
        if (j.Documentos.Count == 0)
        {
            sb.Append("<p class=\"muted\">Nenhum registro.</p>\n");
        }
        foreach (var d in j.Documentos)
        {
            var badge = d.Situacao switch
            {
                ExportViews.DocumentSigned => "<span class=\"badge\">Assinado</span>",
                ExportViews.DocumentSuperseded => "<span class=\"badge superseded\">Substituído</span>",
                _ => "<span class=\"badge draft\">Rascunho</span>",
            };
            sb.Append("<div class=\"item\">\n")
                .Append($"<h3>{E(d.NomeTipo)} {badge}</h3>\n")
                .Append($"<p class=\"muted\">Documento {E(d.Codigo)} · criado em {E(ExportFormat.DisplayDay(d.CriadoEm))}")
                .Append(d.AssinadoEm is { } signed ? $" · assinado em {E(ExportFormat.Display(signed))}" : "")
                .Append(d.Pdf is { } pdf ? $" · <a href=\"{E(pdf)}\">PDF</a>" : d.AssinadoEm is null ? "" : " · PDF indisponível")
                .Append("</p>\n");
            if (d.Assinatura is { } a)
            {
                sb.Append($"<p class=\"muted\">Assinado por {E(a.Nome)}, CRP {E(a.Crp)}, {E(a.Cidade)}.</p>\n");
            }
            if (d.Campos.Count > 0)
            {
                foreach (var c in d.Campos)
                {
                    sb.Append($"<p><strong>{E(c.Rotulo)}</strong></p>\n<p class=\"text\">{E(c.Valor)}</p>\n");
                }
            }
            else if (d.Corpo.Trim().Length > 0)
            {
                sb.Append($"<p class=\"text\">{E(d.Corpo.Trim())}</p>\n");
            }
            if (d.Equipe.Count > 0)
            {
                Table(sb, ["Equipe", "Categoria", "Registro", "Análise"],
                    d.Equipe.Select(t => new[] { t.Nome, t.Categoria, t.Registro, t.Analise }));
            }
            sb.Append("</div>\n");
        }

        return Page($"Paciente {j.Pasta}", sb.ToString());
    }

    private static string ThemeLabel(TemaJson theme) => theme.Proposto ? $"{theme.Tema} (proposta da IA)" : theme.Tema;

    private static void Row(StringBuilder sb, string label, string? value) =>
        sb.Append($"<dt>{E(label)}</dt><dd>{(string.IsNullOrWhiteSpace(value) ? "<span class=\"muted\">—</span>" : E(value))}</dd>\n");

    private static void Table(StringBuilder sb, IReadOnlyList<string> header, IEnumerable<string?[]> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0)
        {
            sb.Append("<p class=\"muted\">Nenhum registro.</p>\n");
            return;
        }
        sb.Append("<table>\n<tr>");
        foreach (var h in header)
        {
            sb.Append($"<th>{E(h)}</th>");
        }
        sb.Append("</tr>\n");
        foreach (var row in list)
        {
            sb.Append("<tr>");
            foreach (var cell in row)
            {
                sb.Append($"<td class=\"text\">{E(cell)}</td>");
            }
            sb.Append("</tr>\n");
        }
        sb.Append("</table>\n");
    }
}
