# Model Selection Evidence — Embedder + Reranker

**Decision date 2026-07-16 · commit `6e9cbfa`.**

> ### Correction, 2026-08-28: the deployed reranker is the 0.6B, not the 4B
>
> This document records the 4B as the chosen reranker. It is not what is running. The live
> endpoint serves **`tomaarsen/Qwen3-Reranker-0.6B-seq-cls`**, confirmed two independent ways:
> the `RERANKER_MODEL_NAME` app setting on the azuredevopsforager web app, and the endpoint's
> own `/v1/models` response. The endpoint is still *named* `qwen3-reranker-4b-seq-cls-urh`,
> which is how it went unnoticed.
>
> **The swap was deliberate.** On this workload the 4B measured roughly **4% better and about 125%
> slower**. That is not a trade worth making for an interactive search box, so the 0.6B was deployed
> and this document never caught up. The decision was right; the record was stale.
>
> The 4B benchmark figures below are third-party numbers for a model that is not deployed.
> Section 2's own independent evidence for the 0.6B (CoIR 65.18 from arXiv 2509.25085v3) is
> the line that actually describes production.

Recovered 2026-08-25 from the construction transcript. Everything here is either a **measurement taken against the live endpoints** or a **published third-party benchmark with its source named** — the two are kept separate on purpose, and nothing below is an estimate.

---

## 1. Measured on the live endpoints

These are our own numbers, taken against the running HF Inference Endpoints on 2026-07-16.

### 1.1 The finding that mattered: prompt formatting dominates

Qwen3-Reranker scores through its chat template. We tested applying that wrapping client-side versus posting raw query/document text to the same endpoint, same model, same documents:

| Prompt format | Relevant vs irrelevant score separation |
|---|---|
| **Qwen3 chat-template wrapping (client-side)** | **4,600×** |
| Raw, no wrapping | **3.3×** |

Same model. Same corpus. **Three orders of magnitude of ranking quality live in the prompt formatting**, not in the model choice.

**The exact test**, `tomaarsen/Qwen3-Reranker-4B-seq-cls`, 329 prompt tokens, three documents, one query — both calls against the same live endpoint:

```
Instruct: Given a code search query, retrieve relevant code chunks that answer the query.
Query:    how is user authentication handled
```

| Document | Chat-template wrapped | Raw |
|---|---|---|
| `PasswordSignInAsync(...)` — **the answer** | **0.1618865728378296** | 0.375985 |
| `LogTimestamp(...)` — logging helper | 0.0000346425440512 | 0.105656 |
| `CalculateBasketTotal(...)` — unrelated | 0.0000053274602578 | 0.113646 |
| **Separation, top vs next** | **4,673×** | **3.31×** |

Top vs worst, wrapped: **30,388×**.

**Precise reading, because the loose one overclaims.** Raw did *not* fail — it ranked the authentication method first too. It got the answer right. What it lost was **discrimination**: it scored a basket-total calculation and a logging helper within 8% of each other, and both within a factor of 3.5 of the correct answer. Wrapped, the correct answer sits three to four orders of magnitude clear of the field, which is what makes a score threshold viable at all.

The embedder was verified in the same pass: query `how does the checkout flow work` with the `<instruct>`/`<query>` prefix returned HTTP 200 and exactly **1536 dimensions**.

### 1.2 Live system measurements

| Metric | Value |
|---|---|
| Warm query, end to end | **918 ms** |
| Full reindex — 254 files / 605 chunks, staging + atomic swap | **1 m 13 s** |
| Embedder output dimension, verified against live endpoint | exactly **1536** |
| Health after swap | Green |

Qualitative checks that passed: a zero-keyword-overlap semantic query returned the right files, and grounded chat cited correctly.

### 1.3 Speed — the constraint that actually decided things

Once the reranker was chosen on accuracy, **latency became the binding constraint**, and it drove more decisions than the benchmark scores did.

