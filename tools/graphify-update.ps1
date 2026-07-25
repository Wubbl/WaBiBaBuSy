<#
.SYNOPSIS
    Rebuild the graphify knowledge graph deterministically.

.DESCRIPTION
    Wrapper around `graphify update .` that pins PYTHONHASHSEED.

    Why this exists: graphify clusters the graph with NetworkX Louvain. Louvain is
    called with a fixed seed, but it iterates over Python sets of node-id strings,
    and CPython randomizes string hashing per process. So every rebuild produced a
    different community numbering — identical nodes, identical edges, but ~70% of
    community IDs renumbered. graph.json and GRAPH_REPORT.md are generated from
    those IDs, so a no-op rebuild rewrote ~31k lines and every build showed up as a
    huge meaningless diff.

    Pinning PYTHONHASHSEED makes the partition reproducible: back-to-back rebuilds
    are byte-identical, so the graph only changes when the code actually changes.

    The installed git hooks (.git/hooks/post-commit, post-checkout) export the same
    variable. Those live outside version control, so re-run `graphify install` +
    this script's -PatchHooks switch if they are ever reinstalled.

.PARAMETER Force
    Pass --force to graphify, bypassing its "fewer nodes than before" safety check.
    Needed after refactors that legitimately delete code.

.PARAMETER Fresh
    Delete graph.json before rebuilding. `graphify update` unions new nodes into the
    existing graph rather than replacing it, so nodes for files that are no longer
    indexed survive a plain rebuild. Use this after changing .graphifyignore.

.PARAMETER PatchHooks
    Re-add the PYTHONHASHSEED export to .git/hooks/post-commit and post-checkout,
    then exit without rebuilding. Idempotent.

.EXAMPLE
    ./tools/graphify-update.ps1
.EXAMPLE
    ./tools/graphify-update.ps1 -Fresh -Force
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$Fresh,
    [switch]$PatchHooks
)

$ErrorActionPreference = 'Stop'

# Keep this in sync with the value exported by the git hooks.
$HashSeed = '0'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Add-HookSeed {
    param([string]$HookPath)

    if (-not (Test-Path $HookPath)) {
        Write-Host "  skip $(Split-Path -Leaf $HookPath) (not installed)"
        return
    }

    $lines = Get-Content $HookPath
    if ($lines -match '^export PYTHONHASHSEED=') {
        Write-Host "  ok   $(Split-Path -Leaf $HookPath) (already pinned)"
        return
    }

    # Insert after the shebang so the export precedes any interpreter launch.
    $seedBlock = @(
        '',
        '# Pin string-hash randomization so Louvain community numbering is reproducible',
        '# and graph.json/GRAPH_REPORT.md only change when the code changes.',
        '# See tools/graphify-update.ps1.',
        "export PYTHONHASHSEED=$HashSeed"
    )
    $patched = @($lines[0]) + $seedBlock + $lines[1..($lines.Count - 1)]
    Set-Content -Path $HookPath -Value $patched -NoNewline:$false
    Write-Host "  set  $(Split-Path -Leaf $HookPath)"
}

if ($PatchHooks) {
    Write-Host 'Pinning PYTHONHASHSEED in git hooks:'
    Add-HookSeed (Join-Path $repoRoot '.git/hooks/post-commit')
    Add-HookSeed (Join-Path $repoRoot '.git/hooks/post-checkout')
    exit 0
}

$graphJson = Join-Path $repoRoot 'graphify-out/graph.json'
if ($Fresh -and (Test-Path $graphJson)) {
    Write-Host 'Removing graph.json for a from-scratch rebuild...'
    Remove-Item $graphJson
}

$graphifyArgs = @('update', $repoRoot)
if ($Force -or $Fresh) { $graphifyArgs += '--force' }  # a fresh build has fewer nodes by definition

$previousSeed = $env:PYTHONHASHSEED
$env:PYTHONHASHSEED = $HashSeed
try {
    & graphify @graphifyArgs
    $code = $LASTEXITCODE
}
finally {
    if ($null -eq $previousSeed) { Remove-Item Env:PYTHONHASHSEED -ErrorAction SilentlyContinue }
    else { $env:PYTHONHASHSEED = $previousSeed }
}

exit $code
