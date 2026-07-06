#!/usr/bin/env bash
# Fetches the two ONNX models Azure DevOps Forager needs and places them where config.json expects.
#
#   intfloat/e5-large-v2     -> models/e5-large-v2/e5-large-v2.onnx  (+ vocab.txt)          [embeddings, 1024-dim]
#   BAAI/bge-reranker-v2-m3  -> models/bge-reranker-v2-m3-onnx/model.onnx (+ sentencepiece.bpe.model)  [reranker]
#
# Both models are MIT-licensed and redistributable. Exports official HuggingFace weights to ONNX with
# Optimum (reproducible; no dependency on third-party mirrors).
#
# Prerequisites: Python 3.9+ and pip. First run downloads ~2.5 GB of weights.
# Manual alternative: download from the HuggingFace pages, export to ONNX yourself, and drop the files
# at the two paths above (then set OnnxModelPath / RerankerModelPath in config.json).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "==> Ensuring Python export tooling (optimum[exporters], onnx, onnxruntime)..."
python3 -m pip install --quiet --upgrade "optimum[exporters]" onnx onnxruntime

# --- e5-large-v2 (query/passage embeddings, 1024-dim) ---
E5="$ROOT/e5-large-v2"
echo "==> Exporting intfloat/e5-large-v2 -> $E5"
optimum-cli export onnx --model intfloat/e5-large-v2 --task feature-extraction "$E5"
[ -f "$E5/model.onnx" ] && mv -f "$E5/model.onnx" "$E5/e5-large-v2.onnx"

# --- bge-reranker-v2-m3 (cross-encoder reranker) ---
BGE="$ROOT/bge-reranker-v2-m3-onnx"
echo "==> Exporting BAAI/bge-reranker-v2-m3 -> $BGE"
optimum-cli export onnx --model BAAI/bge-reranker-v2-m3 --task text-classification "$BGE"

echo
echo "Done. Expected files:"
echo "  $E5/e5-large-v2.onnx        (+ vocab.txt)"
echo "  $BGE/model.onnx             (+ sentencepiece.bpe.model)"
