param(
    [ValidateSet('master', 'catalogue', 'storage', 'capture', 'workspace', 'compatibility')]
    [string[]] $Suites = @('master', 'catalogue', 'storage', 'capture', 'workspace', 'compatibility'),
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repo
try {
    if (-not $SkipBuild) {
        & dotnet build Surf2.slnx --no-restore -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'V2 build failed.' }
    }
    $runner = Join-Path $repo 'Surf2.RegressionTests/bin/Debug/net10.0-windows/Surf2.RegressionTests.dll'
    $logDirectory = Join-Path (Split-Path -Parent $runner) 'verification-logs'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    foreach ($suite in $Suites) {
        Write-Host "Running V2 verification: $suite"
        $suiteArguments = @(switch ($suite) {
            'master' { @() }
            'catalogue' { @('--v2-catalogue') }
            'storage' { @('--v2-storage') }
            'capture' { @('--v2-capture') }
            'workspace' { @('--v2-workspace') }
            'compatibility' { @('--storage', '--local', '--legacy-package', '--package-export', '--package-import', '--pasted-freeze', '--raw-text', '--runtime-capture') }
        })
        $logPath = Join-Path $logDirectory ($suite + '.log')
        & dotnet $runner @suiteArguments 2>&1 | Tee-Object -FilePath $logPath |
            Where-Object { "$_" -notmatch '^PASS[: ]' }
        if ($LASTEXITCODE -ne 0) { throw "V2 verification failed: $suite" }
    }
    Write-Host 'Requested V2 verification suites passed.'
}
finally { Pop-Location }
