# Reranker A/B

Head-to-head comparison of `bge-reranker-v2-m3` (local ONNX, CPU) against
`Qwen3-Reranker-0.6B-seq-cls` (hosted, vLLM on GPU).

A cross-encoder is a pure function of (query, documents). Handing both models the **identical**
candidate list therefore removes the embedder, the index, the fusion weights and the score floors
from the comparison completely. Nothing differs but the reranker.

## Why it runs in stages

    rerank-ab.exe candidates   # pull one top-30 shortlist per question from local SQL, save it
    rerank-ab.exe hosted       # score those shortlists on the GPU endpoint  (seconds)
    rerank-ab.exe local        # score the same shortlists on CPU ONNX       (~40 minutes)
    rerank-ab.exe report       # compare

The hosted endpoint scales to zero and costs real money to wake, while the local pass takes about
forty minutes on CPU. Running hosted first and writing its scores to disk means the slow pass can
never burn the expensive one. Each stage writes its own JSON, so any of them can be rerun alone.

## Environment

`hosted` needs `HF_RERANK_URL` and `HF_TOKEN`, plus `RERANKER_API_FORMAT` and
`RERANKER_MODEL_NAME` if they differ from the defaults. Everything else comes from the config file
passed as the second argument (default `config.local-e5.json`).

## Scoring

Only questions whose shortlist actually contained the correct answer are scored. A reranker cannot
reorder something that was never retrieved, so counting those against it measures retrieval, not
reranking. On the eShopOnWeb corpus that is 13 of 18.

Results are de-duplicated to file level before ranking: several chunks of one file are one answer.