| Measurement | Value | When |
|---|---|---|
| **Cold-start query** (scaled-to-zero endpoint + auto-paused serverless SQL) | **116,466 ms — 1 m 56 s** | 2026-07-08 |
| Cold start, re-measured before demo recording | **~140 s** | 2026-07-17 |
| **Warm query, end to end** | **918 ms** | 2026-07-16 |
| Endpoint warm via heartbeat | **1 s** | 2026-07-16 |
| GPU endpoint reindex vs local CPU ONNX | **~10–15× faster** (minutes → seconds) | estimated |

**Cold start, not accuracy, was the real product risk.** A recruiter clicking the demo link after it has been idle gets a first query that takes up to two minutes. Someone who thinks it's broken and closes the tab at second 20 never sees the retrieval quality at all. That produced the warm-up retry, the heartbeat, and honest "waking up the server…" messaging rather than a spinner that lies — and the operational rule of pre-warming ~140 s before recording anything.

**Where speed beat accuracy on the model choice:**

- **Qwen3-Reranker-8B was rejected on the speed/cost curve, not on quality.** MTEB-Code 81.22 vs the 4B's 81.20 — **a 0.02 gain for roughly double the GPU cost**, requiring A100/L40S-class hardware. It only wins meaningfully on Chinese (CMTEB-R 77.45 vs 75.94). Not worth it for this workload.
- **A10G was chosen over the cheaper L4** for the 4B reranker specifically because it gives **~2× the memory bandwidth and better latency**, despite L4 fitting the model in memory at $0.80/hr vs $1.00.
- **T4 was ruled out for the 4B entirely**, and rejected for the embedder because TEI's Turing image is experimental with Flash-Attention precision problems.

**Why hosted at all:** self-hosting embedding and reranking was attempted at length on an Azure **B2s** and abandoned — the instance does not have the compute. That failure is what led to HF Inference Endpoints, and it is why the app holds no local models in RAM.

---

## 2. Published benchmarks that drove the selection

**Not our measurements.** Sources named per row.

### 2.1 Rerankers

| Model | MTEB-Code (rerank top-100) | CoIR | License | Outcome |
|---|---|---|---|---|
| Qwen3-Reranker-8B | 81.22 | — | Apache-2.0 | overkill for the gain |
| Qwen3-Reranker-4B | 81.20 | — | Apache-2.0 | chosen at the time; **not deployed** |
| **Qwen3-Reranker-0.6B** | 73.42 | **65.18** | Apache-2.0 | **what is actually running** (see correction at top) |
| jina-reranker-v3 | — | 63.28 (also cited ~70.6) | **CC-BY-NC** | unusable commercially |
| jina-reranker-v2 | — | 56.14 | **CC-BY-NC** | unusable commercially |
| zerank-2 | code nDCG@10 **0.6528** | — | **CC-BY-NC** | unusable commercially |
| **bge-reranker-v2-m3** *(incumbent)* | **41.38** | 35.97 | Apache-2.0 | **replaced** |

Sources: Qwen model-card table (MTEB-Code); jina-v3 paper Table 2 (CoIR); ZeroEntropy's own eval for zerank-2.

**The headline:** the incumbent `bge-reranker-v2-m3` scored **41.38 against 81.20** — it was losing roughly **half the achievable code-ranking nDCG**. It is a 568M XLM-RoBERTa/BGE-M3 model that remains strong on multilingual text (MIRACL 69.32, best in its table) and lags *specifically and severely on code*.

For context on the rejected commercial options: zerank-2's code nDCG@10 of 0.6528 beat Gemini 2.5 Flash (0.6128) and cohere-rerank-3.5 (0.5364) — it was rejected on license, not capability.

### 2.2 Embedder

`BAAI/bge-code-v1` — Qwen2.5-Coder-1.5B backbone, 1536-dim, 32k context, Apache-2.0, **CoIR ~81.8**, state of the art on code retrieval. Replaced `e5-large-v2` (1024-dim, general-text).

**`Qwen3-Embedding-4B` was evaluated and rejected on a schema constraint, not quality:** 2560 native dimensions **exceeds SQL Server's ~1998 `VECTOR(n)` cap**. It would have required MRL truncation to 1536 or 1024 with quality loss, at roughly **2.5× the serving cost** of bge-code-v1 for comparable code-retrieval quality (MTEB-Code ~80.06). Only worth it if top-tier natural-language retrieval from the same model were also needed. It isn't.

