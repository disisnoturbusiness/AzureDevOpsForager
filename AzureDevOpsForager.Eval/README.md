# AzureDevOpsForager.Eval — retrieval quality harness

Answers the one question the search code cannot answer about itself: **did that change actually make retrieval better, and what did it cost in latency?**

It runs a hand-labelled golden set against every configuration in a sweep and writes the comparison out as CSV, JSON and Markdown.

---

## What it measures

| Metric | Asks |
|---|---|
| **nDCG@K** | Were the best answers ranked above the merely acceptable ones? |
| **Recall@K** | Were the right answers found at all? |
| **Precision@K** | What fraction of the page was worth reading? |
| **MRR** | How far down did the user have to read? |
| **Success rate** | Did any relevant result land in the top K? |
| **p50 / p95 latency** | What did the quality cost? |

Four quality metrics rather than one because they disagree in useful ways. A change that lifts recall while sinking MRR is a real trade-off, and a single headline number hides it.

## What it can vary

Every knob the fusion proc and the search path read:

- `RrfVectorWeight`, `RrfChunkFtsWeight`, `RrfFileFtsWeight` — the three RRF legs
- `MinFtsRank` — the full-text rank floor
- `MaxVectorDistance` — the cosine distance ceiling
- `RerankerEnabled`, `RerankerInputSize` — second-stage rerank and its over-fetch pool
- `Embedder`, `Reranker` — `local` (ONNX) or `hosted` (Hugging Face endpoint)

---

## Running it

```bash
dotnet run --project AzureDevOpsForager.Eval -- --golden golden-queries.json --sweep sweep.json --out .\eval-output
```

| Flag | Meaning |
|---|---|
| `--golden <path>` | Golden query set JSON. **Required.** |
| `--sweep <path>` | Sweep spec JSON. Omit to measure current settings only. |
| `--out <dir>` | Output directory. Defaults to `./eval-output`. |
| `--top-k <n>` | Cutoff K for the @K metrics. Defaults to 10. |
| `--baseline <name>` | Config name to measure deltas against. Defaults to the first run. |
| `--config <path>` | Forager `config.json`. Defaults to the one beside the exe. |

It reads the same `config.json` and user overrides the server does, so it measures the deployed path rather than a lookalike.

### Output

- **`results.csv`** — one row per configuration, knobs and metrics side by side. Sort it.
- **`per-query.csv`** — one row per configuration per query. **This is the file that tells you what a change broke**, which the means never will.
- **`report.md`** — ranked table with baseline deltas, a plain-language verdict, and warnings.
- **`results.json`** — everything, for anything the CSVs cannot express.

---

## Building the golden set

`Samples/golden-queries.sample.json` is a **template**, not a usable set — its paths point at eShopOnWeb because that is the shipped default corpus. Replace them with real paths from your index.

Rules that keep the numbers honest:

1. **Label before you sweep.** Labels derived from a previous run's output just re-certify whatever the search already did.
2. **Aim for ~30 queries.** Enough that one relabelled item does not move the mean; few enough to label carefully in one sitting.
3. **Mix the kinds.** Navigational ("catalog filter specification") and conceptual ("how is an order created after checkout") stress different legs of the fusion. Use `Category` to tag them so the report can show a change helping one and hurting the other.
4. **Grade, don't just mark.** `3` answers the question outright, `2` strongly relevant, `1` background, `0` looks relevant but isn't. Only nDCG can see the difference, and grading is what makes it able to.
5. **Set `ChunkName` when the method matters.** It is what separates "found the right file" from "found the right method".
6. **Write the `Notes`.** Not read by any metric. It exists so a label can be argued with in six months instead of taken on faith.

Paths are matched leniently (case-insensitive, separator-normalized, segment-aligned suffix), so a repo-relative label matches an absolute indexed path. The segment alignment is deliberate: it stops `Service.cs` from matching `BasketService.cs`.

The loader **fails the whole run** on a malformed set rather than skipping bad entries. A measuring instrument that ignores the parts of itself it doesn't understand still produces confident-looking readings, and those get believed.

---

## Two things it refuses to fake

**Configurations that cannot run are reported, not dropped.** Each one is proved on a single query before thirty more are spent on it. A missing model, a dead connection or a dimension mismatch comes back as a named skipped row with the reason attached.

**A dead vector leg is called out.** If a configuration returns zero vector-backed results, it was measuring full-text search wearing a hybrid label, and the report says so regardless of how good the scores look. Usually it means `MaxVectorDistance` sits below the embedding model's real distance floor.

### Sweeping the embedder

The query vector has to come from the same model as the stored vectors. Changing `Embedder` against a fixed index compares a query vector against foreign geometry, and SQL will reject the width mismatch — reported as a skipped configuration.

**To compare embedding models properly, reindex between runs** and run the harness once per index. That is a real limitation of the approach, not a bug in the harness.
