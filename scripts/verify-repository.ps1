[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'FhirBulkExportManager.sln'
if (-not (Test-Path $solution)) { throw 'Solution file is missing.' }

$solutionText = Get-Content $solution -Raw
$projectMatches = [regex]::Matches($solutionText, 'Project\("[^"]+"\) = "[^"]+", "([^"]+\.csproj)"')
if ($projectMatches.Count -lt 10) { throw "Solution has only $($projectMatches.Count) projects." }
foreach ($match in $projectMatches) {
    $relative = $match.Groups[1].Value.Replace('\', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path (Join-Path $root $relative))) { throw "Missing solution project: $relative" }
}

$centralPackagesPath = Join-Path $root 'Directory.Packages.props'
[xml]$centralPackages = Get-Content $centralPackagesPath -Raw
if ($centralPackages.Project.PropertyGroup.CentralPackageTransitivePinningEnabled -ne 'true') {
    throw 'Central package transitive pinning must remain enabled.'
}

$cryptoXmlPackage = @($centralPackages.Project.ItemGroup.PackageVersion) |
    Where-Object { $_.Include -eq 'System.Security.Cryptography.Xml' } |
    Select-Object -First 1
if ($null -eq $cryptoXmlPackage) {
    throw 'System.Security.Cryptography.Xml must be centrally pinned.'
}

$cryptoXmlVersion = [version]$cryptoXmlPackage.Version
if ($cryptoXmlVersion -lt [version]'10.0.10') {
    throw "System.Security.Cryptography.Xml $cryptoXmlVersion is vulnerable to GHSA-23rf-6693-g89p; use 10.0.10 or later."
}

$efPackageNames = @(
    'Microsoft.EntityFrameworkCore',
    'Microsoft.EntityFrameworkCore.Design',
    'Microsoft.EntityFrameworkCore.Sqlite'
)
$efPackageVersions = foreach ($packageName in $efPackageNames) {
    $package = @($centralPackages.Project.ItemGroup.PackageVersion) |
        Where-Object { $_.Include -eq $packageName } |
        Select-Object -First 1
    if ($null -eq $package) { throw "$packageName must be centrally versioned." }
    [version]$package.Version
}
if (($efPackageVersions | Select-Object -Unique).Count -ne 1) {
    throw "Microsoft EF Core package versions must match: $($efPackageVersions -join ', ')."
}

$persistenceProjectPath = Join-Path $root 'src/FhirBulkExportManager.Persistence.Sqlite/FhirBulkExportManager.Persistence.Sqlite.csproj'
[xml]$persistenceProject = Get-Content $persistenceProjectPath -Raw
$sqliteProviderReference = @($persistenceProject.Project.ItemGroup.PackageReference) |
    Where-Object { $_.Include -eq 'Microsoft.EntityFrameworkCore.Sqlite' } |
    Select-Object -First 1
if ($null -eq $sqliteProviderReference) {
    throw 'The SQLite persistence project must reference Microsoft.EntityFrameworkCore.Sqlite so UseSqlite is available.'
}

$snapshotPath = Join-Path $root 'src/FhirBulkExportManager.Persistence.Sqlite/Migrations/ExportDbContextModelSnapshot.cs'
$snapshotModelPath = Join-Path $root 'src/FhirBulkExportManager.Persistence.Sqlite/Migrations/InitialCreateModel.cs'
$snapshotText = Get-Content $snapshotPath -Raw
$snapshotModelText = Get-Content $snapshotModelPath -Raw
if ($snapshotText -match 'ExportDbContext\.ConfigureModel\s*\(') {
    throw 'The EF migration snapshot must be frozen and must not rebuild itself from the live DbContext model.'
}
$expectedEfProductVersion = $efPackageVersions[0].ToString()
if ($snapshotModelText -notmatch [regex]::Escape("HasAnnotation(`"ProductVersion`", `"$expectedEfProductVersion`")")) {
    throw "The EF migration snapshot product version must match EF Core $expectedEfProductVersion."
}
if ($snapshotModelText -notmatch 'HasColumnType\("TEXT"\)' -or $snapshotModelText -notmatch 'HasColumnType\("INTEGER"\)') {
    throw 'The EF migration snapshot must preserve SQLite provider-specific column metadata.'
}

$productionRoots = @((Join-Path $root 'src'), (Join-Path $root 'plugins'))
$forbidden = Get-ChildItem $productionRoots -Recurse -File -Include *.cs | Select-String -Pattern 'NotImplementedException|TODO|ServerCertificateCustomValidationCallback\s*=|ReadAsStringAsync\(|ExecuteSqlRaw(?:Async)?\s*\(\s*\$"'
if ($forbidden) { throw "Forbidden production placeholder or unsafe API found: $($forbidden.Path):$($forbidden.LineNumber)" }

$emptyProjects = foreach ($project in Get-ChildItem $productionRoots -Recurse -Filter *.csproj) {
    $sourceCount = (Get-ChildItem $project.DirectoryName -Recurse -File -Include *.cs,*.xaml | Measure-Object).Count
    if ($sourceCount -eq 0) { $project.FullName }
}
if ($emptyProjects) { throw "Projects without source: $($emptyProjects -join ', ')" }

$wpfRoot = Join-Path $root 'src/FhirBulkExportManager.App.Wpf'
$ambiguousMessageBoxes = Get-ChildItem $wpfRoot -Recurse -File -Include *.cs |
    Select-String -Pattern '(?<!System\.Windows\.)\bMessageBox\.Show\('
if ($ambiguousMessageBoxes) {
    throw "Unqualified WPF MessageBox call can conflict with Windows Forms: $($ambiguousMessageBoxes.Path):$($ambiguousMessageBoxes.LineNumber)"
}

[xml]$wpfProject = Get-Content (Join-Path $wpfRoot 'FhirBulkExportManager.App.Wpf.csproj') -Raw
if ($wpfProject.Project.PropertyGroup.ApplicationHighDpiMode -ne 'PerMonitorV2') {
    throw 'The WPF/Windows Forms host must configure ApplicationHighDpiMode as PerMonitorV2 in the project file.'
}
$applicationManifest = Get-Content (Join-Path $wpfRoot 'app.manifest') -Raw
if ($applicationManifest -match '<dpiAware(?:ness)?\b') {
    throw 'High-DPI settings must not be declared in app.manifest; use ApplicationHighDpiMode to avoid WFO0003.'
}

Write-Host "Repository verification passed: $($projectMatches.Count) solution projects; no empty production projects; frozen SQLite migration snapshot for EF Core $($efPackageVersions[0]); secure cryptography XML dependency pin $cryptoXmlVersion."
