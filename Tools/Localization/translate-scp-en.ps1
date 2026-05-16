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
$createdFiles = 0
$missingFiles = 0

foreach ($relativeRoot in $relativeRoots) {
    $sourceRoot = Join-Path $sourceCultureRoot $relativeRoot
    $targetRoot = Join-Path $targetCultureRoot $relativeRoot
    $sourceRootFull = (Resolve-Path $sourceRoot).Path.TrimEnd("\", "/")
    $sourceFiles = @(Get-ChildItem -Path $sourceRoot -Recurse -Filter "*.ftl" -File | Sort-Object FullName)

    foreach ($sourceFile in $sourceFiles) {
        $relativePath = $sourceFile.FullName.Substring($sourceRootFull.Length).TrimStart("\", "/")
        $targetFile = Join-Path $targetRoot $relativePath

        if (-not (Test-Path $targetFile)) {
            if ($DryRun) {
                $missingFiles += 1
                continue
            }

            $targetDirectory = Split-Path -Parent $targetFile
            New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
            Copy-Item -Path $sourceFile.FullName -Destination $targetFile
            $createdFiles += 1
        }

        $targetFiles.Add($targetFile)
    }
}

if ($DryRun -and $missingFiles -gt 0) {
    Write-Host "Dry run skipped $missingFiles missing en-US target file(s). Run without -DryRun to create them from ru-RU sources."
}

if ($createdFiles -gt 0) {
    Write-Host "Created $createdFiles missing en-US target file(s) from ru-RU SCP sources."
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
