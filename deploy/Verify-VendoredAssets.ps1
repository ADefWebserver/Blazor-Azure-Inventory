<#
.SYNOPSIS
  Verifies the SHA-256 of the vendored client-side files listed in vendored-assets.sha256.
.DESCRIPTION
  Fails when a listed file is missing or changed, when a file exists under a vendored
  library folder but is not listed, or when the removed vis-network folder comes back.
  Run it locally or in CI. It never restores files from a CDN.
#>
[CmdletBinding()]
param(
    [string]$Manifest = (Join-Path $PSScriptRoot 'vendored-assets.sha256')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]
$listed = @{}

foreach ($line in Get-Content -LiteralPath $Manifest) {
    if ($line -match '^\s*(#|$)') { continue }
    if ($line -notmatch '^(?<hash>[0-9a-fA-F]{64})  (?<path>\S.*)$') {
        $failures.Add("Malformed manifest line: $line")
        continue
    }
    $relative = $Matches['path']
    $listed[$relative] = $true
    $full = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        $failures.Add("Missing: $relative")
        continue
    }
    $actual = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
    if ($actual -ne $Matches['hash'].ToUpperInvariant()) {
        $failures.Add("Hash mismatch: $relative (expected $($Matches['hash']), actual $($actual.ToLowerInvariant()))")
    }
}

foreach ($folder in 'cytoscape', 'fluent-icons') {
    $base = Join-Path $root "BlazorAzureInventory/wwwroot/lib/$folder"
    if (-not (Test-Path -LiteralPath $base)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File) {
        $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'
        if (-not $listed.ContainsKey($relative)) { $failures.Add("Not in manifest: $relative") }
    }
}

if (Test-Path -LiteralPath (Join-Path $root 'BlazorAzureInventory/wwwroot/lib/vis-network')) {
    $failures.Add('wwwroot/lib/vis-network must not exist (Cytoscape.js is the only graph library).')
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}

Write-Host "Vendored assets verified: $($listed.Count) files."
