# Azure DevOps Forager — Developer Guide

> Audience: an engineer **extending** this application. This document is deliberately verbose and code-accurate. Where a behavior is load-bearing (a magic constant, an ordering requirement, a fail-soft path), it is called out explicitly. File paths are given relative to the repo root.

Azure DevOps Forager is a self-hostable semantic + lexical code-search tool. It indexes a codebase (Azure DevOps TFVC, Azure DevOps Git, or GitHub) into **SQL Server 2025's native `VECTOR` type**, then answers questions with a **hybrid retrieval pipeline** — dense vector search fused with two full-text signals via Reciprocal Rank Fusion (RRF), re-ranked by a cross-encoder, and optionally explained by a grounded LLM (Groq). No code leaves your infrastructure except the question plus the snippets the server retrieves, and the LLM key lives only on the server.

Embedding and reranking each run **in-process via local ONNX** *or* **remotely against a Hugging Face (HF) Inference Endpoint** — and the two paths run different models. The HF path (how the hosted demo runs) uses code-specialized models: `BAAI/bge-code-v1` embeddings (1536-dim) and `Qwen3-Reranker-0.6B`. The local path uses the lightweight pair: `e5-large-v2` (1024-dim) and `bge-reranker-v2-m3`, in-process with zero GPU and zero API cost. These sit behind the `IEmbedder` and `IReranker` interfaces, so the choice is a configuration/DI concern, not a code one — a recipient can run zero-local-ONNX (point at HF), fully local (bundled ONNX models), or use the hosted demo. Local ONNX remains the offline/no-account default and works exactly as before; note the two model families produce incompatible vectors, so switching embedding models requires a full reindex (§4).

> For **end-user** instructions — running a search, the Indexer UI walkthrough, troubleshooting — see [USER_GUIDE.md](USER_GUIDE.md). This guide is the internals.

---

## Table of Contents

