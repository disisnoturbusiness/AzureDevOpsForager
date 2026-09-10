# Local baseline: e5-large-v2 + bge-reranker-v2-m3

Measured 2026-08-28 on this workstation. SQL Server 2025 RTM, 254 files / 605 vectors of
eShopOnWeb, CPU-only ONNX for both models. This is the A side of the A/B; the B side is the
hosted stack (1536-dim hosted embedder + Qwen3-Reranker-0.6B on GPU) and has not been run yet.

## Why the old question set was thrown out

The six questions used on 2026-07-17 were picked to demo well. Every one of them contained a
word that appears in its own answer's filename ("how does the app send email" -> EmailSender.cs),
so plain full-text matching could win them outright and the embedder was never actually tested.
Any embedder comparison run on that set would have measured nothing.

The replacement set has 14 semantic questions with **zero** token overlap between question and
answer filename, verified programmatically per question at run time, plus 4 exact-identifier
questions as a lexical control and 2 unanswerable questions as a threshold control.

## Headline

| set | n | rank1 | top5 | miss | MRR |
|---|---|---|---|---|---|
| semantic   | 14 | 3 | 6 | 8 | 0.304 |
| identifier |  4 | 3 | 4 | 0 | 0.833 |

The identifier control passing 4/4 confirms the lexical leg works, so the semantic score is a
real measurement rather than a broken pipeline.

## Validity checks performed before drawing any conclusion

1. **All 16 expected-answer files are in the corpus and fully vectorized** (2 to 5 chunks each,
   every chunk with a non-null embedding). No miss is a coverage gap. Confirmed by direct query
   against CodeFiles / CodeChunks.
2. **The vector leg is live.** This run is the first one after the `WITH APPROXIMATE` grammar
   fix in SchemaInitializer; prior local runs silently fell back to full-text only.
3. **Retrieval was measured separately from reranking** by calling dbo.SearchCode directly with
   the same weights the server uses (v=60, chunk-fts=30, file-fts=30, MinFtsRank=10,
   MaxDistance=2.0) at the same pool depth the reranker receives (30).

## Where the failures actually are

Pre-rerank rank of the correct file, per leg, against final post-rerank rank:

| expected file | hybrid | vector | fts | final | |
|---|---|---|---|---|---|
| EmptyBasketOnCheckoutException.cs | 4 | 1 | 10 | 1 | reranker promoted |
| TransferBasket.cs | - | - | - | - | retrieval miss |
| ToastComponent.cs | 4 | - | 5 | 4 | carried by fts |
| ImageValidators.cs | 5 | 1 | 9 | 2 | reranker promoted |
| CacheHelpers.cs | - | - | - | - | retrieval miss |
| CatalogContextSeed.cs | 1 | 11 | 1 | 1 | carried by fts |
| HomePageHealthCheck.cs | - | - | - | - | retrieval miss |
| ExceptionMiddleware.cs | 4 | 1 | 14 | - | reranker demoted |
| IdentityTokenClaimService.cs | 13 | 6 | - | 2 | reranker promoted 13 -> 2 |
| GetMyOrdersHandler.cs | 19 | 16 | - | - | deep in pool, not rescued |
| CatalogFilterPaginatedSpecification.cs | 21 | - | - | - | deep in pool, not rescued |
| Logout.cshtml.cs | - | - | - | - | retrieval miss |
| Register.cshtml.cs | 2 | 2 | 16 | 1 | reranker promoted |
| CustomAuthStateProvider.cs | - | - | - | - | retrieval miss |

### The reranker is net positive

Four promotions, one demotion, two neutral. The 13 -> 2 rescue of IdentityTokenClaimService.cs
is exactly the case a cross-encoder exists for. It is not the problem on this corpus.

### Retrieval is the bottleneck, and specifically the embedder

Five of eight misses never entered the pool of 30 on any leg. For all five the vector-only rank
is also absent, so e5-large-v2 did not place the correct file near the top for its own question.
These are natural-language questions with no shared vocabulary with the filename, which is the
case a code-specialized embedder is supposed to handle.

e5 is bimodal rather than uniformly weak. When it hits, it hits hard (vector rank 1 on
EmptyBasketOnCheckoutException, ImageValidators, ExceptionMiddleware; rank 2 on Register). When
it misses, the file is nowhere in 30. Two of the six final hits (ToastComponent,
CatalogContextSeed) were carried by full text, not by the embedder at all, so e5's own semantic
contribution is 4 of 14.

