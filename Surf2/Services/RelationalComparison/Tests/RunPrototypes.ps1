param([Parameter(Mandatory=$true)][string]$ProductionDirectory,
      [string]$OutputDirectory = 'C:\Users\ciara\source\repos\Surf\build-check\comparison-prototypes',
      [switch]$CompileSqlSuite)
$ErrorActionPreference = 'Stop'
$source = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet).Source
$root = Split-Path -Parent $dotnet
$sdk = Get-ChildItem -LiteralPath (Join-Path $root 'sdk') -Directory |
    Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePaths = [ordered]@{}
foreach ($pack in @('Microsoft.NETCore.App.Ref', 'Microsoft.WindowsDesktop.App.Ref')) {
    $version = Get-ChildItem -LiteralPath (Join-Path $root "packs/$pack") -Directory |
        Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    Get-ChildItem -LiteralPath (Join-Path $version.FullName 'ref/net10.0') -Filter '*.dll' |
        ForEach-Object { $referencePaths[$_.Name] = $_.FullName }
}
$refs = $referencePaths.Values | ForEach-Object { '/reference:' + $_ }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$assembly = Join-Path $OutputDirectory 'ComparisonPrototypes.dll'
$files = @('ComparisonContracts.cs','ComparisonScratch.cs','ComparisonExternalSort.cs','ComparisonDiskKeys.cs',
    'ComparisonCodecs.cs','ComparisonTextHash.cs','ComparisonResultStore.cs','ComparisonCollectionEngine.cs',
    'RelationalComparisonService.Tables.cs','ComparisonContractChecks.cs','RelationalComparisonResultWindow.cs',
    'ComparisonWindowContractChecks.cs','ComparisonScratchContractChecks.cs') | ForEach-Object { Join-Path $source $_ }
$surf = Split-Path -Parent (Split-Path -Parent $source)
$files += Join-Path $surf 'Storage/Relational/Access/AccessCancellation.cs'
$files += Join-Path $surf 'Services/OwnedScratchLease.cs'
& $dotnet (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') /nologo /target:exe /langversion:latest /nullable:enable /nowarn:0436 `
    /define:RELATIONAL_COMPARISON_PROTOTYPE "/out:$assembly" $refs ("/reference:" + (Join-Path $ProductionDirectory 'Surf2.dll')) `
    $files (Join-Path $PSScriptRoot 'ComparisonPrototypes.cs')
if ($LASTEXITCODE -ne 0) { throw 'Comparison prototype compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ComparisonPrototypes.runtimeconfig.json') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $ProductionDirectory 'Surf2.dll') -Destination $OutputDirectory
& $dotnet $assembly (Join-Path $surf 'App.xaml')
if ($LASTEXITCODE -ne 0) { throw 'Comparison prototypes failed.' }
if ($CompileSqlSuite) {
    # Compile the fixture suite only; never invoke it or build a project reference.
    $regression = Join-Path (Split-Path -Parent $surf) 'Surf2.RegressionTests'
    $cases = Get-ChildItem -LiteralPath (Join-Path $regression 'StorageCases') -Filter '*.cs' |
        ForEach-Object { $_.FullName }
    $productionRefs = Get-ChildItem -LiteralPath $ProductionDirectory -Filter '*.dll' |
        Where-Object { $_.Name -ne 'Surf2.RegressionTests.dll' } |
        ForEach-Object { '/reference:' + $_.FullName }
    & $dotnet (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') /nologo /target:library /langversion:latest /nullable:enable `
        /define:RELATIONAL_COMPARISON_SQL_COMPILE ("/out:" + (Join-Path $OutputDirectory 'RuntimeSuiteCompilation.dll')) `
        $refs $productionRefs $cases (Join-Path $regression 'StorageRegressionSuite.cs') (Join-Path $PSScriptRoot 'RuntimeSuiteGlobals.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Fixture suite compilation failed; no SQL was run.' }
}
