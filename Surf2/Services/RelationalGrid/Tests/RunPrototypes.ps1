$ErrorActionPreference = 'Stop'
$grid = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet).Source
$root = Split-Path -Parent $dotnet
$sdk = Get-ChildItem -LiteralPath (Join-Path $root 'sdk') -Directory |
    Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem -LiteralPath (Join-Path $root 'packs/Microsoft.NETCore.App.Ref') -Directory |
    Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdk -or -not $referencePack) { throw 'The existing .NET 10 SDK/reference pack is required.' }
$output = Join-Path $grid '.prototype-artifacts'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$references = Get-ChildItem -LiteralPath (Join-Path $referencePack.FullName 'ref/net10.0') -Filter '*.dll' |
    ForEach-Object { '/reference:' + $_.FullName }
$sources = Get-ChildItem -LiteralPath $grid -Filter '*.cs' |
    Where-Object { $_.Name -ne 'CapturedGridSource.cs' } | ForEach-Object { $_.FullName }
$surf = Split-Path -Parent (Split-Path -Parent $grid)
$sources += @((Join-Path $surf 'Services/OwnedScratchLease.cs'),
    (Join-Path $surf 'Storage/Relational/Capture/CaptureDisplay.cs'),
    (Join-Path $surf 'Storage/Relational/Capture/CaptureContracts.cs'),
    (Join-Path $surf 'Storage/Relational/Capture/CaptureLayout.cs'),
    (Join-Path $surf 'Services/CsvGridParser.cs'), (Join-Path $surf 'Models/CsvGridRow.cs'))
$assembly = Join-Path $output 'GridPrototypes.dll'
& $dotnet (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') /nologo /target:exe /langversion:latest /nullable:enable /define:RELATIONAL_GRID_PROTOTYPE "/out:$assembly" $references $sources (Join-Path $PSScriptRoot 'GridPrototypes.cs')
if ($LASTEXITCODE -ne 0) { throw 'Grid prototype compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'GridPrototypes.runtimeconfig.json') -Destination $output
& $dotnet $assembly
if ($LASTEXITCODE -ne 0) { throw 'Grid prototypes failed.' }
