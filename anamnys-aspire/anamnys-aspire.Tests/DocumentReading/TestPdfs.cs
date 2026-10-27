using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Anamnys.Tests.DocumentReading;

// PDFs built in the test: A4 pages with embedded text in Helvetica/WinAnsi like the dev-seed samples
// (null = a page with no text at all), or one page that holds only an image (a "scan").
internal static class TestPdfs
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    public static byte[] WithPages(params string?[] pages)
    {
        var objects = new List<byte[]>();
        int Add(string body) { objects.Add(Latin1.GetBytes(body)); return objects.Count; }
        int AddStream(string content)
        {
            var bytes = Latin1.GetBytes(content);
            objects.Add([.. Latin1.GetBytes($"<< /Length {bytes.Length} >>\nstream\n"), .. bytes, .. Latin1.GetBytes("\nendstream")]);
            return objects.Count;
        }

        var font = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var pagesId = font + 1 + pages.Length * 2;
        var kids = new List<int>();
        foreach (var text in pages)
        {
            var ops = new StringBuilder("BT /F1 24 Tf");
            var y = 780;
            foreach (var line in (text ?? "").Split('\n').Where(l => l.Length > 0))
            {
                var escaped = line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
                ops.Append($"\n1 0 0 1 40 {y} Tm ({escaped}) Tj");
                y -= 34;
            }
            ops.Append("\nET");
            var content = AddStream(ops.ToString());
            kids.Add(Add($"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {content} 0 R >>"));
        }
        Add($"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>");
        var catalog = Add($"<< /Type /Catalog /Pages {pagesId} 0 R >>");

        using var output = new MemoryStream();
        output.Write(Latin1.GetBytes("%PDF-1.4\n"));
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            output.Write(Latin1.GetBytes($"{i + 1} 0 obj\n"));
            output.Write(objects[i]);
            output.Write(Latin1.GetBytes("\nendobj\n"));
        }
        var xref = output.Position;
        var table = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            table.Append($"{offset:D10} 00000 n \n");
        }
        table.Append($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        output.Write(Latin1.GetBytes(table.ToString()));
        return output.ToArray();
    }

    public static byte[] ScanOf(byte[] png)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        page.AddPng(png, new PdfRectangle(0, 0, page.PageSize.Width, page.PageSize.Height));
        return builder.Build();
    }

    public static byte[] Sample(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "SampleDocuments", name));

    public static string[] SampleNames() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "SampleDocuments"), "*.pdf")
            .Select(Path.GetFileName).OfType<string>().Order().ToArray();
}
