[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $sdk = dotnet --version
    if (-not $sdk.StartsWith('10.')) {
        throw "The .NET 10 SDK is required. Detected: $sdk"
    }

    dotnet restore FhirBulkExportManager.sln
    dotnet build FhirBulkExportManager.sln --configuration $Configuration --no-restore
    dotnet test FhirBulkExportManager.sln --configuration $Configuration --no-build --logger trx --results-directory TestResults
    ./scripts/verify-repository.ps1
}
finally {
    Pop-Location
}

