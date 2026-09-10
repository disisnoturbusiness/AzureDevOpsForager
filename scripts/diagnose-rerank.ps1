<#
.SYNOPSIS
  Splits the eval score into RETRIEVAL failure vs RERANKER failure.

.DESCRIPTION
  run-eval-battery.ps1 reports only the FINAL top-5 after the cross-encoder has reordered
  things, so a miss there is ambiguous: either the hybrid retrieval never surfaced the right
  file, or it did and the reranker pushed it out.

  This calls dbo.SearchCode directly with the SAME weights the server uses and asks for the
  full pool the reranker receives (RerankerInputSize). For each question it prints where the
  expected file sat BEFORE reranking, next to where it ended up AFTER.

    retrieval <= 5, final MISS   -> the RERANKER demoted a correct answer
    retrieval in 6..pool         -> retrieval had it; reranker failed to promote it
    retrieval = none             -> RETRIEVAL never found it; embedder/corpus problem

  Nothing here changes configuration and the reranker is not touched.
#>
param(
  [string]$Site = "http://localhost:8010",
  [int]$Pool    = 30,
  [string]$Out  = ""
)
$ErrorActionPreference = 'Stop'

# question -> expected file -> FINAL rank measured by run-eval-battery.ps1 (-1 = not in top 5)
$rows = @(
  @{ Q="what stops someone paying with nothing in their cart";             A="EmptyBasketOnCheckoutException.cs";     F=1 }
  @{ Q="what happens to a guest's items when they sign in";                A="TransferBasket.cs";                     F=-1 }
  @{ Q="how does the site show a temporary popup message";                 A="ToastComponent.cs";                     F=4 }
  @{ Q="how does it decide a picture upload is allowed";                   A="ImageValidators.cs";                    F=2 }
  @{ Q="what keeps repeated lookups from hitting the database every time"; A="CacheHelpers.cs";                       F=-1 }
  @{ Q="what fills the store with starting products";                      A="CatalogContextSeed.cs";                 F=1 }
  @{ Q="what tells us the storefront is still reachable";                  A="HomePageHealthCheck.cs";                F=-1 }
  @{ Q="what turns an unhandled failure into a clean api response";        A="ExceptionMiddleware.cs";                F=-1 }
  @{ Q="how is a bearer credential produced after sign in";                A="IdentityTokenClaimService.cs";          F=2 }
  @{ Q="where does a customer see everything they bought before";          A="GetMyOrdersHandler.cs";                 F=-1 }
  @{ Q="how is a long product list broken up for display";                 A="CatalogFilterPaginatedSpecification.cs";F=-1 }
  @{ Q="what runs when a shopper ends their session";                      A="Logout.cshtml.cs";                      F=-1 }
  @{ Q="where does someone create a new login";                            A="Register.cshtml.cs";                    F=1 }
  @{ Q="how does the browser client know who is currently signed in";      A="CustomAuthStateProvider.cs";            F=-1 }
)

$lines = New-Object System.Collections.Generic.List[string]
function Emit([string]$s) { Write-Host $s; $lines.Add($s) }

function Get-Vector([string]$q) {
  $body = @{ text = $q; kind = "query" } | ConvertTo-Json -Compress
  $emb  = Invoke-RestMethod -Uri "$Site/embed" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 300
  $vec  = $emb
  if ($emb -is [System.Management.Automation.PSCustomObject]) {
    foreach ($n in 'vector','embedding','data','values') {
      if ($emb.PSObject.Properties.Name -contains $n) { $vec = $emb.$n; break }
    }
  }
  if ($vec -and $vec[0] -is [System.Array]) { $vec = $vec[0] }
  if (@($vec).Count -eq 0) { throw "no vector returned from /embed" }
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.Append('[')
  for ($k=0; $k -lt @($vec).Count; $k++) {
    if ($k -gt 0) { [void]$sb.Append(',') }
    [void]$sb.Append([System.Convert]::ToDouble($vec[$k]).ToString('R',[System.Globalization.CultureInfo]::InvariantCulture))
  }
  [void]$sb.Append(']')
  return $sb.ToString()
}

