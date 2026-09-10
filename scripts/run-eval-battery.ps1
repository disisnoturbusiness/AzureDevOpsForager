<#
.SYNOPSIS
  A query set built to DISCRIMINATE between retrieval stacks, not to demo well.

.DESCRIPTION
  The earlier six-question set was chosen on 2026-07-17 to look good in a demo video -
  "see what actually comes back strong". Every one of those questions contained words that
  appear in its own answer's filename ("how does the app send EMAIL" -> EMAILSender.cs), so
  plain keyword matching could win them outright and no embedder was ever really tested.

  Here the SEMANTIC questions are constructed so that NO word in the question appears in the
  answer's filename. The script prints that overlap check per question, so the claim is
  verifiable rather than asserted. If a question ever shows overlap, it is a bad question.

  Sections:
    SEMANTIC   - 14 questions, zero filename overlap. This is the part that tests the embedder.
    IDENTIFIER -  4 exact class names. Keyword SHOULD win these; they are the control that
                  proves the lexical leg still works.
    CONTROL    -  2 questions the corpus cannot answer. Every result is a false positive, so
                  the RESULT COUNT is the score-threshold signal.

.PARAMETER Site
  http://localhost:8010 for the local stack, or the hosted demo URL.

.NOTES
  Run the SAME set against both stacks and diff. Nothing here is tuned to either one.
#>
param(
  [string]$Site  = "http://localhost:8010",
  [string]$Label = "unlabelled",
  [string]$Out   = ""
)

$ErrorActionPreference = 'Stop'

$semantic = @(
  @{ Q = "what stops someone paying with nothing in their cart";              A = "EmptyBasketOnCheckoutException.cs" }
  @{ Q = "what happens to a guest's items when they sign in";                 A = "TransferBasket.cs" }
  @{ Q = "how does the site show a temporary popup message";                  A = "ToastComponent.cs" }
  @{ Q = "how does it decide a picture upload is allowed";                    A = "ImageValidators.cs" }
  @{ Q = "what keeps repeated lookups from hitting the database every time";  A = "CacheHelpers.cs" }
  @{ Q = "what fills the store with starting products";                       A = "CatalogContextSeed.cs" }
  @{ Q = "what tells us the storefront is still reachable";                   A = "HomePageHealthCheck.cs" }
  @{ Q = "what turns an unhandled failure into a clean api response";         A = "ExceptionMiddleware.cs" }
  @{ Q = "how is a bearer credential produced after sign in";                 A = "IdentityTokenClaimService.cs" }
  @{ Q = "where does a customer see everything they bought before";           A = "GetMyOrdersHandler.cs" }
  @{ Q = "how is a long product list broken up for display";                  A = "CatalogFilterPaginatedSpecification.cs" }
  @{ Q = "what runs when a shopper ends their session";                       A = "Logout.cshtml.cs" }
  @{ Q = "where does someone create a new login";                             A = "Register.cshtml.cs" }
  @{ Q = "how does the browser client know who is currently signed in";       A = "CustomAuthStateProvider.cs" }
)

$identifier = @(
  @{ Q = "OrderBuilder";         A = "OrderBuilder.cs" }
  @{ Q = "ExceptionMiddleware";  A = "ExceptionMiddleware.cs" }
  @{ Q = "CacheHelpers";         A = "CacheHelpers.cs" }
  @{ Q = "BasketQueryService";   A = "BasketQueryService.cs" }
)

$control = @(
  "how many bits in a byte",
  "what is the boiling point of water in kelvin"
)

$lines = New-Object System.Collections.Generic.List[string]
if ($Out) { Set-Content -Path $Out -Value "" -Encoding utf8 }
# Append to $Out as we go: a 20-question run takes ~40 min on CPU and an interrupted
# session must not lose completed work.
function Emit([string]$s) {
  Write-Host $s
  $lines.Add($s)
  if ($script:Out) { Add-Content -Path $script:Out -Value $s -Encoding utf8 }
}

# Words ignored when checking question-vs-filename overlap.
$stop = @('what','how','where','when','who','why','is','a','an','the','of','in','on','to','for','with',
          'does','do','it','they','their','them','are','and','from','into','after','before','up','out',
          'still','every','time','someone','something','us','we','i','my','be','get','got','new','know',
          'currently','runs','run','see','show','shows','make','makes')

function Get-Overlap([string]$question, [string]$file) {
  $fileTokens = ([regex]::Matches(($file -replace '\.cs$|\.cshtml$',''), '[A-Z]?[a-z]+|[A-Z]+(?![a-z])') |
                 ForEach-Object { $_.Value.ToLower() })
  $qTokens = ($question.ToLower() -replace "[^a-z ]", " ") -split '\s+' | Where-Object { $_ -and $stop -notcontains $_ }
  $hit = @($qTokens | Where-Object { $fileTokens -contains $_ })
  return $hit
}

