# Dev-only: renders fictitious documents as images (no embedded text) to exercise OCR in F-08.
# Every person, clinic and registration number is invented.
#   powershell -ExecutionPolicy Bypass -File dev-seed/make-scanned-samples.ps1
# Writes dev-seed/sample-documents/*-foto.jpg, *-scan.png and an image-only PDF via make-scanned-pdf.mjs.
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot "sample-documents"
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Render([string]$path, [string[]]$lines, [System.Drawing.Imaging.ImageFormat]$format, [single]$angle) {
    $width = 1240; $height = 1754   # A4 at 150 DPI
    $bmp = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(250, 248, 242))
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.TranslateTransform(0, 0); $g.RotateTransform($angle)
    $title = New-Object System.Drawing.Font("Arial", 26, [System.Drawing.FontStyle]::Bold)
    $body = New-Object System.Drawing.Font("Arial", 18)
    $ink = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(30, 30, 40))
    $y = 110
    foreach ($line in $lines) {
        $font = $body
        $text = $line
        if ($line.StartsWith("#")) { $font = $title; $text = $line.Substring(1) }
        $g.DrawString($text, $font, $ink, 110, $y)
        $y += [int]($font.GetHeight($g) * 1.45)
    }
    $g.Dispose()
    $bmp.Save($path, $format)
    $bmp.Dispose()
    Write-Output (Split-Path $path -Leaf)
}

Render (Join-Path $out "thiago-andrade-receita-foto.jpg") @(
    "#RECEITUÁRIO",
    "Dra. Helena Prado Castilho - Médica de família",
    "CRM-SP 111111 (fictício)",
    "",
    "Paciente: Thiago Andrade",
    "",
    "Uso oral",
    "1. Escitalopram 10 mg ..................... 30 comprimidos",
    "   Tomar 1 comprimido pela manhã.",
    "",
    "Campinas, 12 de outubro de 2026."
) ([System.Drawing.Imaging.ImageFormat]::Jpeg) 1.2

Render (Join-Path $out "vinicius-andrade-atestado-scan.png") @(
    "#ATESTADO MÉDICO",
    "",
    "Atesto que o paciente Vinícius Andrade esteve em consulta",
    "psiquiátrica nesta data e deve permanecer afastado de suas",
    "atividades laborais por 5 (cinco) dias.",
    "",
    "CID-10 F33.1",
    "",
    "Campinas, 15 de outubro de 2026.",
    "",
    "Dr. Otávio Rangel Pires",
    "Psiquiatra - CRM-SP 333333 (fictício)"
) ([System.Drawing.Imaging.ImageFormat]::Png) 0

Render (Join-Path $out "_scan-page.jpg") @(
    "#LAUDO PSICOPEDAGÓGICO",
    "Espaço Aprender - Avaliação e Intervenção",
    "",
    "Paciente: Fernanda Barbosa",
    "Solicitante: a própria paciente.",
    "",
    "Conclusão: dificuldades de atenção sustentada compatíveis",
    "com quadro ansioso; sem indícios de transtorno de aprendizagem.",
    "",
    "São Paulo, 3 de outubro de 2026.",
    "",
    "Renata Moraes Lima - Psicopedagoga",
    "ABPp 4444 (fictício)"
) ([System.Drawing.Imaging.ImageFormat]::Jpeg) -0.8
