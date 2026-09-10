<#
.SYNOPSIS
  Runs the fixed query battery against a Forager server and prints ranked results.

.DESCRIPTION
  The same questions that were fired at the hosted demo on 2026-07-17, so the two runs are
  directly comparable. Point it at the local server (e5-large-v2, 1024-dim) or the hosted one
  (bge-code-v1, 1536-dim) and diff the output.

  Expected rank-1 answers are baked in for the semantic set, so the script scores itself.

.PARAMETER Site
  Base URL. Local:  http://localhost:8000   Hosted: https://azuredevops.aidataforager.com

.PARAMETER Label
  Free text written into the header, e.g. "e5-large-v2 local" - keeps saved output straight.

.PARAMETER Out
  Optional path to also write the transcript to.

.EXAMPLE
  .\run-query-battery.ps1 -Site http://localhost:8000 -Label "e5-large-v2 + bge-reranker (local)" -Out battery-e5.txt
  .\run-query-battery.ps1 -Site https://azuredevops.aidataforager.com -Label "bge-code-v1 + Qwen3-4B (hosted)" -Out battery-bge.txt

.NOTES
  The hosted endpoint scales to zero - the first call can take ~140 s while it wakes.
  The local server has no cold start but embeds on CPU, so expect seconds per query.
#>
param(
  [string]$Site  = "http://localhost:8000",
  [string]$Label = "unlabelled run",
  [string]$Out   = ""
)

$ErrorActionPreference = 'Stop'

# question -> expected rank-1 file (null = no single right answer, judged by eye)
$semantic = [ordered]@{
  "how does the app send email"                      = "EmailSender.cs"
  "how is the basket total calculated"               = "Basket.cs"
  "how are users authenticated at sign in"           = "AuthenticateEndpoint.cs"
  "how are catalog items filtered by brand and type" = "CatalogItemService.cs"
  "where is the database connection configured"      = $null
  "what happens when a user checks out"              = $null   # known miss on the hosted run
}
$reworded = [ordered]@{
  "create an order from the basket" = "Order.cs"
  "how is an order placed"          = "Order.cs"
  "checkout controller action"      = $null
}
$identifiers = [ordered]@{
  "EmailSender"       = "EmailSender.cs"
  "CatalogItemService"= "CatalogItemService.cs"
  "OrderService"      = "OrderService.cs"
  "IRepository"       = "IRepository.cs"
  "PaymentMethods"    = $null
}
# negative control: the corpus cannot answer this, so every result is a false positive.
# the COUNT is the calibration signal for the score threshold.
$control = @("how many bits in a byte")

$lines = New-Object System.Collections.Generic.List[string]
function Emit([string]$s) { Write-Host $s; $lines.Add($s) }

function Invoke-Q([string]$question, [string]$expected, [switch]$Control) {
  $body = @{ question = $question; nResults = 4 } | ConvertTo-Json -Compress
  $sw = [Diagnostics.Stopwatch]::StartNew()
  try {
    $r = Invoke-RestMethod -Uri "$Site/query" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 300
  } catch {
    Emit ("  Q: {0}`n     ERROR: {1}" -f $question, $_.Exception.Message)
    return
  }
  $sw.Stop()
  $ids = @()
  if ($r.ids -and $r.ids.Count -gt 0) { $ids = @($r.ids[0]) }
  # @() forces an array: a single result would otherwise collapse to a
  # scalar string, and indexing a string yields characters, not filenames.
  $files = @($ids | ForEach-Object { if ($_) { ($_ -split '[\\/]')[-1] } })

  $verdict = ""
  if ($Control) {
    $verdict = "  <- NEGATIVE CONTROL: $($files.Count) result(s); every one is a false positive"
  } elseif ($expected) {
    $verdict = if ($files.Count -gt 0 -and $files[0] -eq $expected) { "  <- HIT (rank 1)" }
               elseif ($files -contains $expected)                 { "  <- expected at rank $(( [array]::IndexOf($files,$expected) ) + 1)" }
               else                                                { "  <- MISS (expected $expected)" }
  }

  Emit ("  Q: {0}   [{1} ms]{2}" -f $question, $sw.ElapsedMilliseconds, $verdict)
  if ($files.Count -eq 0) { Emit "     (no results)" }
  else { for ($i = 0; $i -lt $files.Count; $i++) { Emit ("     {0}. {1}" -f ($i + 1), $files[$i]) } }
  Emit ""
}

Emit ("=" * 78)
Emit "QUERY BATTERY  -  $Label"
Emit "site: $Site"
Emit ("run : {0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Emit ("=" * 78)

Emit "`n--- health ---"
try {
  $h = Invoke-RestMethod -Uri "$Site/health" -TimeoutSec 300
  Emit ("  status={0}  ftsFileCount={1}  vectorPointCount={2}  vectorStatus={3}" -f $h.status, $h.ftsFileCount, $h.vectorPointCount, $h.vectorStatus)
} catch { Emit ("  health check failed: " + $_.Exception.Message) }

Emit "`n--- SEMANTIC (plain-English questions) ---"
foreach ($k in $semantic.Keys) { Invoke-Q $k $semantic[$k] }

Emit "--- REWORDED (the checkout miss, rephrased) ---"
foreach ($k in $reworded.Keys) { Invoke-Q $k $reworded[$k] }

Emit "--- EXACT IDENTIFIERS (keyword leg) ---"
foreach ($k in $identifiers.Keys) { Invoke-Q $k $identifiers[$k] }

Emit "--- NEGATIVE CONTROL (threshold calibration) ---"
foreach ($k in $control) { Invoke-Q $k $null -Control }

Emit ("=" * 78)

if ($Out) {
  $lines | Set-Content -Path $Out -Encoding utf8
  Write-Host "`ntranscript written to $Out"
}
