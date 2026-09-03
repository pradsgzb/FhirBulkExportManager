[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][string]$Publisher,
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Publisher)) { throw 'MSIX Publisher must match the signing certificate subject.' }
$publish = [IO.Path]::GetFullPath($PublishDirectory)
if (-not (Test-Path (Join-Path $publish 'FhirBulkExportManager.App.Wpf.exe'))) { throw 'Published WPF executable was not found.' }
$makeAppx = Get-ChildItem "$env:ProgramFiles(x86)\Windows Kits\10\bin" -Recurse -Filter makeappx.exe | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeAppx) { throw 'makeappx.exe was not found. Install the Windows 10/11 SDK.' }

$staging = Join-Path $env:TEMP ("fhir-msix-" + [Guid]::NewGuid().ToString('N'))
try {
    Copy-Item $publish $staging -Recurse
    $manifest = (Get-Content (Join-Path $PSScriptRoot 'AppxManifest.template.xml') -Raw).Replace('__PUBLISHER__', [Security.SecurityElement]::Escape($Publisher)).Replace('__VERSION__', $Version)
    Set-Content (Join-Path $staging 'AppxManifest.xml') $manifest -Encoding utf8
    $assets = Join-Path $staging 'Assets'
    New-Item -ItemType Directory $assets -Force | Out-Null
    Add-Type -AssemblyName System.Drawing
    function New-FhirAsset([string]$Name, [int]$Width, [int]$Height) {
        $bitmap = [Drawing.Bitmap]::new($Width, $Height)
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([Drawing.Color]::FromArgb(15, 76, 129))
                $pen = [Drawing.Pen]::new([Drawing.Color]::White, [Math]::Max(2, [Math]::Floor([Math]::Min($Width, $Height) / 11)))
                try {
                    $cx = [int]($Width / 2)
                    $cy = [int]($Height / 2)
                    $arm = [int]([Math]::Min($Width, $Height) * 0.27)
                    $graphics.DrawLine($pen, $cx - $arm, $cy, $cx + $arm, $cy)
                    $graphics.DrawLine($pen, $cx, $cy - $arm, $cx, $cy + $arm)
                }
                finally { $pen.Dispose() }
            }
            finally { $graphics.Dispose() }
            $bitmap.Save((Join-Path $assets $Name), [Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $bitmap.Dispose() }
    }
    New-FhirAsset 'StoreLogo.png' 50 50
    New-FhirAsset 'Square150x150Logo.png' 150 150
    New-FhirAsset 'Square44x44Logo.png' 44 44
    New-FhirAsset 'Wide310x150Logo.png' 310 150
    $output = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory (Split-Path -Parent $output) -Force | Out-Null
    if (Test-Path $output) { Remove-Item $output -Force }
    & $makeAppx.FullName pack /d $staging /p $output /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE" }
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
}