## Latency (recorded, NOT scored)

Roughly 110 s per query on CPU. Recorded only so the runs are reproducible. The two stacks run
on different hardware, so latency says nothing about either model and is excluded from the
comparison. The scorecard is retrieval quality only: rank-1, top-5, MRR, and pool membership.

## Threshold calibration

The two unanswerable control questions returned 3 and 5 results respectively, all false
positives. There is no effective abstention at the current settings. This is a separate issue
from ranking quality and is not addressed here.

## The prediction this sets up

If a code-specialized embedder is worth its cost, bge-code-v1 should pull some of these five
into the pool:

    TransferBasket.cs
    CacheHelpers.cs
    HomePageHealthCheck.cs
    Logout.cshtml.cs
    CustomAuthStateProvider.cs

That is a sharp, falsifiable claim. Run the identical battery against the hosted demo and
compare pool membership on those five, not just the headline MRR. Scored on results only.

## Reproducing

    scripts\run-eval-battery.ps1   -Site <url> -Label <text> -Out <file>
    scripts\diagnose-rerank.ps1    -Site <url> -Pool 30      -Out <file>

diagnose-rerank.ps1 requires direct SQL access, so it only runs against the local stack. For the
hosted side only the battery applies.

---

# Stack vs stack: local against hosted (2026-08-28)

Both stacks answered the identical 20-question battery against the identical corpus (254 files,
605 vectors, confirmed equal on both sides).

    LOCAL   e5-large-v2 (ONNX, CPU)  + bge-reranker-v2-m3 (ONNX, CPU)
    HOSTED  HF embed endpoint        + Qwen3-Reranker-0.6B-seq-cls (vLLM, GPU)

This compares the two whole pipelines as configured. It is deliberately not an
embedder-versus-embedder test, and must not be quoted as one: the two sides differ in reranker
model and in result filtering as well, so no single component can be credited.

## Scoreboard

| | local | hosted |
|---|---|---|
| semantic, correct answer in top 5 | 6 of 14 | 5 of 14 |
| semantic MRR | 0.304 | 0.286 |
| head-to-head wins | 4 | 3 |
| exact class names at rank 1 | 3 of 4 | 4 of 4 |
| files returned on the 2 unanswerable questions | 3 and 5 | 1 and 0 |

## Per question

| question | expected file | local | hosted | |
|---|---|---|---|---|
| nothing in cart at checkout | EmptyBasketOnCheckoutException.cs | 1 | miss | local |
| guest items on sign in | TransferBasket.cs | miss | 2 | hosted |
| temporary popup message | ToastComponent.cs | 4 | 1 | hosted |
| picture upload allowed | ImageValidators.cs | 2 | miss | local |
| repeated lookups | CacheHelpers.cs | miss | miss | both fail |
| store starting products | CatalogContextSeed.cs | 1 | 1 | tie |
| storefront reachable | HomePageHealthCheck.cs | miss | miss | both fail |
| unhandled failure to api response | ExceptionMiddleware.cs | miss | 1 | hosted |
| bearer credential after sign in | IdentityTokenClaimService.cs | 2 | miss | local |
| everything bought before | GetMyOrdersHandler.cs | miss | miss | both fail |
| long product list split up | CatalogFilterPaginatedSpecification.cs | miss | miss | both fail |
| shopper ends session | Logout.cshtml.cs | miss | miss | both fail |
| create a new login | Register.cshtml.cs | 1 | 2 | local |
| who is currently signed in | CustomAuthStateProvider.cs | miss | miss | both fail |

## Reading it

On natural-language questions the two are a coin flip: 4 wins to 3 with 7 draws on n=14, and an
MRR difference of 0.018 that is well inside noise at this sample size. Neither stack is better
at answering questions in any way this test can detect.

Hosted is better on exact identifiers, 4 of 4 against 3 of 4.

Hosted is much better at abstaining. It returned 1 and 0 files for the two questions the corpus
cannot answer, against local's 3 and 5. This is configuration rather than model quality: hosted
sets `MINVECTORONLY_RERANK_SCORE = 0.05` and `MINRERANK_TOP_SCORE = 0.000001`, and the local
config has neither key. The same floor costs hosted some near-misses, so the setting trades
recall for precision. It is a dial, and local currently has it turned off.