# file-level rank of $expected in dbo.SearchCode output under the given leg weights
function Get-RetrievalRank($conn, [string]$vecJson, [string]$q, [string]$expected, [int]$vw, [int]$cfw, [int]$ffw) {
  $cmd = $conn.CreateCommand()
  $cmd.CommandTimeout = 180
  $cmd.CommandText = @"
DECLARE @qv VECTOR(1024) = CAST(@vecjson AS VECTOR(1024));
EXEC dbo.SearchCode @SearchText=@txt, @QueryVector=@qv, @TopN=@top, @ChunkType=NULL,
                    @VectorWeight=@vw, @ChunkFtsWeight=@cfw, @FileFtsWeight=@ffw,
                    @MinFtsRank=10, @MaxDistance=2.0;
"@
  [void]$cmd.Parameters.AddWithValue("@vecjson", $vecJson)
  [void]$cmd.Parameters.AddWithValue("@txt", $q)
  [void]$cmd.Parameters.AddWithValue("@top", $Pool)
  [void]$cmd.Parameters.AddWithValue("@vw",  $vw)
  [void]$cmd.Parameters.AddWithValue("@cfw", $cfw)
  [void]$cmd.Parameters.AddWithValue("@ffw", $ffw)
  $rd = $cmd.ExecuteReader()
  $files = @()
  while ($rd.Read()) {
    $f = ([string]$rd['FilePath'] -split '[\/]')[-1]
    if ($files -notcontains $f) { $files += $f }
  }
  $rd.Close()
  $r = [array]::IndexOf($files, $expected)
  if ($r -ge 0) { return $r + 1 } else { return -1 }
}

function Fmt([int]$r) { if ($r -lt 1) { return "  -" } else { return ("{0,3}" -f $r) } }

$conn = New-Object System.Data.SqlClient.SqlConnection "Server=localhost;Database=AzureDevOpsForager;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=30"
$conn.Open()
try {
  Emit ("=" * 100)
  Emit "RETRIEVAL vs RERANKER  -  where the correct answer sat before and after the cross-encoder"
  Emit ("pool = {0} (RerankerInputSize)   weights v=60 cfts=30 ffts=30   site {1}" -f $Pool, $Site)
  Emit ("=" * 100)
  Emit ("{0,-52} {1,-6} {2,-6} {3,-6} {4,-6}  {5}" -f "expected file","hyb","vec","fts","final","verdict")
  Emit ("-" * 100)

  $demoted = 0; $notfound = 0; $ok = 0; $weak = 0
  foreach ($row in $rows) {
    $vj = Get-Vector $row.Q
    $h = Get-RetrievalRank $conn $vj $row.Q $row.A 60 30 30
    $v = Get-RetrievalRank $conn $vj $row.Q $row.A 60  0  0
    $f = Get-RetrievalRank $conn $vj $row.Q $row.A  0 30 30

    $verdict = ""
    if ($row.F -ge 1 -and $row.F -le 5) { $verdict = "ok"; $ok++ }
    elseif ($h -ge 1 -and $h -le 5)     { $verdict = "RERANKER DEMOTED (retrieval had it at $h)"; $demoted++ }
    elseif ($h -ge 1)                   { $verdict = "retrieval weak (rank $h in pool), reranker did not rescue"; $weak++ }
    else                                { $verdict = "RETRIEVAL MISS - not in pool of $Pool at all"; $notfound++ }

    Emit ("{0,-52} {1} {2} {3} {4}   {5}" -f $row.A, (Fmt $h), (Fmt $v), (Fmt $f), (Fmt $row.F), $verdict)
  }

  Emit ("-" * 100)
  Emit ("final top-5 hits      : {0}" -f $ok)
  Emit ("reranker demoted      : {0}   <- retrieval was right, cross-encoder lost it" -f $demoted)
  Emit ("retrieval weak        : {0}   <- in pool but deep; reranker failed to promote" -f $weak)
  Emit ("retrieval miss        : {0}   <- never in the pool; embedder/corpus limit" -f $notfound)
  Emit ("=" * 100)
}
finally { $conn.Close() }

if ($Out) { $lines | Set-Content -Path $Out -Encoding utf8; Write-Host "`nwritten -> $Out" }
