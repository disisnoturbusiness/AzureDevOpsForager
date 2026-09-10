param([string]$Site="http://localhost:8010",[int]$Depth=15)
$ErrorActionPreference='Stop'
$rows=@(
  @{L="WORKS "; Q="what stops someone paying with nothing in their cart"; A="EmptyBasketOnCheckoutException.cs"}
  @{L="WORKS "; Q="what fills the store with starting products";          A="CatalogContextSeed.cs"}
  @{L="FAILS "; Q="what tells us the storefront is still reachable";      A="HomePageHealthCheck.cs"}
  @{L="FAILS "; Q="what runs when a shopper ends their session";          A="Logout.cshtml.cs"}
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
$conn=New-Object System.Data.SqlClient.SqlConnection "Server=localhost;Database=AzureDevOpsForager;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=30"
$conn.Open()
"{0,-8} {1,-52} {2,8} {3,8} {4,9} {5,7}" -f "kind","question","best","15th","spread","want@"
("-"*95)
try{
 foreach($r in $rows){
  $vj=Get-Vector $r.Q
  $c=$conn.CreateCommand();$c.CommandTimeout=180
  $c.CommandText=@"
DECLARE @qv VECTOR(1024) = CAST(@vecjson AS VECTOR(1024));
EXEC dbo.SearchCode @SearchText=@txt,@QueryVector=@qv,@TopN=@top,@ChunkType=NULL,
     @VectorWeight=60,@ChunkFtsWeight=0,@FileFtsWeight=0,@MinFtsRank=10,@MaxDistance=2.0;
"@
  [void]$c.Parameters.AddWithValue("@vecjson",$vj);[void]$c.Parameters.AddWithValue("@txt",$r.Q);[void]$c.Parameters.AddWithValue("@top",$Depth)
  $rd=$c.ExecuteReader();$d=@();$seen=@();$want=-1;$i=0
  while($rd.Read()){
    $f=([string]$rd['FilePath'] -split '[\/]')[-1]
    if($seen -notcontains $f){$seen+=$f;$i++;$d+=[double]$rd['Distance'];if($f -eq $r.A){$want=$i}}
  }
  $rd.Close()
  $spread = if($d.Count -gt 1){$d[$d.Count-1]-$d[0]}else{0}
  "{0,-8} {1,-52} {2,8:N4} {3,8:N4} {4,9:N4} {5,7}" -f $r.L,$r.Q.Substring(0,[Math]::Min(50,$r.Q.Length)),$d[0],$d[$d.Count-1],$spread,$(if($want -gt 0){$want}else{"none"})
 }
}finally{$conn.Close()}