Evidence it is a score cut and not a result cap: re-issuing a hosted query with nResults=10
still returned a single row, whose metadata read rerank_score=0.03474, below the 0.05 floor,
surviving only because its match_source was Hybrid rather than vector-only.

## The finding that matters more than the winner

Six of fourteen questions were missed by **both** stacks:

    CacheHelpers.cs
    HomePageHealthCheck.cs
    GetMyOrdersHandler.cs
    CatalogFilterPaginatedSpecification.cs
    Logout.cshtml.cs
    CustomAuthStateProvider.cs

Every one of these files is present and fully vectorized in both corpora. The local diagnostic
showed five of them never entered the retrieval pool of 30 on any leg, vector or full text. No
reranker can fix what retrieval never surfaces, so swapping rerankers or turning score floors
will not move these. This is the real ceiling, and it is the thing worth working on.

## What this comparison cannot tell you

The reranker and the filtering differ alongside the embedder, so nothing here attributes a
result to the embedder. The hosted embedding model is also unconfirmed: its `/info` reports
`model_id = /repository` and no app setting names it, so "bge-code-v1" is inherited from earlier
documentation rather than verified. To attribute anything to the embedder specifically, the two
sides would need the same reranker and the same score floors.

---

# The clean embedder comparison (2026-09-10)

Finally measured with everything else held equal. Both corpora verified identical first:
605 chunks, 605 vectors, 351 carrying the context header, on each side.

Method: exact cosine rank of the correct file across all 605 chunks. No DiskANN approximation,
no RRF fusion, no reranker, no score floor. Query vectors were built with the same instruction
wrapper the application uses (`<instruct>Given a code search query, retrieve relevant code that
answers the query.\n<query>...`), documents raw, as bge-code-v1 expects. One endpoint wake, no
`/query` calls, no publish. Azure SQL was reached directly through a temporary firewall rule that
was removed afterward.

| expected file | local e5-large-v2 | hosted (1536-dim) | winner |
|---|---|---|---|
| EmptyBasketOnCheckoutException.cs | **1** | 124 | local, by 123 |
| Register.cshtml.cs | **2** | 51 | local, by 49 |
| TransferBasket.cs | 93 | **15** | hosted, by 78 |
| CacheHelpers.cs | 56 | **33** | hosted, by 23 |
| HomePageHealthCheck.cs | 262 | **61** | hosted, by 201 |
| GetMyOrdersHandler.cs | **24** | 99 | local, by 75 |
| CatalogFilterPaginatedSpecification.cs | 60 | **17** | hosted, by 43 |
| Logout.cshtml.cs | **59** | 95 | local, by 36 |

## What it settles

**The code-specialized embedder does not rescue the hard questions.** It wins four of the six,
and on HomePageHealthCheck the margin is large, 262 to 61. But its best rank anywhere in this set
is **15 of 605**. Nothing lands in the top 5, so all six still miss in any real search. Buying a
better embedder does not turn these into answers.

**Neither model dominates.** The hosted embedder is dramatically worse exactly where the local one
is strongest: rank 1 becomes 124, rank 2 becomes 51. The two are good at different questions
rather than one being better.

**Therefore the six shared misses are not an embedder problem**, which also means the original
diagnosis behind them is still open. It was not the chunk header (tested and falsified, see
above) and it is not embedder quality (tested here). The remaining candidates are the chunking
strategy itself and the questions' distance from anything literally present in the code.

## What it does not settle

The hosted embedding model is still unidentified. Its `/info` reports `model_id = /repository`
and no app setting names it, so "bge-code-v1" remains inherited from documentation rather than
confirmed. The dimension is 1536, which is consistent with that claim but does not prove it.

A caveat on the two control questions: the live site returns Register.cshtml.cs at rank 2 for
"where does someone create a new login" even though its raw vector rank here is 51. That is not a
contradiction. The site fuses full-text with vector, and the lexical leg carries that one. It is
a reminder that these numbers isolate the embedder deliberately, and the shipped pipeline is
stronger than its vector leg alone.

## Cost

