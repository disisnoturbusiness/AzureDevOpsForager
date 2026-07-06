<#
.SYNOPSIS
  Fetches the two ONNX models Azure DevOps Forager needs and places them where config.json expects.

.DESCRIPTION
  Exports official HuggingFace weights to ONNX with Optimum (reproducible; no dependency on
  third-party ONNX mirrors):

    intfloat/e5-large-v2     -> models/e5-large-v2/e5-large-v2.onnx  (+ vocab.txt)          [embeddings, 1024-dim]
    BAAI/bge-reranker-v2-m3  -> models/bge-reranker-v2-m3-onnx/model.onnx (+ sentencepiece.bpe.model)  [reranker]

  Both models are MIT-licensed and redistributable.

.PREREQUISITES
  Python 3.9+ and pip on PATH. The script installs 'optimum[exporters]', onnx, and onnxruntime
  into the current Python environment if they are missing. First run downloads ~2.5 GB of weights.

.NOTES
  Manual alternative: download the models from their HuggingFace pages, export to ONNX yourself,
  and drop the files at the two paths above (then set OnnxModelPath / RerankerModelPath in config.json).
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "==> Ensuring Python export tooling (optimum[exporters], onnx, onnxruntime)..."
python -m pip install --quiet --upgrade "optimum[exporters]" onnx onnxruntime

# --- e5-large-v2 (query/passage embeddings, 1024-dim) ---
$e5 = Join-Path $root "e5-large-v2"
Write-Host "==> Exporting intfloat/e5-large-v2 -> $e5"
optimum-cli export onnx --model intfloat/e5-large-v2 --task feature-extraction $e5
$e5model = Join-Path $e5 "model.onnx"
if (Test-Path $e5model) { Move-Item -Force $e5model (Join-Path $e5 "e5-large-v2.onnx") }

# --- bge-reranker-v2-m3 (cross-encoder reranker) ---
$bge = Join-Path $root "bge-reranker-v2-m3-onnx"
Write-Host "==> Exporting BAAI/bge-reranker-v2-m3 -> $bge"
optimum-cli export onnx --model BAAI/bge-reranker-v2-m3 --task text-classification $bge

Write-Host ""
Write-Host "Done. Expected files:"
Write-Host "  $e5\e5-large-v2.onnx        (+ vocab.txt)"
Write-Host "  $bge\model.onnx             (+ sentencepiece.bpe.model)"
Write-Host ""
Write-Host "config.json defaults already point here:"
Write-Host "  OnnxModelPath     = models/e5-large-v2/e5-large-v2.onnx"
Write-Host "  RerankerModelPath = models/bge-reranker-v2-m3-onnx/model.onnx"
