param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$ErrorActionPreference = "Stop"

$sourceRoot = Join-Path $RepoRoot ".agents/skills"
$claudeBridgeRoot = Join-Path $RepoRoot ".claude/skills"

function Get-SkillData {
    param([string]$SkillMdPath)

    $raw = Get-Content $SkillMdPath -Raw
    $fmMatch = [regex]::Match($raw, "(?ms)^---\r?\n(.*?)\r?\n---")
    if (-not $fmMatch.Success) {
        throw "No YAML frontmatter in $SkillMdPath"
    }

    $frontmatter = $fmMatch.Groups[1].Value
    $body = $raw.Substring($fmMatch.Index + $fmMatch.Length).Trim()
    $name = [regex]::Match($frontmatter, "(?m)^name:\s*(.+)$").Groups[1].Value.Trim()
    $description = [regex]::Match($frontmatter, "(?m)^description:\s*(.+)$").Groups[1].Value.Trim()
    $sourceSkill = [regex]::Match(
        $frontmatter,
        "(?m)^\s*source_skill:\s*`"?([^`"\r\n]+)`"?\s*$"
    ).Groups[1].Value.Trim()

    return @{
        raw = $raw
        body = $body
        name = $name
        description = $description
        source_skill = $sourceSkill
    }
}

if (-not (Test-Path $sourceRoot)) {
    throw "Source skills path not found: $sourceRoot"
}
if (-not (Test-Path $claudeBridgeRoot)) {
    throw "Claude bridge skills path not found: $claudeBridgeRoot"
}

$errors = New-Object System.Collections.Generic.List[string]

$sourceSkills = Get-ChildItem $sourceRoot -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "SKILL.md") } |
    Sort-Object Name
$claudeBridgeSkills = Get-ChildItem $claudeBridgeRoot -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "SKILL.md") } |
    Sort-Object Name

$sourceNames = @($sourceSkills.Name)
$claudeBridgeNames = @($claudeBridgeSkills.Name)

foreach ($source in $sourceSkills) {
    $name = $source.Name
    $sourceSkillMd = Join-Path $source.FullName "SKILL.md"
    $claudeBridgeSkillMd = Join-Path $claudeBridgeRoot "$name/SKILL.md"

    if (-not (Test-Path $claudeBridgeSkillMd)) {
        $errors.Add("Missing Claude bridge SKILL.md for '$name'.")
        continue
    }

    $sourceData = Get-SkillData -SkillMdPath $sourceSkillMd
    $claudeData = Get-SkillData -SkillMdPath $claudeBridgeSkillMd

    $expectedSourceSkill = "../../../.agents/skills/$name/SKILL.md"

    if ($claudeData.name -ne $name) {
        $errors.Add("Claude bridge name mismatch for '$name': '$($claudeData.name)'")
    }
    if ($claudeData.description -ne $sourceData.description) {
        $errors.Add("Claude bridge description mismatch for '$name'.")
    }
    if ($claudeData.body -notmatch [regex]::Escape($expectedSourceSkill)) {
        $errors.Add("Claude bridge reference mismatch for '$name'.")
    }
}

foreach ($bridgeName in $claudeBridgeNames) {
    if ($sourceNames -notcontains $bridgeName) {
        $errors.Add("Claude bridge exists without source skill: '$bridgeName'.")
    }
}

foreach ($sourceName in $sourceNames) {
    if ($claudeBridgeNames -notcontains $sourceName) {
        $errors.Add("Source skill missing in Claude bridge tree: '$sourceName'.")
    }
}

if ($errors.Count -gt 0) {
    Write-Host "Bridge check failed with $($errors.Count) issue(s):" -ForegroundColor Red
    foreach ($errorItem in $errors) {
        Write-Host "- $errorItem"
    }
    exit 1
}

Write-Host "Bridge check passed:"
Write-Host "- source skills: $($sourceSkills.Count)"
Write-Host "- claude bridges: $($claudeBridgeSkills.Count)"
exit 0