One endpoint wake, 46 seconds to scale from zero, then eight embed calls. No reranker calls and
no publish.

---

# The reranker comparison (2026-09-10)

The experiment that should have been run first. Both rerankers were handed the **identical**
candidate shortlist for every question, so the embedder, the index, the fusion weights and the
score floors are all removed from the comparison. A cross-encoder is a pure function of
(query, documents); give two of them the same documents and the only variable left is the model.

    LOCAL   bge-reranker-v2-m3            ONNX, CPU
    HOSTED  Qwen3-Reranker-0.6B-seq-cls   vLLM, GPU

Shortlists came from one `dbo.SearchCode` call per question at TopN=30, the same pool depth the
server uses (`RerankerInputSize`). Ranks are file-level after de-duplication.

Only the 13 questions whose shortlist actually contained the answer are scored. A reranker cannot
reorder a document that was never retrieved, so scoring the other five would measure retrieval and
blame the reranker for it.

## Result

| expected file | first stage | bge-v2-m3 | Qwen3-0.6B | |
|---|---|---|---|---|
| EmptyBasketOnCheckoutException.cs | 8 | 1 | 1 | tie |
| ToastComponent.cs | 4 | 4 | **1** | Qwen3 |
| ImageValidators.cs | 6 | 2 | **1** | Qwen3 |
| CatalogContextSeed.cs | 1 | 1 | 1 | tie |
| ExceptionMiddleware.cs | 5 | 5 | **1** | Qwen3 |
| IdentityTokenClaimService.cs | 18 | **2** | 5 | bge |
| GetMyOrdersHandler.cs | 27 | 7 | **4** | Qwen3 |
| CatalogFilterPaginatedSpecification.cs | 28 | 9 | **4** | Qwen3 |
| Register.cshtml.cs | 2 | **1** | 3 | bge |
| OrderBuilder.cs *(identifier)* | 2 | 3 | **1** | Qwen3 |
| ExceptionMiddleware.cs *(identifier)* | 1 | 1 | 1 | tie |
| CacheHelpers.cs *(identifier)* | 1 | 1 | 1 | tie |
| BasketQueryService.cs *(identifier)* | 1 | 1 | 1 | tie |

    Qwen3-Reranker-0.6B wins 6    bge-reranker-v2-m3 wins 2    tie 5

| stage | MRR | over first stage |
|---|---|---|
| first stage, no rerank | 0.452 | |
| bge-reranker-v2-m3 | 0.618 | +37% |
| **Qwen3-Reranker-0.6B** | **0.772** | **+71%** |

## What this changes

**The reranker is where the retrieval quality lives on this corpus, and the embedder is not.**
Swapping the embedding model moved nothing that mattered: four wins out of six hard questions, no
question made retrievable, and both controls badly worse. Swapping the reranker on identical input
moves MRR by 71%.

The clearest cases are the ones retrieval nearly lost. `GetMyOrdersHandler.cs` came out of the
first stage at rank 27 and `CatalogFilterPaginatedSpecification.cs` at 28, both effectively
invisible, and Qwen3 pulled them to 4. `ExceptionMiddleware.cs` sat at 5 and went to 1, where
bge left it at 5 and never moved it at all.

bge is not useless: it still adds 37% over raw retrieval, and it wins two questions outright,
including rescuing `IdentityTokenClaimService.cs` from 18 to 2 where Qwen3 only reached 5. But
across the set it is the weaker model by a clear margin.

**This also settles the earlier ambiguity about the deployed 0.6B.** The docs recorded the 4B as
chosen, and the 0.6B was deployed instead for latency. On measured retrieval quality against the
model this project previously shipped, the 0.6B is the stronger reranker regardless.

## What it does not settle

Only two of the six rerankers in the evaluation table have now been run head to head. The other
four remain published benchmarks.

Five questions are still unanswerable by this pipeline because the first stage never surfaces the
right file. No reranker can fix that, and this experiment does not try to.

Latency is not scored. The two models run on different hardware, roughly 250 ms per query on GPU
against roughly 140 s on CPU, which measures an A10G against a laptop and says nothing about
either model.

## Reproducing

    scripts/rerank-ab/    candidates -> hosted -> local -> report

Raw output is in `rerank-candidates.json`, `rerank-hosted.json` and `rerank-local.json`.