### 2.3 A benchmark finding that shaped the architecture

CoREB — *"Beyond Retrieval"*, arXiv 2605.04615 (2026), a contamination-limited multitask code retrieval benchmark:

- Off-the-shelf rerankers swing **up to 12 nDCG points** on code-to-code
- **No generic baseline is net-positive** across text-to-code / code-to-text / code-to-code
- **Short keyword queries collapse every model**

→ **This is why BM25/FTS5 stays in the RRF fusion** rather than trusting the reranker on keyword queries. The hybrid design is a response to published evidence, not a hedge.

---

## 2.4 The full candidate evaluation — six rerankers, and why benchmarks couldn't decide it

Before the swap, a nine-agent research pass (~852K tokens) evaluated every viable reranker and embedder. The conclusion it reached is more interesting than the pick.

### Rerankers evaluated

| Model | Params | Evidence | Outcome |
|---|---|---|---|
| **Qwen3-Reranker-0.6B** | 0.6B | CoIR **65.18** (independent, arXiv 2509.25085v3 Table 2) | best Apache CPU-class option |
| **bge-reranker-v2-m3** *(incumbent)* | 0.57B | CoIR **36.28** · BEIR 56.51 · MIRACL **69.32** | code-weak, text-strong |
| mxbai-rerank-base-v2 | 0.5B | BEIR 58.40 · CoIR 65.71 — roughly a tie with Qwen3-0.6B | near-free second A/B arm |
| jina-reranker-v3 | — | strong on CoIR | **CC-BY-NC — license-blocked** |
| jina-code | — | — | **CC-BY-NC — license-blocked** |
| zerank-1-small | 1.7B | Apache-2.0 | ~3× the cost |

### ⭐ The methodology finding — this is the real work

**The headline benchmark was thrown out after being checked.**

The widely-quoted "MTEB-Code 73.42 vs 41.38" figure is **Qwen self-run on Qwen-retrieved candidates** — the vendor scoring its own model on its own retrieval. Not independent.

So the contested claim was verified from the primary source instead: fetching **arXiv 2509.25085 v3, Table 2** — the jina-reranker-v3 paper, a *rival* lab — which gives Qwen3-Reranker-0.6B **CoIR 65.18** against bge-reranker-v2-m3's **36.28**. A genuine, independent, same-pipeline **~29-point code gap**.

Worth noting: **two of the nine research agents claimed that table didn't exist**, having read an older revision of the paper. They were wrong, and the pinned-revision check caught it.

**And then the whole benchmark layer was demoted anyway:**

> **MTEB-Code and CoIR contain zero named C#.** No model in the field has any published C#-specific evaluation. Qwen was fine-tuned on CodeSearchNet (in-domain); bge had no code training at all. CoREB (2026) separately found **no off-the-shelf reranker is net-positive across code tasks.**

**Therefore: published benchmarks can only *pick candidates*. They cannot decide a winner.** Every option is equally unproven on C#, which is what makes a hand-labelled golden set mandatory rather than nice-to-have. That conclusion is why the eval harness exists.

### Two more calls that went against the obvious answer

**Staging was inverted — embedder first, reranker second.** The instinct is to swap the reranker first because the benchmark gap is bigger. Rejected, because the reranker is the *risky* integration: chat-template handling plus yes/no logit extraction, and both vLLM and llama.cpp had shipped silent-wrong-score bugs. The embedder is deterministic and parity-testable, with a drop-in dimension and existing ONNX exports. **Do the verifiable one first.**

**A cost claim was corrected by hand.** One agent asserted Qwen3-Reranker cost "+35% FLOPs" over bge. Working the embedding-table math directly — bge's XLM-R-large table is ~250,002 × 1024 ≈ 256M of its 568M parameters and carries no FLOPs, leaving ~312M non-embedding, against Qwen3-0.6B's ~440M — gives **≈1.41×**, and the full-vocab `lm_head` at only the last position is ~0.3 GFLOP, negligible. The "+35%" figure was true only for a naive all-position export.

