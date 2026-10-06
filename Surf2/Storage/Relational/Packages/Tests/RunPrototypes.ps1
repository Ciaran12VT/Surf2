$ErrorActionPreference = 'Stop'
$packages = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet).Source
$root = Split-Path -Parent $dotnet
$sdk = Get-ChildItem -LiteralPath (Join-Path $root 'sdk') -Directory |
    Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$pack = Get-ChildItem -LiteralPath (Join-Path $root 'packs/Microsoft.NETCore.App.Ref') -Directory |
    Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$scriptDom = Join-Path $env:USERPROFILE '.nuget/packages/microsoft.sqlserver.transactsql.scriptdom/180.18.1/lib/netstandard2.1/Microsoft.SqlServer.TransactSql.ScriptDom.dll'
if (-not $sdk -or -not $pack -or -not (Test-Path -LiteralPath $scriptDom)) {
    throw 'The installed .NET 10 SDK/reference pack and existing restored ScriptDom test dependency are required.'
}
$output = Join-Path $packages '.prototype-artifacts'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$references = Get-ChildItem -LiteralPath (Join-Path $pack.FullName 'ref/net10.0') -Filter '*.dll' |
    ForEach-Object { '/reference:' + $_.FullName }
$sources = @('PackageContracts.cs', 'PackageTableCatalogue.cs', 'PackageRowEncoding.cs', 'PackageHashingStream.cs', 'PackageLocalFiles.cs', 'PackageIsolation.cs') |
    ForEach-Object { Join-Path $packages $_ }
$assembly = Join-Path $output 'PackagePrototypes.dll'
& $dotnet (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') /nologo /target:exe /langversion:latest /nullable:enable /define:PACKAGE_PROTOTYPE "/out:$assembly" $references "/reference:$scriptDom" $sources (Join-Path $PSScriptRoot 'PackagePrototypes.cs')
if ($LASTEXITCODE -ne 0) { throw 'Package prototype compilation failed.' }
Copy-Item -LiteralPath $scriptDom -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PackagePrototypes.runtimeconfig.json') -Destination $output
& $dotnet $assembly (Join-Path (Split-Path -Parent $packages) 'Schema')
if ($LASTEXITCODE -ne 0) { throw 'Package prototypes failed.' }
