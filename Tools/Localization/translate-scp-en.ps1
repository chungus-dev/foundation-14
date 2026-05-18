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
$sourceCultureRoot = Join-Path $repoRoot "Resources\Locale\ru-RU"
$targetCultureRoot = Join-Path $repoRoot "Resources\Locale\en-US"
$relativeRoots = @(
    "_strings\_scp",
    "_prototypes\_scp"
)

foreach ($relativeRoot in $relativeRoots) {
    $sourceRoot = Join-Path $sourceCultureRoot $relativeRoot
    if (-not (Test-Path $sourceRoot)) {
        throw "Source locale directory not found: $sourceRoot"
    }
}

$targetFiles = New-Object System.Collections.Generic.List[string]
$prepareReport = Join-Path ([System.IO.Path]::GetTempPath()) ("foundation-loc-prepare-en-" + [System.Guid]::NewGuid().ToString("N") + ".json")
$prepareArgs = @(
    "prepare-target-files",
    "--source-culture-root", $sourceCultureRoot,
    "--target-culture-root", $targetCultureRoot,
    "--report-json", $prepareReport
)

foreach ($relativeRoot in $relativeRoots) {
    $prepareArgs += @("--relative-root", $relativeRoot)
}

if ($DryRun) {
    $prepareArgs += "--dry-run"
}

Write-Host "Preparing en-US SCP target files..."
$prepareOutput = python $runner @prepareArgs
if ($LASTEXITCODE -ne 0) {
    throw "Failed to prepare en-US target files."
}
Write-Host $prepareOutput

try {
    $report = Get-Content -Path $prepareReport -Raw | ConvertFrom-Json
    foreach ($targetFile in $report.target_files) {
        $targetFiles.Add([string] $targetFile)
    }

    if ($DryRun -and $report.dry_run_missing_files -gt 0) {
        Write-Host "Dry run skipped $($report.dry_run_missing_files) missing en-US target file(s). Run without -DryRun to create files only for messages absent from the whole en-US locale."
    }

    if ($report.prepared_files -gt 0) {
        Write-Host "Prepared $($report.prepared_files) en-US target file(s) with messages absent from the whole en-US locale."
    }

    if ($report.skipped_existing_messages -gt 0) {
        Write-Host "Skipped $($report.skipped_existing_messages) SCP source file(s): their messages already exist somewhere in en-US."
    }
}
finally {
    if (Test-Path $prepareReport) {
        Remove-Item -LiteralPath $prepareReport -Force
    }
}

$files = @($targetFiles | Sort-Object)
if ($files.Count -eq 0) {
    Write-Host "No en-US SCP .ftl files found to translate."
    exit 0
}

$extraArgs = @(
    "--source-culture", "ru-RU",
    "--target-culture", "en-US"
)

if ($DryRun) {
    $extraArgs += "--dry-run"
}

Push-Location $repoRoot
try {
    for ($index = 0; $index -lt $files.Count; $index += $BatchSize) {
        $end = [Math]::Min($index + $BatchSize - 1, $files.Count - 1)
        $batch = $files[$index..$end]
        Write-Host "Translating _scp en-US files $($index + 1)-$($end + 1) of $($files.Count) from ru-RU..."
        python $runner translate @batch @extraArgs
    }
}
finally {
    Pop-Location
}