---

## 3. Serving infrastructure and cost

TEI **cannot serve Qwen3-Reranker natively** (HF PRs #835/#886 open, targeted for TEI v1.10.0). The `tomaarsen/Qwen3-Reranker-*-seq-cls` sequence-classification conversions load directly in **vLLM** (`task="score"`, OpenAI-compatible `/rerank`), so the reranker runs on a vLLM container while the embedder runs on TEI.

**HF Inference Endpoint GPU pricing surveyed:**

| GPU | VRAM | $/hr |
|---|---|---|
| T4 ×1 | 16 GB (14 usable) | 0.50 |
| L4 ×1 | 24 GB | 0.80 |
| **A10G ×1** | 24 GB | **1.00** |
| L40S ×1 | 48 GB | 1.80 |
| A100 ×1 | 80 GB | 2.50 |
| H200 ×1 | 141 GB | 5.00 |
| CPU | — | 0.033–0.536 |

Sizing: the 1.5B embedder (~3.1 GB fp16) fits an L4; T4 was rejected because TEI's Turing image is experimental with Flash-Attention precision problems. The 4B reranker (~8 GB fp16) fits an L4, but **A10G was chosen for ~2× the memory bandwidth and better latency**; T4 is not viable for 4B at all.

**Cost outcome:** worst-case always-on embedder + reranker on 2×L4 was costed at **~$1.60/hr ≈ $1,150/month**. Running A10G at $1/hr each with **scale-to-zero at 30 minutes** brings real demo usage down to a few dollars a day.

Live endpoints (A10G, us-east-1, Private, scale-to-zero 30 min):
- embed — `bge-code-v1-oec`
- rerank — endpoint named `qwen3-reranker-4b-seq-cls-urh`, **serving `Qwen3-Reranker-0.6B-seq-cls`**
  (verified against `/v1/models` on 2026-08-28; the name is stale, the model is the 0.6B)

---

## 4. What this evidence does not cover

Stated plainly so nobody over-claims it.

- **There is no nDCG or recall@k measured on our own corpus.** Section 2 is published benchmarks on public datasets; section 1 is a score-separation test on a hand-built 3-document probe.
- **There is no model-vs-model A/B on our own retrieval quality.** `AzureDevOpsForager.Eval` (built 2026-08-14) is the harness that produces it; the golden set is still the 6-entry eShopOnWeb template.
- **The six rerankers were never run head-to-head on the same query.** That table is published benchmarks, not a bake-off on this corpus. The 4,673× measurement tested one model against itself in two prompt formats.
- **No per-model latency.** 918 ms is whole-system; nothing isolates reranker time from embed, SQL or fusion.

### ⭐ The embedder A/B is runnable — it just hasn't been run

Both embedder paths ship: `EmbeddingService` (local ONNX e5-large-v2, 1024-dim) and `HuggingFaceEmbedder` (hosted bge-code-v1, 1536-dim). The comparison is **two indexes and two runs**, not a dead end.

**And it does not need the full 30-query labelled golden set to start.** The six-question battery already has known-correct answers — EmailSender.cs, Basket.cs, AuthenticateEndpoint.cs, CatalogItemService.cs. Run those six plus the negative control against an e5-indexed corpus and there is a defensible rank-1 head-to-head the same day. The golden set upgrades that from "which one got it right" to nDCG@k. **It is not a prerequisite for a first number.**
- Comparing embedders is not a sweep — **each embedding model requires a full reindex**, because the query vector must match the stored vector geometry.

**The honest one-line summary:** the model selection is defensible on published benchmarks plus a decisive live prompt-format measurement. The end-to-end retrieval-quality number is the one thing still missing, and it is gated on labelling the golden set.

---

*Recovered from session transcript `e2ae7dc4-ae44-4ae0-b932-56c52d3b6ed8`, 2026-07-16 19:16–23:29 and 2026-07-17 13:20.*
