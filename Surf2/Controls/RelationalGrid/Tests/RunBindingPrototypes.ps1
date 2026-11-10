$ErrorActionPreference = 'Stop'
$binding = Split-Path -Parent $PSScriptRoot
$surf = Split-Path -Parent (Split-Path -Parent $binding)
$grid = Join-Path $surf 'Services/RelationalGrid'
$dotnet = (Get-Command dotnet).Source
$root = Split-Path -Parent $dotnet
$sdk = Get-ChildItem -LiteralPath (Join-Path $root 'sdk') -Directory |
    Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem -LiteralPath (Join-Path $root 'packs/Microsoft.NETCore.App.Ref') -Directory |
    Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdk -or -not $referencePack) { throw 'The existing .NET 10 SDK/reference pack is required.' }
$output = Join-Path $binding '.prototype-artifacts'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$references = Get-ChildItem -LiteralPath (Join-Path $referencePack.FullName 'ref/net10.0') -Filter '*.dll' |
    ForEach-Object { '/reference:' + $_.FullName }
$sources = @((Join-Path $surf 'Services/OwnedScratchLease.cs'),
    (Join-Path $grid 'GridContracts.cs'), (Join-Path $binding 'GridViewportItems.cs'),
    (Join-Path $grid 'GridValues.cs'), (Join-Path $grid 'GridExternalSort.cs'),
    (Join-Path $grid 'GridDiskStore.cs'), (Join-Path $grid 'GridOwnedWorkspace.cs'),
    (Join-Path $grid 'GridSourceBase.cs'), (Join-Path $grid 'GridQuerySession.cs'))
$assembly = Join-Path $output 'BindingPrototypes.dll'
& $dotnet (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') /nologo /target:exe /langversion:latest /nullable:enable /define:RELATIONAL_GRID_BINDING_PROTOTYPE "/out:$assembly" $references $sources (Join-Path $PSScriptRoot 'BindingPrototypes.cs')
if ($LASTEXITCODE -ne 0) { throw 'Binding prototype compilation failed.' }
Copy-Item -LiteralPath (Join-Path $grid 'Tests/GridPrototypes.runtimeconfig.json') -Destination (Join-Path $output 'BindingPrototypes.runtimeconfig.json')
& $dotnet $assembly
if ($LASTEXITCODE -ne 0) { throw 'Binding prototypes failed.' }