function Invoke-Q($question, $expected, [string]$kind) {
  $body = @{ question = $question; nResults = 5 } | ConvertTo-Json -Compress
  $sw = [Diagnostics.Stopwatch]::StartNew()
  try {
    $r = Invoke-RestMethod -Uri "$Site/query" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 300
  } catch {
    Emit ("  Q: {0}" -f $question); Emit ("     ERROR: {0}" -f $_.Exception.Message); Emit ""
    return [pscustomobject]@{ Rank = -1; Ms = 0 }
  }
  $sw.Stop()

  $ids = @(); if ($r.ids -and $r.ids.Count -gt 0) { $ids = @($r.ids[0]) }
  # de-duplicate to FILE level: several chunks of one file are one answer, not several
  $files = @()
  foreach ($id in $ids) { if ($id) { $f = ($id -split '[\\/]')[-1]; if ($files -notcontains $f) { $files += $f } } }

  $rank = -1
  if ($expected) { $rank = [array]::IndexOf($files, $expected); if ($rank -ge 0) { $rank++ } }

  $verdict = ""
  if ($kind -eq 'control') {
    $verdict = "  <- CONTROL: {0} distinct file(s) returned, all false positives" -f $files.Count
  } elseif ($rank -eq 1) { $verdict = "  <- HIT rank 1" }
    elseif ($rank -gt 1) { $verdict = "  <- rank $rank" }
    else                 { $verdict = "  <- MISS (expected $expected)" }

  Emit ("  Q: {0}   [{1} ms]{2}" -f $question, $sw.ElapsedMilliseconds, $verdict)
  if ($expected) {
    $ov = Get-Overlap $question $expected
    if ($ov.Count -gt 0) { Emit ("     !! filename overlap: {0} - weak question" -f ($ov -join ',')) }
  }
  for ($i = 0; $i -lt [Math]::Min(5, $files.Count); $i++) { Emit ("     {0}. {1}" -f ($i+1), $files[$i]) }
  Emit ""
  return [pscustomobject]@{ Rank = $rank; Ms = $sw.ElapsedMilliseconds }
}

Emit ("=" * 80)
Emit "EVAL BATTERY  -  $Label"
Emit "site: $Site"
Emit ("run : {0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Emit ("=" * 80)
try {
  $h = Invoke-RestMethod -Uri "$Site/health" -TimeoutSec 300
  Emit ("health: status={0} files={1} vectors={2} vectorStatus={3}" -f $h.status,$h.ftsFileCount,$h.vectorPointCount,$h.vectorStatus)
} catch { Emit ("health check failed: " + $_.Exception.Message) }

Emit ""
Emit "--- SEMANTIC (14) - no question word appears in the answer filename ---"
Emit ""
$semResults = @()
foreach ($item in $semantic) { $semResults += (Invoke-Q $item.Q $item.A 'semantic') }

Emit "--- IDENTIFIER (4) - lexical control, keyword should win ---"
Emit ""
$idResults = @()
foreach ($item in $identifier) { $idResults += (Invoke-Q $item.Q $item.A 'identifier') }

Emit "--- CONTROL (2) - corpus cannot answer these ---"
Emit ""
foreach ($q in $control) { [void](Invoke-Q $q $null 'control') }

function Score($rows, $name) {
  $r1  = @($rows | Where-Object { $_.Rank -eq 1 }).Count
  $r5  = @($rows | Where-Object { $_.Rank -ge 1 }).Count
  $mis = @($rows | Where-Object { $_.Rank -lt 1 }).Count
  $mrr = 0.0; foreach ($x in $rows) { if ($x.Rank -ge 1) { $mrr += 1.0 / $x.Rank } }
  $mrr = if ($rows.Count) { $mrr / $rows.Count } else { 0 }
  $ms  = ($rows | Measure-Object -Property Ms -Average).Average
  Emit ("{0,-12} n={1,-3} rank1={2,-3} top5={3,-3} miss={4,-3} MRR={5:N3}  avg={6:N0} ms" -f $name,$rows.Count,$r1,$r5,$mis,$mrr,$ms)
}

Emit ("=" * 80)
Emit "SCORE"
Score $semResults "SEMANTIC"
Score $idResults  "IDENTIFIER"
Emit ("=" * 80)

if ($Out) { Write-Host "`ntranscript -> $Out" }
