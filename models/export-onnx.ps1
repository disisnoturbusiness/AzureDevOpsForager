<#
  Exports the two LOCAL ONNX models, for optimum 2.x.

  Replaces the export half of download-models.ps1, which was written against optimum 1.x
  where 'exporters' was a valid extra. In optimum 2.x the ONNX exporter lives in the
  separate 'optimum-onnx' package, so `pip install "optimum[exporters]"` silently installs
  optimum WITHOUT the exporter and `optimum-cli export onnx` fails with
  "unrecognized arguments: onnx".

  Prereq (already done):  python -m pip install optimum-onnx
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$e5   = Join-Path $root 'e5-large-v2'
$bge  = Join-Path $root 'bge-reranker-v2-m3-onnx'

Write-Host "==> [1/2] intfloat/e5-large-v2 -> $e5  (1024-dim embeddings)"
optimum-cli export onnx --model intfloat/e5-large-v2 --task feature-extraction $e5
if ($LASTEXITCODE -ne 0) { throw "e5 export failed with exit code $LASTEXITCODE" }

$e5src = Join-Path $e5 'model.onnx'
$e5dst = Join-Path $e5 'e5-large-v2.onnx'
if (Test-Path $e5src) {
  Move-Item -Force $e5src $e5dst
  Write-Host "    renamed model.onnx -> e5-large-v2.onnx"
}

Write-Host ""
Write-Host "==> [2/2] BAAI/bge-reranker-v2-m3 -> $bge  (cross-encoder reranker)"
optimum-cli export onnx --model BAAI/bge-reranker-v2-m3 --task text-classification $bge
if ($LASTEXITCODE -ne 0) { throw "bge-reranker export failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "==> [3/3] sentencepiece.bpe.model -> $bge"
# BgeReranker.cs resolves the SentencePiece vocabulary as a SIBLING of RerankerModelPath and
# disables reranking (fail-soft, RRF-only) if it is absent. The optimum ONNX export emits
# tokenizer.json instead, so the raw SentencePiece model has to be fetched from the model repo.
$spDest = Join-Path $bge 'sentencepiece.bpe.model'
if (Test-Path $spDest) {
  Write-Host "    already present, skipping"
} else {
  python -c "from huggingface_hub import hf_hub_download; import shutil, sys; p = hf_hub_download(repo_id='BAAI/bge-reranker-v2-m3', filename='sentencepiece.bpe.model'); shutil.copy(p, sys.argv[1])" $spDest
  if ($LASTEXITCODE -ne 0) { throw "sentencepiece.bpe.model download failed with exit code $LASTEXITCODE" }
  Write-Host "    fetched"
}

Write-Host ""
Write-Host "==> Verifying the five files the app actually requires:"
$required = @(
  (Join-Path $e5  'e5-large-v2.onnx'),
  (Join-Path $e5  'vocab.txt'),
  (Join-Path $bge 'model.onnx'),
  (Join-Path $bge 'model.onnx_data'),
  $spDest
)
$missing = @()
foreach ($f in $required) {
  if (Test-Path $f) {
    $mb = [math]::Round((Get-Item $f).Length / 1MB, 1)
    Write-Host ("    OK    {0,8} MB  {1}" -f $mb, (Split-Path -Leaf $f))
  } else {
    Write-Host "    MISS            $f"
    $missing += $f
  }
}
if ($missing.Count -gt 0) { throw "$($missing.Count) required file(s) missing." }

Write-Host ""
Write-Host "config.json settings for these local models:"
Write-Host "  OnnxModelPath      = <abs path>/models/e5-large-v2/e5-large-v2.onnx"
Write-Host "  RerankerModelPath  = <abs path>/models/bge-reranker-v2-m3-onnx/model.onnx"
Write-Host "  EmbeddingDimension = 1024   (REQUIRED for e5-large-v2; 1536 is the hosted bge-code-v1 default)"

Write-Host ""
Write-Host "==> Resulting files:"
Get-ChildItem -Recurse -File $e5, $bge |
  Select-Object @{n='MB';e={[math]::Round($_.Length/1MB,1)}}, FullName |
  Sort-Object MB -Descending |
  Format-Table -AutoSize
