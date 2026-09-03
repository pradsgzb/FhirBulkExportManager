[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/Enterprise-FHIR-Bulk-Export-Manager-v1.0.0-source.zip')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputPath)
$staging = Join-Path $env:TEMP ("fhir-source-" + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    $target = Join-Path $staging 'FhirBulkExportManager'
    New-Item -ItemType Directory -Path $target | Out-Null
    Get-ChildItem $root -Force | Where-Object { $_.Name -notin @('.git', 'bin', 'obj', '.vs', 'TestResults', 'artifacts', 'Data', 'logs') } | ForEach-Object {
        Copy-Item $_.FullName -Destination $target -Recurse -Force
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
    if (Test-Path $output) { Remove-Item $output -Force }
    Compress-Archive -Path $target -DestinationPath $output -CompressionLevel Optimal
    Write-Host $output
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
}
