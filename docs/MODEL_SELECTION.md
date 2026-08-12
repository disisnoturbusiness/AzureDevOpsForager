# Model & serving selection

Why this project runs the models it runs. Every number below was measured against this deployment —
254 files / 605 chunks of C# (the eShopOnWeb reference app) on Azure SQL, served from Hugging Face
Inference Endpoints — or taken from a published benchmark and cited as such.

**What runs today**

| Stage | Model | Served by | Hardware |
|---|---|---|---|
| Embedding | `BAAI/bge-code-v1` (1536-dim) | TEI | A10G, scale-to-zero |
| Reranking | `tomaarsen/Qwen3-Reranker-0.6B-seq-cls` | vLLM `+ --enforce-eager` | A10G, scale-to-zero |
| Answering | `llama-3.3-70b` | Groq | — |

There is also a fully local path (`e5-large-v2` + `bge-reranker-v2-m3` via ONNX Runtime, no GPU, no
account, no API cost) behind the same `IEmbedder` / `IReranker` interfaces. Nothing below argues that
path is wrong; it argues about what is best *for hosted code search*.

---

## 1. Embeddings: `e5-large-v2` → `BAAI/bge-code-v1`

**The problem with the general-text model.** `e5-large-v2` is a strong general-purpose encoder trained
on prose. Code is not prose: identifiers are compound (`GetOrderTotalAsync`), the useful signal is
often structural rather than lexical, and a natural-language question and the code that answers it
share almost no surface vocabulary. A general encoder puts "how is the basket total calculated" and
`Basket.Total()` further apart than it puts two unrelated English sentences.

**Why bge-code-v1.** It is code-specialized — a Qwen2.5-Coder-1.5B backbone, 1536-dim, 32k context,
Apache 2.0 — and reports state of the art on CoIR, the code-retrieval benchmark. On this corpus the
practical difference shows up as natural-language questions actually reaching the right file.

**What it cost.**

- **A full reindex.** 1024-dim → 1536-dim means the `VECTOR(n)` column, the DiskANN index and
  `dbo.SearchCode` all change shape. Vectors from the two models are not interchangeable; an index is
  only searchable with the model that built it.
- **A different prompt convention.** e5 wants `query: ` / `passage: ` prefixes. bge-code-v1 is
  *asymmetric* in a different way: queries are wrapped `<instruct>{task}\n<query>{text}`, documents are
  embedded raw. Get this mismatched between index time and query time and every distance inflates.

