<#
.SYNOPSIS
  For the six questions BOTH stacks miss, shows what the vector leg actually returns instead.
.DESCRIPTION
  No reindex, no reranker, no hosted calls. Vector-only at depth 12 so we see the ranking the
  embedder produces, plus where the expected file really sits.
#>
param([string]$Site = "http://localhost:8010", [int]$Depth = 12)
$ErrorActionPreference = 'Stop'

$rows = @(
  @{ Q="what happens to a guest's items when they sign in";                A="TransferBasket.cs" }
  @{ Q="what keeps repeated lookups from hitting the database every time"; A="CacheHelpers.cs" }
  @{ Q="what tells us the storefront is still reachable";                  A="HomePageHealthCheck.cs" }
  @{ Q="where does a customer see everything they bought before";          A="GetMyOrdersHandler.cs" }
  @{ Q="how is a long product list broken up for display";                 A="CatalogFilterPaginatedSpecification.cs" }
  @{ Q="what runs when a shopper ends their session";                      A="Logout.cshtml.cs" }
)

function Get-Vector([string]$q) {
  $body = @{ text = $q; kind = "query" } | ConvertTo-Json -Compress
  $emb  = Invoke-RestMethod -Uri "$Site/embed" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 300
  $vec  = $emb
  if ($emb -is [System.Management.Automation.PSCustomObject]) {
    foreach ($n in 'vector','embedding','data','values') { if ($emb.PSObject.Properties.Name -contains $n) { $vec = $emb.$n; break } }
  }
  if ($vec -and $vec[0] -is [System.Array]) { $vec = $vec[0] }
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.Append('[')
  for ($k=0; $k -lt @($vec).Count; $k++) { if ($k -gt 0) { [void]$sb.Append(',') }; [void]$sb.Append([System.Convert]::ToDouble($vec[$k]).ToString('R',[System.Globalization.CultureInfo]::InvariantCulture)) }
  [void]$sb.Append(']')
  return $sb.ToString()
}

$conn = New-Object System.Data.SqlClient.SqlConnection "Server=localhost;Database=AzureDevOpsForager;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=30"
$conn.Open()
try {
  foreach ($row in $rows) {
    $vj = Get-Vector $row.Q
    Write-Host ""
    Write-Host ("Q: {0}" -f $row.Q)
    Write-Host ("   want: {0}" -f $row.A)
    $cmd = $conn.CreateCommand(); $cmd.CommandTimeout = 180
    $cmd.CommandText = @"
DECLARE @qv VECTOR(1024) = CAST(@vecjson AS VECTOR(1024));
EXEC dbo.SearchCode @SearchText=@txt, @QueryVector=@qv, @TopN=@top, @ChunkType=NULL,
                    @VectorWeight=60, @ChunkFtsWeight=0, @FileFtsWeight=0,
                    @MinFtsRank=10, @MaxDistance=2.0;
"@
    [void]$cmd.Parameters.AddWithValue("@vecjson",$vj)
    [void]$cmd.Parameters.AddWithValue("@txt",$row.Q)
    [void]$cmd.Parameters.AddWithValue("@top",$Depth)
    $rd = $cmd.ExecuteReader()
    $i=0; $seen=@(); $hit=-1
    while ($rd.Read()) {
      $f = ([string]$rd['FilePath'] -split '[\/]')[-1]
      if ($seen -notcontains $f) {
        $seen += $f; $i++
        $mark = if ($f -eq $row.A) { "  <<< WANTED" } else { "" }
        if ($f -eq $row.A) { $hit = $i }
        Write-Host ("   {0,2}. {1,-46} dist={2:N4}{3}" -f $i, $f, [double]$rd['Distance'], $mark)
      }
    }
    $rd.Close()
    if ($hit -lt 1) { Write-Host ("   -> WANTED FILE NOT IN TOP {0}" -f $Depth) }
  }
}
finally { $conn.Close() }
