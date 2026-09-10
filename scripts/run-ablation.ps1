<#
.SYNOPSIS
  Runs the query battery three times - hybrid, vector-only, full-text-only - to find out
  which leg is actually doing the work.

.DESCRIPTION
  The RRF proc scores each leg as  weight * (1.0 / (60 + rank)),  so setting a weight to 0
  removes that leg's contribution entirely.

  Why this matters: if FULL-TEXT ONLY scores the same as HYBRID, then the embedding model was
  never being tested by these queries, and any "model A == model B" conclusion drawn from them
  is meaningless. This is the control that should have been run before comparing embedders.

  Restarts the Server between variants because Config is read once at startup.
#>
param(
  [string]$Port    = "8010",
  [string]$RepoDir = "C:\Temp\ForClaude\AzureDevOpsForager"
)

$ErrorActionPreference = 'Stop'

$serverBin = Join-Path $RepoDir "AzureDevOpsForager.Server\bin\Debug\net8.0\win-x64"
$cfgPath   = Join-Path $serverBin "config.json"
$exe       = Join-Path $serverBin "AzureDevOpsForager.Server.exe"
$battery   = Join-Path $RepoDir "scripts\run-query-battery.ps1"
$site      = "http://localhost:$Port"

$original = Get-Content $cfgPath -Raw

$variants = @(
  @{ Name = "HYBRID (baseline)";   Vec = 60; Chunk = 30; File = 30 },
  @{ Name = "VECTOR ONLY";         Vec = 60; Chunk =  0; File =  0 },
  @{ Name = "FULL-TEXT ONLY";      Vec =  0; Chunk = 30; File = 30 }
)

function Restart-Server {
  Get-Process -Name "AzureDevOpsForager.Server" -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Seconds 2
  $log = Join-Path $RepoDir "server-local.log"
  Start-Process -FilePath $exe -WorkingDirectory $serverBin -RedirectStandardOutput $log -RedirectStandardError "$log.err" -WindowStyle Hidden
  $wc = New-Object System.Net.WebClient
  foreach ($i in 1..40) {
    Start-Sleep -Seconds 3
    try { [void]$wc.DownloadString("$site/health"); return $true } catch { }
  }
  return $false
}

try {
  foreach ($v in $variants) {
    Write-Host ""
    Write-Host ("#" * 78)
    Write-Host ("#  {0}    vector={1}  chunkFts={2}  fileFts={3}" -f $v.Name, $v.Vec, $v.Chunk, $v.File)
    Write-Host ("#" * 78)

    $cfg = $original
    $cfg = $cfg -replace '"RrfVectorWeight":\s*"\d+"',   ('"RrfVectorWeight": "{0}"'   -f $v.Vec)
    $cfg = $cfg -replace '"RrfChunkFtsWeight":\s*"\d+"', ('"RrfChunkFtsWeight": "{0}"' -f $v.Chunk)
    $cfg = $cfg -replace '"RrfFileFtsWeight":\s*"\d+"',  ('"RrfFileFtsWeight": "{0}"'  -f $v.File)
    Set-Content -Path $cfgPath -Value $cfg -Encoding utf8

    if (-not (Restart-Server)) { Write-Host "  SERVER DID NOT COME UP - skipping variant"; continue }

    $slug = ($v.Name -replace '[^A-Za-z0-9]+','-').Trim('-').ToLower()
    $out  = Join-Path $RepoDir ("battery-ablation-{0}.txt" -f $slug)
    & $battery -Site $site -Label ("e5 local - {0}" -f $v.Name) -Out $out | Out-Null

    # summarise: count HIT lines and show the rank-1 file per query
    $lines = Get-Content $out
    $hits  = ($lines | Select-String -SimpleMatch "<- HIT (rank 1)").Count
    $miss  = ($lines | Select-String -SimpleMatch "<- MISS").Count
    Write-Host ("  RESULT: {0} rank-1 hits, {1} misses    -> {2}" -f $hits, $miss, (Split-Path -Leaf $out))
  }
}
finally {
  Set-Content -Path $cfgPath -Value $original -Encoding utf8
  Write-Host ""
  Write-Host "config restored to hybrid defaults; restarting server one last time..."
  [void](Restart-Server)
}
