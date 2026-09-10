<#
.SYNOPSIS
  Exact global rank of the correct answer by raw vector distance across all 605 chunks.
.DESCRIPTION
  No DiskANN, no RRF, no reranker, no score floor. Just: how close is the right chunk to the
  question, compared with everything else in the corpus. This measures the embedder itself.
  If the correct chunk ranks in the hundreds, no amount of pipeline tuning can recover it.
#>
param([string]$Site="http://localhost:8010")
$ErrorActionPreference='Stop'
$rows=@(
  @{Q="what stops someone paying with nothing in their cart";              A="EmptyBasketOnCheckoutException.cs"; K="works"}
  @{Q="where does someone create a new login";                             A="Register.cshtml.cs";                K="works"}
  @{Q="what happens to a guest's items when they sign in";                 A="TransferBasket.cs";                 K="FAILS"}
  @{Q="what keeps repeated lookups from hitting the database every time";  A="CacheHelpers.cs";                   K="FAILS"}
  @{Q="what tells us the storefront is still reachable";                   A="HomePageHealthCheck.cs";            K="FAILS"}
  @{Q="where does a customer see everything they bought before";           A="GetMyOrdersHandler.cs";             K="FAILS"}
  @{Q="how is a long product list broken up for display";                  A="CatalogFilterPaginatedSpecification.cs"; K="FAILS"}
  @{Q="what runs when a shopper ends their session";                       A="Logout.cshtml.cs";                  K="FAILS"}
)
function Get-Vector([string]$q){
  $b=@{text=$q;kind="query"}|ConvertTo-Json -Compress
  $e=Invoke-RestMethod -Uri "$Site/embed" -Method Post -ContentType 'application/json' -Body $b -TimeoutSec 300
  $v=$e; if($e -is [System.Management.Automation.PSCustomObject]){foreach($n in 'vector','embedding','data','values'){if($e.PSObject.Properties.Name -contains $n){$v=$e.$n;break}}}
  if($v -and $v[0] -is [System.Array]){$v=$v[0]}
  $sb=New-Object System.Text.StringBuilder;[void]$sb.Append('[')
  for($k=0;$k -lt @($v).Count;$k++){if($k -gt 0){[void]$sb.Append(',')};[void]$sb.Append([System.Convert]::ToDouble($v[$k]).ToString('R',[System.Globalization.CultureInfo]::InvariantCulture))}
  [void]$sb.Append(']');return $sb.ToString()
}
$conn=New-Object System.Data.SqlClient.SqlConnection "Server=localhost;Database=AzureDevOpsForager;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=60"
$conn.Open()
"{0,-6} {1,-44} {2,10} {3,10} {4,10} {5,10}" -f "kind","expected file","bestRank","bestDist","top1Dist","of"
("-"*100)
try{
 foreach($r in $rows){
  $vj=Get-Vector $r.Q
  $c=$conn.CreateCommand();$c.CommandTimeout=300
  $c.CommandText=@"
DECLARE @qv VECTOR(1024) = CAST(@vecjson AS VECTOR(1024));
WITH d AS (
  SELECT c.Id, f.FilePath,
         VECTOR_DISTANCE('cosine', c.Embedding, @qv) AS Dist
  FROM dbo.CodeChunks c JOIN dbo.CodeFiles f ON f.Id=c.CodeFileId
), r AS (
  SELECT *, ROW_NUMBER() OVER (ORDER BY Dist) AS Rnk FROM d
)
SELECT
  (SELECT MIN(Rnk) FROM r WHERE FilePath LIKE @want) AS BestRank,
  (SELECT MIN(Dist) FROM r WHERE FilePath LIKE @want) AS BestDist,
  (SELECT MIN(Dist) FROM r)                          AS Top1Dist,
  (SELECT COUNT(*)  FROM r)                          AS Total;
"@
  [void]$c.Parameters.AddWithValue("@vecjson",$vj)
  [void]$c.Parameters.AddWithValue("@want","%"+$r.A)
  $rd=$c.ExecuteReader()
  if($rd.Read()){
    $br=if($rd['BestRank'] -is [DBNull]){"n/a"}else{[int]$rd['BestRank']}
    $bd=if($rd['BestDist'] -is [DBNull]){0}else{[double]$rd['BestDist']}
    "{0,-6} {1,-44} {2,10} {3,10:N4} {4,10:N4} {5,10}" -f $r.K,$r.A,$br,$bd,[double]$rd['Top1Dist'],[int]$rd['Total']
  }
  $rd.Close()
 }
}finally{$conn.Close()}