**The trap this created — worth the whole section.** `MaxVectorDistance` was left at `0.5`, a value
calibrated for e5. bge-code-v1 puts code→code distances at 0.11–0.44 but natural-language→code at
**~0.49 and up**. So the ceiling sat just below where every realistic query lands, the vector leg
returned zero rows for weeks, RRF quietly fused only the two full-text signals, and the application
kept returning plausible-looking results the whole time. Nothing errored. See
[§4](#4-what-actually-goes-wrong) — this is the single most expensive thing in this document.

---

## 2. Reranker model: `bge-reranker-v2-m3` → Qwen3-Reranker

Retrieval gets you a candidate pool; a cross-encoder decides what is actually relevant by scoring each
`(query, chunk)` pair jointly instead of comparing two independently-computed vectors. That joint
scoring is why it is worth a second model at all.

The same code-vs-prose argument applies, and the published gap on **MTEB-Code** is not subtle:

| Reranker | MTEB-Code |
|---|---|
| `bge-reranker-v2-m3` | 41.38 |
| `Qwen3-Reranker-0.6B` | **73.42** |
| `Qwen3-Reranker-4B` | 81.20 |

`bge-reranker-v2-m3` is a fine general multilingual reranker and remains the local-ONNX default,
because offline-with-no-GPU is a real constraint and 41.38 beats no reranking. For the hosted demo,
where a code-specialized model is one endpoint away, it was leaving most of the available precision on
the table.

**Deployment note.** Qwen3-Reranker ships as a causal LM that scores through yes/no logits.
`tomaarsen/Qwen3-Reranker-0.6B-seq-cls` is the community sequence-classification conversion of the same
weights, which is what lets a standard serving stack expose it as a scoring endpoint. That distinction
turns out to matter enormously — see [§3.1](#31-the-architecture-field-decides-everything).

---

## 3. Reranker size: 4B → 0.6B, and what that did *not* fix

The 4B scores 81.20 against the 0.6B's 73.42, so the swap gave up real ranking quality. It was made for
cold start: these are scale-to-zero endpoints, and the 4B's first query after an idle period took
around two minutes.

It helped — and then the investigation found that **the model was never the main cost.** From vLLM's own
startup logs serving the 0.6B:

```
Loading weights took 1.28 seconds
Model loading took 1.12 GiB memory and 1.668149 seconds
torch.compile took 20.80 s in total
init engine (profile, create kv cache, warmup model) took 26.58 s (compilation: 20.80 s)
```

Weight loading is **1.3 seconds of a 64-second container startup**. Everything else is framework:
roughly 36 s of Python/torch import and CUDA context creation before a weight is read, then ~21 s of
`torch.compile`. Shrinking the model only ever attacked the 1.3-second row.

Two corollaries, both learned the expensive way:

- A **bf16 conversion is pointless here.** The seq-cls repo is fp32 and twice the bytes, but vLLM
  already downcasts on load (`Model loading took 1.12 GiB` for a 0.6B model), so it costs nothing in
  VRAM and under a second in load time.
- **Quantization is solving the wrong problem** for the same reason.

### 3.1 The `architectures` field decides everything

vLLM chooses its runner from the model's own `config.json`, and the runner decides which HTTP routes
exist. Two repos of the same model, same image, same GPU, **zero container arguments** on either:

| | `Qwen/Qwen3-Reranker-0.6B` | `tomaarsen/…-seq-cls` |
|---|---|---|
| `architectures` | `Qwen3ForCausalLM` | `Qwen3ForSequenceClassification` |
| routes registered | `/v1/chat/completions`, `/v1/completions`, … | `/pooling` `/classify` `/score` **`/rerank`** |

Point an endpoint at the causal-LM repo and `/rerank` **is not in the routing table**. Every call is a
plain FastAPI route-miss `404`, which looks identical to a wrong model name in the request body but has
a completely different fix. Check `config.json` first, always.

### 3.2 Serving stack: the actual cold-start lever

Three stacks were measured end-to-end through the application, from a genuinely cold (scale-to-zero)
start on identical A10G hardware in the same region:

| | cold | warm | ranking |
|---|---|---|---|
| vLLM | 94.1 s | ~0.78 s | baseline |
| **vLLM + `--enforce-eager`** | **~65–70 s** | **~0.81 s** | identical |
| HF stock Inference Toolkit container | **37.0 s** | ~2.5 s | identical (verified) |

**`--enforce-eager` is close to free here and is what ships.** It drops `init engine` from **26.7 s to
1.38 s** by skipping `torch.compile` and CUDA-graph capture. The expected per-query penalty did not
materialise — warm stayed 584–1089 ms across six queries — because CUDA graphs never fire for this
workload anyway: they dispatch on uniform-decode batches, and a cross-encoder scoring pass never
produces one. vLLM was spending 25 seconds per cold start building something it could not use.

**Why not the stock container, at 37 s?** Because the trade reverses on the warm path: it skips
compilation entirely, so it starts in 5 seconds and pays the difference back on every query (~0.78 s →
~2.5 s). Break-even is roughly 33 queries in a single session. It remains a supported option —
`RERANKER_API_FORMAT=toolkit` switches the wire format from vLLM's Jina-style
`POST /rerank {model, query, documents, top_n}` to `POST / {"inputs": {query, texts}}` — and its scores
were verified identical before it was offered as one. For an interactive demo, sub-second queries were
judged worth ~30 seconds of one-time wake.

**Why not TEI, which starts the embedder in 12 s?** TEI cannot serve this model. Its
sequence-classification support is CamemBERT, XLM-RoBERTa, GTE, ModernBERT and RoBERTa; Qwen3 is
supported for *embeddings only*. And no code-specialized cross-encoder exists on a TEI-servable
architecture — every benchmarked one is a Qwen decoder. Choosing TEI means choosing a general-purpose
reranker, which is the trade [§2](#2-reranker-model-bge-reranker-v2-m3--qwen3-reranker) already rejected.

---

## 4. Why this reranker fits *this* job

Ranking is only half of what it does here. Its scores also drive the **relevance gate** — the thing that
lets the demo return nothing rather than bluff.

Retrieval always has a nearest neighbour, so searching a term this codebase has never contained
(`garbage`, `zebra`, `kubernetes`) still yields a full page of confident-looking files. Vector distance
cannot separate those: measured here, off-topic queries land in the *same* distance band as on-topic
ones, sometimes closer. The cross-encoder can — but only if its scores separate the two populations
cleanly, and this one does:

| | top rerank score |
|---|---|
| unanswerable queries (4 of them) | ≤ 0.0089 |
| weakest hit of any answered query | 0.279 |

A 31× gap with nothing inside it. That gap is what the gate is built on, and it is the concrete reason
a code-specialized cross-encoder earns its place: a reranker that scored answerable and unanswerable
queries into overlapping ranges would leave the demo no honest way to say "nothing here."

Combined with 0.6B being small enough to keep warm queries under a second on a single $1/h GPU that
parks itself at zero when nobody is looking, that is the fit: **enough precision to be trusted, enough
separation to know when to stay quiet, and cheap enough to leave running in public.**

---

## 5. What actually goes wrong

Every failure in this project's retrieval history has been silent. None of them threw.

1. **Tuning constants do not follow a model swap.** `MaxVectorDistance = 0.5` survived the move to
   bge-code-v1 and emptied the vector leg for weeks. On any model change, grep the config surface for
   numeric constants and ask of each: *is this expressed in units the model defines?* If yes, it will
   break. Prefer relative judgements — `MinRerankScoreRatio` is "at least 10% of the best score in this
   result set", which carries across models because it only depends on the shape of one result set.
2. **Fail-soft can be indistinguishable from a real answer.** The hosted reranker's fallback used to
   return `0.0` for every candidate — which the gate correctly read as "everything is irrelevant", so an
   endpoint outage emptied every search instead of degrading to RRF order. A fallback must emit
   something structurally impossible (it now returns high descending pseudo-scores), never a value
   inside the real range.
3. **Log the response body, not just the status code.** A bare `404` covers both "the route does not
   exist" and "that model name is not served" — opposite fixes. A bare `400` hid
   `{"error":"Body needs to provide a inputs key"}`. Both cost hours as status codes and seconds as
   bodies.
4. **Health checks lie by default.** This one returned a hardcoded `"Green"` next to a `SELECT COUNT(*)`
   that never touched the embedding column. It would have reported healthy against an all-NULL vector
   table.
5. **Measure before theorising.** Every wrong turn in the cold-start work came from reasoning about a
   number instead of reading it. The startup logs said `Loading weights took 1.28 seconds` next to
   `torch.compile took 20.80 s` the entire time.
