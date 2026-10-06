$ErrorActionPreference = 'Stop'
$capture = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet).Source
$root = Split-Path -Parent $dotnet
$sdk = Get-ChildItem -LiteralPath (Join-Path $root 'sdk') -Directory |
    Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$referencePack = Get-ChildItem -LiteralPath (Join-Path $root 'packs/Microsoft.NETCore.App.Ref') -Directory |
    Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$scriptDom = Join-Path $env:USERPROFILE '.nuget/packages/microsoft.sqlserver.transactsql.scriptdom/180.18.1/lib/netstandard2.1/Microsoft.SqlServer.TransactSql.ScriptDom.dll'
if (-not $sdk -or -not $referencePack -or -not (Test-Path -LiteralPath $scriptDom)) {
    throw 'The .NET 10 SDK/reference pack and the existing restored ScriptDom package are required.'
}
$output = Join-Path $capture '.prototype-artifacts'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$references = Get-ChildItem -LiteralPath (Join-Path $referencePack.FullName 'ref/net10.0') -Filter '*.dll' |
    ForEach-Object { '/reference:' + $_.FullName }
$sources = @('CaptureContracts.cs', 'CaptureLayout.cs', 'CaptureRowCodec.cs', 'CaptureRowFidelity.cs', 'CaptureImportColumns.cs', 'CaptureBulkBatch.cs', 'CaptureDisplay.cs', 'CaptureExternalSort.cs', 'CaptureDelimitedWriter.cs') |
    ForEach-Object { Join-Path $capture $_ }
$assembly = Join-Path $output 'CapturePrototypes.dll'
& $dotnet (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') /nologo /target:exe /langversion:latest /nullable:enable /define:CAPTURE_PROTOTYPE "/out:$assembly" $references "/reference:$scriptDom" $sources (Join-Path $PSScriptRoot 'CapturePrototypes.cs')
if ($LASTEXITCODE -ne 0) { throw 'Capture prototype compilation failed.' }
Copy-Item -LiteralPath $scriptDom -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CapturePrototypes.runtimeconfig.json') -Destination $output
$schema = Join-Path (Split-Path -Parent $capture) 'Schema/001.Capture.sql'
& $dotnet $assembly $schema
if ($LASTEXITCODE -ne 0) { throw 'Capture prototypes failed.' }
