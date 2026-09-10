<#
.SYNOPSIS
  Calls dbo.SearchCode DIRECTLY with different RRF weights, bypassing the app entirely.

.DESCRIPTION
  The /query API drops MatchSource and the per-leg RRF scores, so from outside you cannot see
  which leg produced a result. This talks to the proc, which returns all of it.

  Purpose: prove whether the vector leg and the full-text leg actually produce DIFFERENT
  rankings on this corpus. If they don't, no embedder comparison run through this pipeline
  means anything.
#>
param(
  [string]$Question = "how is the basket total calculated",
  [string]$Site     = "http://localhost:8010",
  [int]$TopN        = 5
)
$ErrorActionPreference = 'Stop'

# 1) get the query vector from the running server (same model that built the index)
$body = @{ text = $Question; kind = "query" } | ConvertTo-Json -Compress
$emb  = Invoke-RestMethod -Uri "$Site/embed" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 180
$vec  = $emb
if ($emb -is [System.Management.Automation.PSCustomObject]) {
  foreach ($n in 'vector','embedding','data','values') { if ($emb.PSObject.Properties.Name -contains $n) { $vec = $emb.$n; break } }
}
if ($vec -and $vec[0] -is [System.Array]) { $vec = $vec[0] }
Write-Host ("query: {0}" -f $Question)
Write-Host ("vector dims: {0}" -f @($vec).Count)
if (@($vec).Count -eq 0) { throw "no vector returned from /embed" }
$sb = New-Object System.Text.StringBuilder
[void]$sb.Append('[')
for ($k=0; $k -lt @($vec).Count; $k++) { if ($k -gt 0) { [void]$sb.Append(',') }; [void]$sb.Append([System.Convert]::ToDouble($vec[$k]).ToString('R',[System.Globalization.CultureInfo]::InvariantCulture)) }
[void]$sb.Append(']')
$vecJson = $sb.ToString()

$variants = @(
  @{ Name = "HYBRID     "; V = 60; C = 30; F = 30 },
  @{ Name = "VECTOR ONLY"; V = 60; C =  0; F =  0 },
  @{ Name = "FTS ONLY   "; V =  0; C = 30; F = 30 }
)

$conn = New-Object System.Data.SqlClient.SqlConnection "Server=localhost;Database=AzureDevOpsForager;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=30"
$conn.Open()
try {
  foreach ($v in $variants) {
    Write-Host ""
    Write-Host ("--- {0}   vw={1} cfw={2} ffw={3} ---" -f $v.Name.Trim(), $v.V, $v.C, $v.F)
    $cmd = $conn.CreateCommand()
    $cmd.CommandTimeout = 120
    $cmd.CommandText = @"
DECLARE @qv VECTOR(1024) = CAST(@vecjson AS VECTOR(1024));
EXEC dbo.SearchCode @SearchText=@txt, @QueryVector=@qv, @TopN=@top, @ChunkType=NULL,
                    @VectorWeight=@vw, @ChunkFtsWeight=@cfw, @FileFtsWeight=@ffw,
                    @MinFtsRank=10, @MaxDistance=2.0;
"@
    [void]$cmd.Parameters.AddWithValue("@vecjson", $vecJson)
    [void]$cmd.Parameters.AddWithValue("@txt", $Question)
    [void]$cmd.Parameters.AddWithValue("@top", $TopN)
    [void]$cmd.Parameters.AddWithValue("@vw",  $v.V)
    [void]$cmd.Parameters.AddWithValue("@cfw", $v.C)
    [void]$cmd.Parameters.AddWithValue("@ffw", $v.F)
    $rd = $cmd.ExecuteReader()
    $i = 0
    while ($rd.Read()) {
      $i++
      $fp = [string]$rd['FilePath']
      $file = ($fp -split '[\\/]')[-1]
      "{0}. {1,-42} score={2,-10:N6} vec={3,-10:N6} cfts={4,-10:N6} ffts={5,-10:N6} src={6}" -f `
        $i, $file, [double]$rd['Score'], [double]$rd['VectorRRF'], [double]$rd['ChunkFtsRRF'], [double]$rd['FileFtsRRF'], $rd['MatchSource']
    }
    if ($i -eq 0) { "   (no rows)" }
    $rd.Close()
  }
}
finally { $conn.Close() }