1. [Architecture](#1-architecture)
2. [Data Model](#2-data-model)
3. [Search Pipeline](#3-search-pipeline)
4. [Embedding](#4-embedding)
5. [Configuration & Override Layering](#5-configuration--override-layering)
6. [Build & Test](#6-build--test)
7. [Extending the System](#7-extending-the-system)
8. [Deployment](#8-deployment)
9. [Appendix: End-to-End Data Flow](#9-appendix-end-to-end-data-flow)

---

## 1. Architecture

The solution is six projects. The unusual multi-targeting is intentional: `Core` is `netstandard2.0` so it can be shared by every consumer, from a .NET Framework 4.8 desktop app to a .NET 10 Linux server.

| Project | Target framework | Role |
|---------|------------------|------|
| `AzureDevOpsForager.Core` | `netstandard2.0` | Domain + all services: embeddings, search, reranking, chat providers, source adapters, integration clients, schema/DDL, config, utilities. **No project references** — depends only on NuGet. |
| `AzureDevOpsForager.Indexer` | `net10.0-windows` (WinForms) | Single-window index builder. Picks a source + destination DB, Roslyn-chunks, embeds, and writes the vector index. Owns the Roslyn engine. |
| `AzureDevOpsForager.Server` | `net10.0` (`Microsoft.NET.Sdk.Web`) | ASP.NET Core minimal-API host + static web UI (`wwwroot`). Serves `/query`, `/chat`, `/embed`, `/systems`, `/health`, etc. Holds the Groq key. Reads SQL. Runs as console **or** Windows Service. |
| `AzureDevOpsForager.WinForms` | `net48` | Desktop chat viewer — a thin HTTP client of the server's `/chat`. |
| `AzureDevOpsForager.Shared` | `net48` | Shared WinForms UI base (`BaseMainForm`) + utilities used by the desktop viewer. |
| `AzureDevOpsForager.Tests` | `net10.0-windows` | xUnit test project (references `Core` + `Indexer`). |

### How they relate

```
                         ┌────────────────────────────┐
   Web UI (wwwroot) ────▶│  AzureDevOpsForager.Server  │───────▶ SQL Server 2025
                         │  ASP.NET Core minimal API   │  query   dbo.CodeFiles
   Desktop chat ────────▶│  /query /chat /embed ...    │◀────────  dbo.CodeChunks
   (WinForms + Shared)   │  embed→RRF→rerank→Groq      │           VECTOR(1536)+DiskANN
                         └────────────────────────────┘           + full-text indexes
                                     │  Groq key (env / .enc)
                                     ▼
                                  Groq API

   AzureDevOpsForager.Indexer (WinForms)
     pick source ─▶ TFVC / Azure Git / GitHub  (ISourceProvider)
     Roslyn chunk + embed ─▶ *_Staging tables ─▶ atomic swap ─▶ live index
```

- **`Core` is the hub.** Every executable references it. It contains the shared `Config` static class, the `SchemaInitializer` (single source of DDL truth), the search services, the embedding service, the reranker, the source-provider interface and its three implementations, the integration clients (Azure DevOps, GitHub), and the chat providers.
- **The Indexer is the only writer.** It owns the Roslyn chunker/metadata extractor (`AzureDevOpsForager.Indexer/Indexing/*`) and the write path (`AzdoIndexerService`).
- **The Server is the only reader** exposed to clients. `HybridSearchService` is registered as a singleton and every endpoint flows through it or straight to SQL.
- **The clients hold no key and no DB connection.** Both the web UI (served from `wwwroot`) and the WinForms chat (`GroqMainForm` → `BaseChatService`) simply POST to the server's `/chat`.

### Key runtime contracts between projects

- The **embedding dimension is config-driven**: `Config.EmbeddingDimension` (default **1536**, matching the hosted `bge-code-v1`; set **1024** for the local `e5-large-v2`) flows into the `VECTOR(n)` column, the DiskANN index, and the `dbo.SearchCode` proc. The model, the dimension, and the stored vectors must all agree — **changing the embedding model requires a full reindex** (the staging + atomic-swap reindex, §2.4, does this with zero downtime).
- **Embedding + reranking are pluggable via `IEmbedder` and `IReranker`.** Each has a local-ONNX implementation (`EmbeddingService` — `e5-large-v2`; `BgeReranker` — `bge-reranker-v2-m3`) and a remote HF implementation (`HuggingFaceEmbedder` — `bge-code-v1`; `HuggingFaceReranker` — `Qwen3-Reranker-0.6B`). The local and remote embedders run **different models with different dimensions**, so their vectors are *not* interchangeable — an index is searchable only with the model that built it.
- The **Indexer and Server can share an embedding source.** The Indexer can embed locally (its own ONNX model), remotely via a HF endpoint (`HuggingFaceEmbedder`), *or* by calling the Server's `/embed` endpoint. The Server exposes `/embed` precisely so a self-hoster without a local model can still build an index.
- **`SchemaInitializer` is the shared schema authority.** The Indexer calls it to create/stage/swap; the Server's search proc is created by it. Nothing else emits DDL.

---

## 2. Data Model

All DDL lives in `AzureDevOpsForager.Core/Services/Storage/SchemaInitializer.cs`. `Schema/CreateSchema.sql` mirrors it for manual setup, but the Indexer creates everything automatically on connect via `EnsureSchemaAsync`.

### 2.1 `dbo.CodeFiles` — one row per file (~37 columns)

Built by the single-source builder `CodeFilesTable(string suffix)`. The suffix is `""` for the live table and `"_Staging"` for the reindex staging table; **sharing one body is what guarantees live and staging can never drift** and silently break the swap.

Column groups (all `NVARCHAR`, nullable unless noted):

| Group | Columns |
|-------|---------|
| Identity / keys | `Id INT IDENTITY PK`, `FilePath NVARCHAR(500) NOT NULL` (UNIQUE `UQ_CodeFiles_FilePath`) |
| File identity | `FileType` |
| VCS / blame metadata | `Author`, `FileAddDate`, `AllAuthors`, `CommitMessages`, `WorkItemTitles`, `WorkItemTags` |
| Content | `Content NVARCHAR(MAX)` (full file text) |
| Extracted code metadata (Roslyn) | `Namespace`, `ClassName`, `ClassNames`, `ClassModifiers`, `BaseClass`, `Interfaces`, `PropertyNames`, `Properties`, `MethodNames`, `Constructors`, `OverriddenMethods`, `AbstractMethods`, `VirtualMethods`, `AsyncMethods`, `EnumValues`, `Constants`, `StaticFields`, `Attributes`, `Events`, `Delegates`, `GenericTypes`, `ReferencedTypes`, `Dictionaries`, `SqlOperations`, `Usings`, `Regions` |
| Audit | `ModifiedDate DATETIME2(7) NOT NULL DEFAULT (GETDATE())` |

These are populated from `FileMetadata` (`AzureDevOpsForager.Indexer/Indexing/RoslynMetadataExtractor.cs`) by `AzdoIndexerService.UpsertCodeFile`. Note: `AllAuthors`, `CommitMessages`, `WorkItemTitles`, `WorkItemTags` are currently written as empty strings — the VCS enrichment is a documented follow-up. The columns exist and are full-text indexed, so wiring them up later requires no schema change.

### 2.2 `dbo.CodeChunks` — method/class/member chunks, with the vector

Built by `CodeChunksTable(string suffix)`:

```sql
CREATE TABLE dbo.CodeChunks
(
   Id            INT IDENTITY(1,1) NOT NULL,
   CodeFileId    INT               NOT NULL,   -- FK -> CodeFiles(Id) ON DELETE CASCADE
   ChunkKey      NVARCHAR(500)     NOT NULL,   -- UNIQUE (stable identity for delta / dedupe)
   ChunkType     NVARCHAR(50)      NOT NULL,   -- file|class|interface|method|constructor|property|members
   ChunkName     NVARCHAR(200)     NOT NULL,
   StartLine     INT               NOT NULL,
   EndLine       INT               NOT NULL,
   ChunkContent  NVARCHAR(MAX)     NOT NULL,   -- the text that was embedded / matched
   Embedding     VECTOR(1536)      NULL,       -- native vector; n = Config.EmbeddingDimension
   Namespace     NVARCHAR(500)     NULL,
   ClassName     NVARCHAR(200)     NULL,
   Signature     NVARCHAR(MAX)     NULL,
   ParentContext NVARCHAR(MAX)     NULL,
   CONSTRAINT PK_CodeChunks PRIMARY KEY CLUSTERED (Id),
   CONSTRAINT FK_CodeChunks_CodeFiles FOREIGN KEY (CodeFileId)
      REFERENCES dbo.CodeFiles(Id) ON DELETE CASCADE,
   CONSTRAINT UQ_CodeChunks_ChunkKey UNIQUE (ChunkKey)
);
```

- **`ChunkKey`** is `FilePath:ChunkType:ChunkName:StartLine` (see `CodeChunkDto.GetId()`), capped to 500 chars by `AzdoIndexerService.CapKey` (which appends an FNV-1a hash suffix if it must truncate, preserving uniqueness).
- **`Embedding`** is nullable so a file can be indexed for full-text even when embedding is disabled.
- The DDL emits `VECTOR({Config.EmbeddingDimension})` — 1536 by default (hosted `bge-code-v1`), 1024 when configured for the local `e5-large-v2`.

### 2.3 Indexes

- **B-tree** (`IX_CodeChunks_CodeFileId`, `IX_CodeChunks_ChunkType`): created by `EnsureSchemaAsync` (idempotent, guarded by `IF NOT EXISTS`).
- **DiskANN vector index** `IX_CodeChunks_Embedding`:
  ```sql
  CREATE VECTOR INDEX IX_CodeChunks_Embedding ON dbo.CodeChunks(Embedding)
     WITH (metric='cosine', type='diskann');
  ```
  **Deliberately NOT created by `EnsureSchemaAsync`** — DiskANN requires ≥100 non-NULL vectors, so it is created *after* the first load, inside `SwapStagingToLiveAsync` (`RecreateLiveSearchObjectsAsync`). Its creation is wrapped in `TryExecAsync` so a platform without DiskANN (e.g. some Azure SQL regions) still promotes the index; search then falls back to exact `VECTOR_SEARCH` distance.
- **Full-text catalog + indexes** (`CODEINDEX_FTC`):
  - On `dbo.CodeFiles`: covers `Content` plus most extracted-metadata columns and the VCS columns (`CommitMessages`, `WorkItemTitles`, `WorkItemTags`, `AllAuthors`). Keyed on `PK_CodeFiles`, `CHANGE_TRACKING AUTO`.
  - On `dbo.CodeChunks`: covers `ChunkContent, ChunkKey, ChunkName, ClassName, Namespace, Signature, ParentContext`. Keyed on `PK_CodeChunks`.

### 2.4 Zero-downtime reindex: staging → live swap

A full rebuild never touches the live tables until the very end. The flow (see `AzdoIndexerService.RunMonthlyAsync` + `SchemaInitializer`):

1. **Preflight** — `ValidateVectorCapabilitiesAsync`: proves the `VECTOR` type works (casts a literal), checks compat level ≥170 on-prem, enables `PREVIEW_FEATURES`, and smoke-tests a throwaway DiskANN index. Fails in seconds rather than after minutes.
2. **Create staging** — `EnsureStagingTablesAsync` drops any prior `*_Staging` and recreates fresh empty ones from the *same table bodies* (suffix `_Staging`), plus staging b-tree indexes.
3. **Index into staging** — the indexer sets `_tableSuffix = "_Staging"` so every `INSERT` targets the staging tables. No vector index exists during the staging build, so concurrent parallel inserts are cheap.
4. **Completion guard** — `RowCountAsync("CodeFiles_Staging")` must be **≥ 95% of the discovered file count**, else the run **aborts without swapping** (a partial build never clobbers a good live index).
5. **Atomic swap** — `SwapStagingToLiveAsync`, in strict order:
   - `DropLiveDependentObjectsAsync`: drop both full-text indexes, drop `dbo.SearchCode`, drop the live DiskANN index (they bind to the live `object_id`s and block the rename).
   - `RenameStagingIntoLiveAsync`: `sp_rename` live→`_Old`, staging→live, then `DROP` `_Old` (freeing the live constraint names).
   - `RenameStagingConstraintsToLiveNamesAsync`: `sp_rename` every `*_Staging`-suffixed PK/FK/unique/index/default constraint onto its live name, so the promoted tables carry canonical names.
   - `RecreateLiveSearchObjectsAsync`: recreate the DiskANN index (`TryExec`, non-fatal), ensure the full-text catalog + both full-text indexes, and re-create the `SearchCode` proc.

Live tables stay queryable right up to the rename. A build that crashes before step 5 leaves live untouched.

**`ResetAsync`** is the destructive counterpart (drops the vector index, `DELETE`s both tables). It is only invoked after the UI's double confirmation (`IndexerForm.ConfirmDestructiveWipe`).

---

## 3. Search Pipeline

Entry point: `HybridSearchService.SearchAsync` (`AzureDevOpsForager.Core/Services/Search/HybridSearchService.cs`).

### 3.1 The three-signal RRF fusion (one round-trip)

The heavy lifting is a single stored procedure, `dbo.SearchCode` (defined in `SchemaInitializer.SearchCodeProcDdl`). It fuses three ranked signals in one query:

1. **Vector** — `VECTOR_SEARCH(TABLE=dbo.CodeChunks, COLUMN=Embedding, SIMILAR_TO=@QueryVector, METRIC='cosine', TOP_N=200)`, filtered to `distance <= @MaxDistance` (default 0.5) and optionally `@ChunkType`.
2. **Chunk full-text** — `FREETEXTTABLE(dbo.CodeChunks, (ChunkContent, ChunkName, ChunkKey, ClassName, Namespace, Signature, ParentContext), @SafeText)`, filtered to `RANK >= @MinFtsRank` (default 10).
3. **File full-text** — `FREETEXTTABLE(dbo.CodeFiles, *, @SafeText)`, filtered to `RANK >= @MinFtsRank`; file hits are expanded to their chunks.

Each signal is ranked with `ROW_NUMBER()`, then combined with **Reciprocal Rank Fusion**:

```
score_signal = weight × 1 / (60 + rank)
finalScore   = VectorRRF + ChunkFtsRRF + FileFtsRRF
```

Default weights come from `Config`: `RrfVectorWeight=60`, `RrfChunkFtsWeight=30`, `RrfFileFtsWeight=30`. The `60` in the denominator is the standard RRF constant (independent of the vector weight, which also happens to default to 60). Results are returned already scored and ordered, tagged with a `MatchSource` of `Hybrid` / `Vector` / `FullText`.

`@SafeText` strips `' " & | ~ !` (FREETEXT metacharacters) and caps at 4000 chars. Both FTS blocks are skipped when the search text is empty, so a pure vector query still works.

The service calls it via `FetchFusedRowsAsync`:

```csharp
DECLARE @qv VECTOR(n) = CAST(@json AS VECTOR(n));   -- n = Config.EmbeddingDimension
EXEC dbo.SearchCode @SearchText=@txt, @QueryVector=@qv, @TopN=@topN,
   @VectorWeight=@vw, @ChunkFtsWeight=@cw, @FileFtsWeight=@fw,
   @MinFtsRank=@minRank, @MaxDistance=@maxDist;
```

The query vector is serialized to JSON (`System.Text.Json`) and cast to a `VECTOR` sized by
`Config.EmbeddingDimension` server-side (the proc's `@QueryVector` parameter is declared with the same
dimension by `SchemaInitializer`). Every returned row becomes a `(FilePath, Content, Meta)` tuple; **the metadata dictionary keys are the public snake_case API contract** (`class_name`, `chunk_type`, `start_line`, `score`, `match_source`, `vector_rrf`, `chunk_fts_rrf`, `file_fts_rrf`, `distance`, …) — do not rename them without versioning the API.

### 3.2 Cross-encoder rerank (optional second stage)

`SearchViaProcAsync` decides `doRerank = _reranker != null && Config.RerankerEnabled`. When on, it **over-fetches** a wider pool (`fetchN = max(NResults, Config.RerankerInputSize)`, default 30), reranks, then trims to `NResults`.

`ApplyRerankAsync` builds `RerankerCandidate` objects (each carries its original index + the chunk preview) and calls `IReranker.RerankAsync`. Two implementations satisfy that interface — the one that is wired in depends on config (see §5.2 / §7.3): the local ONNX `BgeReranker` scores with `bge-reranker-v2-m3`, the remote `HuggingFaceReranker` with `Qwen3-Reranker-0.6B`. Both are **fail-soft** (any failure returns the candidates in original order, `FallbackOriginalOrder`; only genuine cancellation propagates).

**Local ONNX — `BgeReranker`.** Scores each `(query, chunk)` pair jointly with `bge-reranker-v2-m3` (XLM-RoBERTa cross-encoder) in-process:

- Tokenizes with SentencePiece; applies the **fairseq +1 id offset** (`FairseqOffset`); assembles the XLM-R pair sequence `<s> query </s></s> document </s>`.
- All pairs for one query are packed into a single batched inference; `logits[row, 0]` is the relevance score.
- Truncates to `MaxSequenceLength = 512` (document trimmed first).
- A failed model init is retried no more often than every 5 minutes.

**Remote HF — `HuggingFaceReranker`.** The endpoint serves **`Qwen3-Reranker-0.6B`** in its sequence-classification form (`tomaarsen/Qwen3-Reranker-0.6B-seq-cls`) on a vLLM container. The client POSTs a Jina-style `{ "model", "query", "documents", "top_n" }` request to **`POST <url>/rerank`** (the base URL from `Config.HuggingFaceRerankUrl` with `/rerank` appended) under a bearer token. Qwen3-Reranker scores through its chat template, and that wrapping is applied **client-side**: the query carries the template prefix plus `<Instruct>:` (the task text from `Config.RerankerInstruction`) and `<Query>:` markers, and each document carries the `<Document>:` marker plus the template suffix; `model` comes from `Config.RerankerModelName`. The parser accepts both the vLLM/Jina response shape (`{"results":[{index,relevance_score}]}`) and the older TEI shape (`[{index,score}]`), mapping each returned `index` (position in `documents`) back to the candidate's `OriginalIndex`, then ordering by descending score and trimming to `topK`. No reranker model is loaded in-process. Same fail-soft contract: any HTTP/parse failure — or an empty scored list — degrades to the original order. Scale-to-zero endpoints are covered by the warm-up retry (see §4.3).

### 3.2.1 The relevance gate — returning nothing on purpose

Reranking also decides **how many** results come back. `HybridSearchService.ApplyRelevanceGate` (pure, static, unit-tested without SQL or a live cross-encoder) drops candidates the reranker scored too low, so a question this corpus cannot answer comes back empty instead of with a full page of nearest-neighbour noise. Retrieval on its own can never do this: vector search always returns its N closest chunks however distant they are, and the RRF score is a rank-fusion artifact, not a relevance measure. Three settings, in increasing bluntness:

| Setting | Kind | What it decides |
|---|---|---|
| `MinRerankScoreRatio` (0.1) | **relative** | A hit must score at least this fraction of the best score in its own result set. |
| `MinRerankTopScore` (1e-6) | absolute, tiny | If even the best score is ~0, discard the whole set — the ratio alone cannot tell "all equally good" from "all equally worthless". |
| `MinVectorOnlyRerankScore` (0.05) | absolute, per-subset | A hit with **no full-text corroboration** (both RRF legs zero, `MatchSource='Vector'`) must clear this on its own. |

`MinRerankScoreRatio` is deliberately relative: an absolute floor encodes one model's score calibration, so it silently stops working the moment the reranker is swapped. That is not hypothetical — a floor tuned on `Qwen3-Reranker-4B` returned zero results for every query once the endpoint was pointed at the 0.6B, with nothing logged, because the smaller model scores on a different scale.

`MinVectorOnlyRerankScore` is the one place that rule is knowingly broken, because score alone cannot separate the last failure mode: searching a word the codebase has never contained (`garbage`, `zebra`, `kubernetes`) still returned five files, none containing the term. Those scored *higher* (top 0.0089) than a query that was answered correctly (`how many bits in a byte`, top 0.0033), so no flat threshold rejects one without the other. Provenance separates them cleanly — every junk query was vector-only across the board, and every genuine one produced at least one `Hybrid`/`FullText` hit — so the higher bar applies only to the uncorroborated subset, where a mis-set value costs those hits rather than emptying search. All three are env-readable (`MINRERANK_SCORE_RATIO`, `MINRERANK_TOP_SCORE`, `MINVECTORONLY_RERANK_SCORE`) and echoed in the `[CONFIG]` line at boot, so a deployment's effective gate is on the record; re-measure by setting the last to `0` and reading `rerank_score` + `match_source` off the returned metadata.

Both reranker implementations fall back to **high descending pseudo-scores**, never zeros, precisely so an endpoint outage degrades to RRF order instead of tripping these gates and emptying every search.

### 3.3 FTS-only fallback

If `_embeddingService` is null, or the proc / vector path throws, `SearchAsync` degrades to `BuildFtsOnlyResponse` (`SqlFtsService`). This path:

- Runs three keyword heuristics in priority order (`SqlFtsService.Search`): a **field-assignment** heuristic (`"where is field X populated"`), **PascalCase identifier** lookup (with a Levenshtein ≤3 fuzzy retry against indexed class names), and a **stop-word-filtered FREETEXT** fallback.
- Always folds in filename matches (`SearchByFilename`).
- Blends and re-scores (`CombineResults` / `CalculateFtsScore`, which adds a +200 boost when the question names the file or class).

This is why search "keeps working" even on a database that has full-text but no vectors.

### 3.4 Chat (`/chat`)

`Server.MapChatEndpoints`: the server runs the same hybrid retrieval (`NResults=8`), concatenates the top hits into a `// File: <path>` context block, and calls `ILLMProvider.AskAsync(question, context, null)`. The Groq system prompt (in `GroqProvider.BuildSystemPrompt`) forces grounded, verbatim-code answers with citations and forbids placeholders. The response is `{ answer, sources }`. If no key is configured, `/chat` returns a friendly "not configured" message instead of failing.

---

## 4. Embedding

Embedding is an `IEmbedder` (`AzureDevOpsForager.Core/Services/Embedding/IEmbedder.cs`) — `EmbedQuery` / `EmbedPassage` (plus batch variants), returning a unit-length vector sized by `Config.EmbeddingDimension`. There are two implementations, and they run **different models** — their vectors are *not* interchangeable, so an index built with one must be fully reindexed to search with the other (§2.4's staging + atomic swap does this with zero downtime). Both L2-normalize, so cosine ranking stays valid either way.

- **`EmbeddingService`** — local ONNX. Model **e5-large-v2**, 1024-dim (requires `EmbeddingDimension=1024`), run in-process via ONNX Runtime — no Python, no API, no per-call cost. This is the offline / no-account lightweight default.
- **`HuggingFaceEmbedder`** — remote. POSTs to a HF Inference Endpoint (served by TEI) running **BAAI/bge-code-v1** — code-specialized, Qwen2.5-Coder-1.5B backbone, 1536-dim (the `EmbeddingDimension` default), 32k context, Apache 2.0, state-of-the-art on the CoIR code-retrieval benchmark (~81.8). This is what the hosted demo runs, and what lets the Server and Indexer run with **zero local ONNX**.

`HybridSearchService` depends only on `IEmbedder`, so the query path is oblivious to which one it got (and treats a null embedder as "FTS-only", §3.3).

### 4.1 Model contracts encoded in the code

These are the local `EmbeddingService` (e5-large-v2) internals; the remote `HuggingFaceEmbedder` (bge-code-v1) has a different contract, listed after.

- **1024-dim output** (mpnet was 768).
- **512-token max input** (`_maxLength`); the tokenizer truncates.
- **query/passage prefixes** are mandatory for accuracy: `EmbedQuery` prepends `"query: "`, `EmbedPassage` prepends `"passage: "`. `Embed` is the raw, prefix-free entry point.
- **Three graph inputs**: `input_ids`, `attention_mask`, and `token_type_ids`. The service probes `_session.InputMetadata` once (`_usesTokenTypeIds`) and only supplies the all-zero `token_type_ids` tensor when the model declares it (e5 does; mpnet doesn't). This is what makes the loader tolerant of either model family.
- Pooling: **mean-pool over real tokens** (attention-mask weighted), then **L2-normalize** so cosine comparisons are well-behaved. The DB stores unit vectors and searches with `METRIC='cosine'`.

The model + `vocab.txt` are resolved together: `Config.TokenizerPath` is always derived as `vocab.txt` alongside `Config.OnnxModelPath`. The service throws at construction (not on first embed) if either file is missing.

The remote `HuggingFaceEmbedder` (bge-code-v1) contracts differ:

- **1536-dim output** (the `Config.EmbeddingDimension` default).
- **Queries are instruction-prompted**: `EmbedQueryAsync` sends `<instruct>{task}\n<query>{text}`, where the task text comes from `Config.EmbeddingQueryInstruction`. **Documents/passages are embedded raw** (no prefix), per the model card.
- **32k-token context** (vs e5's 512), so long chunks are embedded without client-side truncation.
- Results are L2-normalized like the local path, so cosine ranking is unchanged.

### 4.2 Selecting the embedding source: HF vs local ONNX vs hosted `/embed`

Two gates decide which `IEmbedder` (if any) is used:

- `Config.HuggingFaceEnabled` — `true` iff **both** `HuggingFaceEmbedUrl` **and** the HF token are present (`!IsNullOrWhiteSpace(HuggingFaceEmbedUrl) && !IsNullOrWhiteSpace(HuggingFaceToken)`). `HuggingFaceEmbedUrl` / `HuggingFaceRerankUrl` live in `config.json` (not secret); the token is a secret resolved by `Config.HuggingFaceToken` → `SecretStore.Get("HF_TOKEN")` (§5.2).
- `Config.IsLocalModelConfigured` — `true` iff `OnnxModelPath` points at an `.onnx` file that **exists on disk**.

**The Server and Indexer resolve the source with different precedence — this is deliberate:**

- **Server** (`Server.RegisterServices`) prefers HF: if `Config.HuggingFaceEnabled`, it registers `HuggingFaceEmbedder`; **else if** `File.Exists(Config.OnnxModelPath)`, it registers the local `EmbeddingService`; **else** no `IEmbedder` is registered and search runs full-text-only. When a local `EmbeddingService` is registered, the Server also exposes `POST /embed` (`{ text, kind }` where `kind` = `"query"` or `"passage"`, default passage) so remote clients can embed against the server's model; if no embedder is available, `/embed` returns HTTP 503.
- **Indexer** (`AzdoIndexerService.ConfigureEmbeddingSource`) prefers a local model (self-hosters embed locally, uncapped), in this order:
  1. **`IsLocalModelConfigured`** → `_localEmbed = new EmbeddingService(...)`; embedding is **uncapped**.
  2. else **`HuggingFaceEnabled`** → `_hfEmbedder = new HuggingFaceEmbedder(HuggingFaceEmbedUrl, HuggingFaceToken)`; a GPU-backed HF endpoint reindexes an estimated ~10–15× faster than local CPU ONNX (rough estimate — GPU vs CPU, so it varies by hardware) and pulls no local model.
  3. else **`EmbeddingServiceUrl` set** → embed remotely by POSTing to `{EmbeddingServiceUrl}/embed` (the hosted demo server), subject to `HostedEmbeddingFileCap` (default 1000 files/run; the UI prompts to proceed with the top-N or cancel).
  4. else → embedding **disabled** (full-text-only index).

`EmbedPassageAsync` implements exactly that: `_localEmbed.EmbedPassage(text)` in-process, `_hfEmbedder.EmbedPassageAsync(text)` against the HF endpoint, or a JSON POST to `_embedUrl` parsing back the `vector` array.

The net effect: a recipient can run **zero-local-ONNX** (point at HF — no ~1.3 GB model download for either the Server or the Indexer), **fully local** (bundled ONNX models, uncapped, offline), or **use the hosted demo** (the shared `/embed`, capped). Local ONNX still works exactly as before and remains the no-account option.

### 4.3 Warm-up retry for scale-to-zero HF endpoints

`HuggingFaceEmbedder` (and `HuggingFaceReranker`) POST through `PostWithWarmupRetryAsync`, which tolerates the transient statuses a scale-to-zero HF endpoint returns while its GPU spins up — **503, 429, 409, 500, 502, 504**. It backs off (2s → 10s, capped) for up to **30 attempts (~5 minutes)** so a cold endpoint warms rather than failing every chunk. A genuine error (e.g. 401 bad token, 400 bad request) is **not** transient and throws immediately via `EnsureSuccessStatusCode()`. In the reranker this failure is caught and degrades fail-soft; in the embedder it propagates (a chunk that can't embed is counted and skipped by the indexer).

---

## 5. Configuration & Override Layering

`AzureDevOpsForager.Core/Config.cs` is a **static** single source of truth read in-process by all three exes. There are three layers, applied in precedence order (later wins):

```
   1. Defaults           (hard-coded in Config.cs; some seeded from env via Global.cs)
        ▼
   2. Per-exe config.json (next to the binary)         Config.LoadFromFile(path)
        ▼
   3. Shared per-user override                          Config.LoadUserOverrides()
      %LOCALAPPDATA%\AzureDevOpsForager\config.json
```

Each exe runs essentially this load sequence at startup:

```csharp
Config.LoadFromFile( Path.Combine( AppContext.BaseDirectory, "config.json" ) ); // layer 2
Config.LoadUserOverrides();   // layer 3 — SharedUserConfigPath, wins over layer 2
Config.EnsureDirectories();   // Server only; the Indexer omits this call
```

(See `Server.LoadConfiguration` and `Indexer/AzdoReindexer.cs`'s `Program.LoadConfiguration`.)

### 5.1 The config file format

Flat JSON string→string map, deserialized with Newtonsoft. `LoadFromFile` is **totally forgiving**: a missing file, malformed JSON, or a null map all leave the current values in place — startup never fails on a bad config. Only recognized keys take effect; each is applied by one of the `Apply*Settings` helpers:

- `ApplyPathAndModelSettings` — `DataRoot`, `SourceDir`, `OnnxModelPath`, `ModelDownloadUrl`, `EmbeddingDimension`, `EmbeddingQueryInstruction`, `HostedEmbeddingFileCap`
- `ApplyServerSettings` — `Port`, `SqlConnectionString`, `AzdoVectorConnectionString`, `ServerUrl`, `EmbeddingServiceUrl`, `HuggingFaceEmbedUrl`, `HuggingFaceRerankUrl` (the two HF endpoint URLs are plain config, **not** secrets — the token protects them; see §5.2)
- `ApplyAzureSettings` — `AzureUrl`, `AzurePAT`, `AzureProject`, `AzureTfvcRoot`
- `ApplySourceSelectionSettings` — `SourceType`, `GitRepository`, `GitBranch`, `GitHubRepoUrl`, `GitHubToken`, `IncludeGlobs`, `ExcludeGlobs`
- `ApplySearchTuningSettings` — `RerankerModelPath`, `RerankerInstruction`, `RerankerModelName`, `RerankerEnabled`, `RerankerInputSize`, `RrfVectorWeight`, `RrfChunkFtsWeight`, `RrfFileFtsWeight`, `MinFtsRank`, `MaxVectorDistance`

Numeric/boolean keys are guarded by `TryParse`; a garbled value leaves the default. `MaxVectorDistance` is parsed with **invariant culture** so a config authored under any locale reads consistently.

**Adding a config key:** add the property to `Config`, then add a `config.TryGetValue(...)` line in the matching `Apply*Settings` helper. That's the whole contract — `LoadFromFile` and `SaveUserOverride` both key off the same string names.

### 5.2 Where secrets come from

- Azure DevOps `AzureUrl` / `AzurePAT` default from environment (`AZDO_URL`, `AZDO_PAT`) via `Global.cs`, and can be overridden by config.
- **Named secrets live in one consolidated, AES-encrypted `secrets.enc`** beside the binary — an encrypted JSON dictionary holding all of the app's named secrets, currently `GROQ_API_KEY` and `HF_TOKEN`. This replaces the earlier single-secret `groqapikey.enc` (still read as a fallback for the Groq key only). `SecretStore` (`Core/Services/Utilities/SecretStore.cs`) is the single reader/writer:
  - **Resolution (`SecretStore.Get(name)`):** the environment variable of the same name **wins** when set; otherwise the value comes from `secrets.enc`; for `GROQ_API_KEY` only, it then falls back to the legacy `groqapikey.enc`. Missing/unreadable file → `null` (feature stays unconfigured, degrades gracefully — never throws on read).
  - **Write:** `Server --set-secret NAME VALUE` (e.g. `Server --set-secret HF_TOKEN hf_xxx`) reads the full dictionary, **merges** the one named secret without disturbing the others, and re-encrypts — so multiple secrets coexist in the one file. `Server --set-groq-key gsk_xxx` is a back-compat alias that writes under `GROQ_API_KEY`. Encryption is `SecretBox`, obfuscation-grade AES-256 from an app-embedded passphrase — deliberately not a vault.
- The **Groq key is never a config key.** `GroqProvider` resolves it via `SecretStore` (`GROQ_API_KEY` env → `secrets.enc` → legacy `groqapikey.enc`).
- The **HF token is never a config key or source-embedded.** `Config.HuggingFaceToken` resolves it via `SecretStore.Get("HF_TOKEN")` (`HF_TOKEN` env → `secrets.enc`). Null/empty means HF is not authorized, so `HuggingFaceEnabled` is `false` and the app falls back to local ONNX. Only the two `HuggingFaceEmbedUrl` / `HuggingFaceRerankUrl` **URLs** go in `config.json`.

### 5.3 How the Indexer persists choices for the whole toolchain

`Config.SaveUserOverride(key, value)` writes a single key into the shared per-user file (`%LOCALAPPDATA%\AzureDevOpsForager\config.json`), **merging** into any existing keys so it never clobbers a previously-saved one. Write failures are swallowed (the override layer is a convenience). The Indexer uses this so a choice it makes for the user is automatically picked up by the local Server and clients with no manual file editing:

- After a successful build, `IndexerForm.PromptSetLocalServerDb` offers to `SaveUserOverride("SqlConnectionString", ...)` so the local Server serves the just-built database.
- The Download-model wizard (`IndexerForm.ApplyModelPath`) calls `SaveUserOverride("OnnxModelPath", ...)` so the machine embeds locally (uncapped) thereafter.

Because all three exes call `LoadUserOverrides()` at startup, these follow the user everywhere.

---

## 6. Build & Test

### 6.1 Building

Standard per-project `dotnet build`:

```bash
dotnet build AzureDevOpsForager.Core
dotnet build AzureDevOpsForager.Server
dotnet build AzureDevOpsForager.Indexer     # net10.0-windows → build on Windows
dotnet build AzureDevOpsForager.WinForms    # net48 → build on Windows
```

Framework notes that matter when you touch project files:

- `Core` targets `netstandard2.0` (`LangVersion=latest`), so it compiles on Linux and Windows and is consumable from `net48`. Keep it dependency-light: its only NuGet refs are `Microsoft.Data.SqlClient`, `Newtonsoft.Json`, `Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers`, `System.Net.Http`.
- `Server` and `Indexer` are pinned to `RuntimeIdentifier=win-x64`, `SelfContained=false`. The Server is `Microsoft.NET.Sdk.Web` and copies `wwwroot/**` to output (`PreserveNewest`) so `dotnet run` — not just publish — serves the UI.
- **`System.Text.Json` is pinned to 9.0.0** in both `Server` and `Indexer` to align with `Microsoft.ML.Tokenizers` (the reranker) and avoid the 8.0/9.0 conflict. Don't downgrade it.
- `WinForms` (`net48`) references both `Shared` and `Core`. `Shared` is also `net48`.

### 6.2 Tests

`AzureDevOpsForager.Tests` (`net10.0-windows`, xUnit) references `Core` and `Indexer`. Current suites are pure-logic (no live SQL, no models):

- `ConfigTests` — config layering / parsing.
- `GitHubServiceTests` — `ParseRepoUrl` across URL shapes.
- `RoslynChunkerTests`, `RoslynMetadataTests` — chunking + metadata extraction.
- `SourceFilterTests` — glob include/exclude semantics.

```bash
dotnet test AzureDevOpsForager.Tests
```

### 6.3 Running-Indexer file-lock gotcha

The Indexer, Server, and Tests all bind native ONNX Runtime / Roslyn assets under `win-x64`. **If the Indexer (or Server) is running, a rebuild of `Indexer`/`Core`/`Tests` can fail with a file lock** on those native DLLs. Close the running exe before rebuilding, or build to a separate output directory. (The Tests project intentionally pins the same RID as the Indexer so its native assets resolve identically.)

---

## 7. Extending the System

The three primary seams are all interfaces in `Core`. Each is small and each existing implementation is a clean template.

### 7.1 Add a source adapter — implement `ISourceProvider`

`AzureDevOpsForager.Core/Services/Sources/ISourceProvider.cs`:

```csharp
public interface ISourceProvider
{
   string SourceDescription { get; }
   Task<List<SourceFileInfo>> GetAllFilesAsync();          // already glob-filtered
   Task<string> GetFileContentAsync( SourceFileInfo file );
   Task<(string author, string date)> GetBasicMetadataAsync( SourceFileInfo file );
}
```

Contract details from the existing three implementations (`TfvcSourceProvider`, `GitSourceProvider`, `GitHubSourceProvider`):

- `GetAllFilesAsync` **must return an already-filtered list** — apply `SourceFilterOptions.ShouldInclude(relativePath)` yourself so the indexer never re-filters.
- Each `SourceFileInfo` carries **two paths**: `RelativePath` (normalized forward-slash, the canonical cross-provider identity used for globbing + storage) and `NativePath` (the provider's own form used to fetch content). Keep them distinct.
- `GetBasicMetadataAsync` is advisory — return `("", "")` if you can't cheaply resolve blame (as `GitHubSourceProvider` does; the zipball carries no per-file history).
- Fetch failures should return `null` from `GetFileContentAsync` rather than throw; the indexer counts and skips them.

Wiring it in: the factory is `AzdoIndexerService.BuildSourceProvider(SourceFilterOptions)`, which switches on `Config.SourceType`. Add your `case`, add a matching `SourceType` value, and (for the desktop UX) add a source sub-panel in `IndexerForm.BuildSourceSection` + a branch in `ApplyConfigFromForm`. Nothing in the indexing pipeline (`IndexFilesAsync`, Roslyn chunking, embedding, write) needs to change — that's the point of the interface.

### 7.2 Add an LLM provider — implement `ILLMProvider`

`AzureDevOpsForager.Core/Services/Chat/ILLMProvider.cs`:

```csharp
public interface ILLMProvider
{
   Task<string> AskAsync( string question, string context, List<object> conversationHistory );
   void ResetConversation();
   string ProviderName { get; }
   bool IsConfigured { get; }        // false when no key/config; lets /chat degrade cleanly
}
```

Follow `GroqProvider` as the template:

- Resolve the key yourself (env var, then encrypted-file fallback via `SecretBox`), set `IsConfigured` accordingly, and **never throw** from `AskAsync` — return an inline error string (the chat UI renders whatever comes back).
- Build the messages array as `[system prompt, recent history (cap it — Groq uses the last 10 turns), user turn with the code context prepended]`.
- Keep temperature low for factual code answers (Groq uses 0.1).

Register it: in `Server.RegisterServices`, replace the singleton registration `builder.Services.AddSingleton<ILLMProvider>(sp => new GroqProvider())`. The `/chat` endpoint is provider-agnostic — it only touches `IsConfigured` and `AskAsync`. (The `GroqChatService` on the *client* side is just a named `BaseChatService` subclass; it holds no provider logic because the client is a thin HTTP client of `/chat`.)

### 7.3 Swap the reranker — implement `IReranker`

`AzureDevOpsForager.Core/Services/Reranking/IReranker.cs`:

```csharp
Task<IReadOnlyList<RerankerResult>> RerankAsync(
   string query, IReadOnlyList<RerankerCandidate> candidates, int topK,
   CancellationToken cancellationToken = default );
```

- Implementations **MUST be fail-soft**: on any model/inference failure, return the candidates in original order truncated to `topK` (see `BgeReranker.FallbackOriginalOrder` / `HuggingFaceReranker.FallbackOriginalOrder`). Only cancellation may throw.
- `RerankerCandidate.OriginalIndex` is the back-pointer the caller uses to reunite a score with its full hit — preserve it. (`HuggingFaceReranker` maps each returned `index` — the position in the posted `texts` array — back through `candidates[index].OriginalIndex`.)
- Two implementations already ship: **`BgeReranker`** (local ONNX) and **`HuggingFaceReranker`** (remote, `POST <url>/rerank`). `Server.RegisterServices` picks between them: if `Config.RerankerEnabled && Config.HuggingFaceEnabled && HuggingFaceRerankUrl` is set → `new HuggingFaceReranker(HuggingFaceRerankUrl, HuggingFaceToken)`; **else if** `Config.RerankerEnabled && File.Exists(Config.RerankerModelPath)` → `new BgeReranker()`; otherwise none. `HybridSearchService` treats a null reranker as "no second stage". Note reranking is optional even when embedding is HF-backed — you can point the embedder at HF and leave the reranker local (or off).

### 7.4 Tuning without code

Most retrieval behavior is config-tunable (no rebuild): RRF weights, `MinFtsRank`, `MaxVectorDistance`, `RerankerInputSize`, `RerankerEnabled`. See §5.1.

---

## 8. Deployment

### 8.1 Server on Azure App Service (Linux, Kestrel)

The Server is a self-contained-off `net10.0` app. `Server.BuildApplication` binds Kestrel to `Config.Port` (default 8000) via `ListenAnyIP`, and `AddServerHeader=false`. It can also run as a **Windows Service** (`WindowsServiceHelpers.IsWindowsService()` → `UseWindowsService()`), but the hosted demo runs on **Azure App Service (Linux) behind Kestrel**.

Deployment shape:

- Publish the Server, point `SqlConnectionString` at your **Azure SQL** database (the schema is created/managed by the Indexer, or by `Schema/CreateSchema.sql`).
- The connection-string builder (`ConnectionStringBuilder`) detects Azure SQL by the `database.windows.net` host token and forces `Encrypt=True;TrustServerCertificate=False` + SQL auth (Azure SQL has no Windows auth). On-prem gets `TrustServerCertificate=True`.
- Serve the web UI from `wwwroot` (`UseDefaultFiles` + `UseStaticFiles`).
- `ApplicationStarted` warms the SQL FTS connection and logs the indexed file count.

### 8.2 Secrets on the server (Groq key, HF token)

Named secrets go in the consolidated encrypted `secrets.enc`, or as environment variables — **env var wins** (§5.2). Two supported sources per secret:

- **Env var:** set `GROQ_API_KEY=gsk_...` and/or `HF_TOKEN=hf_...` in App Service application settings.
- **Encrypted file:** run the one-shot `Server --set-secret GROQ_API_KEY gsk_...` and/or `Server --set-secret HF_TOKEN hf_...`, each of which merges the named secret into `secrets.enc` beside the binary (via `SecretBox.Encrypt`). The server decrypts each whenever its matching env var is unset. `Server --set-groq-key gsk_...` remains as a back-compat alias for the Groq key.

Neither secret is **ever** shipped to a client or stored as a config.json key. Search works without the Groq key (only `/chat` degrades to a "not configured" message). Without `HF_TOKEN` (or a HF URL), the server simply falls back to local ONNX / FTS-only per §4.2.

### 8.3 Model bundle for the Download wizard

Self-hosters get the embedding model via the Indexer's **Download** link (`IndexerForm.DownloadModelAsync`), which streams `Config.ModelDownloadUrl` (a hosted `.zip`, default an Azure Blob) to a temp file with live percentage logging, extracts it into a chosen folder, resolves the `.onnx`, and persists the path via `SaveUserOverride("OnnxModelPath", ...)`. Host your own bundle by overriding `ModelDownloadUrl` in config. The two **local lightweight models** (the hosted demo instead runs `bge-code-v1` + `Qwen3-Reranker-0.6B` on HF endpoints):

| Model | Files (under `models/`) | Purpose |
|-------|-------------------------|---------|
| `e5-large-v2` | `e5-large-v2/e5-large-v2.onnx`, `vocab.txt` | 1024-dim embeddings (set `EmbeddingDimension=1024`) |
| `bge-reranker-v2-m3` | `bge-reranker-v2-m3-onnx/model.onnx`, `sentencepiece.bpe.model` | cross-encoder rerank |

Both are permissively licensed and redistributable (e5-large-v2 under MIT, bge-reranker-v2-m3 under Apache 2.0). The reranker is optional (`RerankerEnabled=false` → RRF-only). `./models/download-models.ps1` fetches both for a from-source setup.

**The local bundle is itself optional when HF is configured.** Point `HuggingFaceEmbedUrl` (and optionally `HuggingFaceRerankUrl`) at HF Inference Endpoints serving the code-specialized models (`bge-code-v1` on TEI, `Qwen3-Reranker-0.6B-seq-cls` on vLLM — keep `EmbeddingDimension` at its 1536 default) and supply `HF_TOKEN` (§5.2, §8.2), and the Server + Indexer load **no local ONNX** — no ~1.3 GB model download at all — while a GPU-backed endpoint reindexes an estimated ~10–15× faster than local CPU ONNX (minutes → seconds, hardware-dependent; scale-to-zero cold starts are absorbed by the warm-up retry, §4.3). The Download wizard is only for the offline / no-account (local ONNX) path.

### 8.4 Zero-downtime prod reindex

Because the Indexer builds into `*_Staging` and only swaps in on the 95% completion guard (§2.4), you can rebuild the prod index against a live Server without downtime and without risk of promoting a partial build. Point the Indexer's destination at the same Azure SQL database the Server reads.

---

## 9. Appendix: End-to-End Data Flow

**Indexing (write path):**

```
IndexerForm → AzdoIndexerService.RunMonthlyAsync
  → ValidateVectorCapabilitiesAsync (preflight)
  → EnsureStagingTablesAsync
  → InitializeServices (source provider + embedding source from Config)
  → source.GetAllFilesAsync()                       [ISourceProvider, glob-filtered]
  → Parallel.ForEachAsync(files):
       source.GetFileContentAsync(file)
       RoslynMetadataExtractor.Extract(content)      → FileMetadata → UpsertCodeFile
       RoslynChunker.ChunkFile(path, content)        → CodeChunkDto[] (semantic chunks)
       EmbedPassageAsync(chunk)                       → local ONNX | HF endpoint | hosted /embed → float[EmbeddingDimension]
       InsertCodeChunk(... CAST(@embedding AS VECTOR({Config.EmbeddingDimension})) ...)
  → completion guard (staged ≥ 95%)
  → SwapStagingToLiveAsync (drop deps → rename → rename constraints → recreate index+FTS+proc)
```

**Query (read path):**

```
client → POST /query {question, n_results}
  → HybridSearchService.SearchAsync
       IEmbedder.EmbedQuery(q) → float[EmbeddingDimension]  [EmbeddingService (local ONNX) | HuggingFaceEmbedder] (or FTS-only fallback)
       EXEC dbo.SearchCode  (VECTOR_SEARCH + chunk-FTS + file-FTS, RRF-fused, one round-trip)
       [optional] IReranker.RerankAsync (over-fetch → rerank → trim)  [BgeReranker (local ONNX) | HuggingFaceReranker (POST /rerank)]
  → SearchResponse {Ids, Documents, Metadatas}
```

**Chat (read path + LLM):**

```
client → POST /chat {question}
  → HybridSearchService.SearchAsync (n=8) → build "// File: <path>" context block
  → ILLMProvider.AskAsync(question, context, null)   [Groq, grounded system prompt]
  → {answer, sources}
```

---

*Every load-bearing constant (the `EmbeddingDimension`-driven vector size — 1536 default, 1024 local e5 — RRF 1/(60+rank), the 95% swap guard, the 512-token local-model cap, the fairseq +1 offset, the 0.5 max cosine distance, the 30-candidate rerank pool, the 1000-file hosted cap, the ~5-min / 30-attempt HF warm-up retry) is defined in `Config.cs`, `SchemaInitializer.cs`, `EmbeddingService.cs`, `BgeReranker.cs`, or the HF classes (`HuggingFaceEmbedder.cs`, `HuggingFaceReranker.cs`) — grep there before changing behavior.*
