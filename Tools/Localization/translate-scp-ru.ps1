param(
    [switch] $DryRun,
    [int] $BatchSize = 100
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($BatchSize -lt 1) {
    throw "BatchSize must be 1 or greater."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$runner = Join-Path $repoRoot "Tools\Localization\run.py"
$localeRoots = @(
    (Join-Path $repoRoot "Resources\Locale\ru-RU\_strings\_scp"),
    (Join-Path $repoRoot "Resources\Locale\ru-RU\_prototypes\_scp")
)

foreach ($localeRoot in $localeRoots) {
    if (-not (Test-Path $localeRoot)) {
        throw "Locale directory not found: $localeRoot"
    }
}

$files = @(Get-ChildItem -Path $localeRoots -Recurse -Filter "*.ftl" -File |
    Sort-Object FullName |
    Select-Object -ExpandProperty FullName)

if ($files.Count -eq 0) {
    Write-Host "No .ftl files found in SCP ru-RU locale roots."
    exit 0
}

$extraArgs = @()
if ($DryRun) {
    $extraArgs += "--dry-run"
}

Push-Location $repoRoot
try {
    for ($index = 0; $index -lt $files.Count; $index += $BatchSize) {
        $end = [Math]::Min($index + $BatchSize - 1, $files.Count - 1)
        $batch = $files[$index..$end]
        Write-Host "Translating _scp ru-RU files $($index + 1)-$($end + 1) of $($files.Count)..."
        python $runner translate @batch @extraArgs
    }
}
finally {
    Pop-Location
}
