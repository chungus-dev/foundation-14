param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$ErrorActionPreference = "Stop"

$sourceRoot = Join-Path $RepoRoot ".agents/rules"
$claudeBridgeRoot = Join-Path $RepoRoot ".claude/rules"

function Get-RuleData {
    param([string]$RulePath)

    $raw = Get-Content $RulePath -Raw
    $fmMatch = [regex]::Match($raw, "(?ms)^---\r?\n(.*?)\r?\n---")
    if (-not $fmMatch.Success) {
        throw "No YAML frontmatter in $RulePath"
    }

    $frontmatter = $fmMatch.Groups[1].Value
    $body = $raw.Substring($fmMatch.Index + $fmMatch.Length).Trim()
    $trigger = [regex]::Match($frontmatter, "(?m)^trigger:\s*(.+)$").Groups[1].Value.Trim()
    $sourceRule = [regex]::Match(
        $frontmatter,
        "(?m)^\s*source_rule:\s*`"?([^`"\r\n]+)`"?\s*$"
    ).Groups[1].Value.Trim()

    return @{
        raw = $raw
        body = $body
        trigger = $trigger
        source_rule = $sourceRule
    }
}

if (-not (Test-Path $sourceRoot)) {
    throw "Source rules path not found: $sourceRoot"
}
if (-not (Test-Path $claudeBridgeRoot)) {
    throw "Claude bridge rules path not found: $claudeBridgeRoot"
}

$errors = New-Object System.Collections.Generic.List[string]

$sourceRules = Get-ChildItem $sourceRoot -File -Filter "*.md" |
    Where-Object { $_.Name -ne "AUTHORING_POLICY.md" } |
    Sort-Object Name
$claudeBridgeRules = Get-ChildItem $claudeBridgeRoot -File -Filter "*.md" |
    Sort-Object Name

$sourceNames = @($sourceRules.Name)
$claudeBridgeNames = @($claudeBridgeRules.Name)

foreach ($source in $sourceRules) {
    $name = $source.Name
    $sourceRuleMd = $source.FullName
    $claudeBridgeRuleMd = Join-Path $claudeBridgeRoot $name

    if (-not (Test-Path $claudeBridgeRuleMd)) {
        $errors.Add("Missing Claude bridge rule for '$name'.")
        continue
    }

    $sourceData = Get-RuleData -RulePath $sourceRuleMd
    $claudeData = Get-RuleData -RulePath $claudeBridgeRuleMd

    $expectedSourceRule = "../../../.agents/rules/$name"

    if ($claudeData.trigger -ne $sourceData.trigger) {
        $errors.Add("Claude bridge trigger mismatch for '$name'.")
    }
    if ($claudeData.body -notmatch [regex]::Escape($expectedSourceRule)) {
        $errors.Add("Claude bridge reference mismatch for '$name'.")
    }
}

foreach ($bridgeName in $claudeBridgeNames) {
    if ($sourceNames -notcontains $bridgeName) {
        $errors.Add("Claude bridge rule exists without source rule: '$bridgeName'.")
    }
}

foreach ($sourceName in $sourceNames) {
    if ($claudeBridgeNames -notcontains $sourceName) {
        $errors.Add("Source rule missing in Claude bridge tree: '$sourceName'.")
    }
}

if ($errors.Count -gt 0) {
    Write-Host "Rule bridge check failed with $($errors.Count) issue(s):" -ForegroundColor Red
    foreach ($errorItem in $errors) {
        Write-Host "- $errorItem"
    }
    exit 1
}

Write-Host "Rule bridge check passed:"
Write-Host "- source rules: $($sourceRules.Count)"
Write-Host "- claude bridges: $($claudeBridgeRules.Count)"
exit 0
