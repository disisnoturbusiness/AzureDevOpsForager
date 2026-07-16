# Functional Matrix

A capability-by-capability map of what Azure DevOps Forager does, where it lives in the
code, and how each piece is verified. Everything listed is **implemented and working**;
the columns distinguish what is covered by an automated unit test from what is proven
end-to-end by the [live demo](https://azuredevops.aidataforager.com) or exercised manually.

**"Verified by" legend**

| Marker | Meaning |
|--------|---------|
| **Unit** | Covered by an automated test in `AzureDevOpsForager.Tests` (suite named). |
| **Demo** | Proven end-to-end by the hosted demo pipeline (HF embed + rerank → SQL vectors → RRF → Groq chat). |
| **Manual** | Exercised by hand / in normal operation; no automated coverage yet. |

> The demo runs the **full retrieval + chat pipeline** on every request, so any capability
> marked **Demo** is continuously exercised in production against a real index.

---

## Search & retrieval

| Capability | Implementation | Verified by |
|------------|----------------|-------------|
| Hybrid RRF search (vector + chunk-FTS + file-FTS fused) | `dbo.SearchCode` proc · `Core/Services/Search/HybridSearchService.cs` | Demo |
| Dense vector search (`VECTOR_SEARCH` + DiskANN, `VECTOR(n)` sized by `EmbeddingDimension`, default 1536) | `Core/Services/Storage/SchemaInitializer.cs` | Demo |
| Full-text search (chunk-level + file-level `FREETEXT`) | `Core/Services/Search/SqlFtsService.cs` | Demo |
| Graceful fallback to full-text-only when embeddings unavailable | `HybridSearchService.BuildFtsOnlyResponse` | Demo |
| Filename search (`POST /search_by_filename`) | `HybridSearchService.SearchByFilename` | Demo |

## Reranking

| Capability | Implementation | Verified by |
|------------|----------------|-------------|
| Hosted cross-encoder rerank — `Qwen3-Reranker-4B` (seq-cls, vLLM `/rerank`, Qwen3 chat-template wrapping client-side) | `Core/Services/Reranking/HuggingFaceReranker.cs` | Demo *(the hosted demo is HF-backed)* |
| Local ONNX cross-encoder rerank — `bge-reranker-v2-m3` (query,chunk) scoring | `Core/Services/Reranking/BgeReranker.cs` | Manual |
| Fail-soft: fall back to RRF order if reranker errors/unavailable | `HybridSearchService.ApplyRerankAsync` | Demo |
| Toggle reranking on/off (`RerankerEnabled`) | `Core/Config.cs` | **Unit** — `ConfigTests` |

## Embeddings

| Capability | Implementation | Verified by |
|------------|----------------|-------------|
| Local ONNX embeddings — `e5-large-v2`, 1024-dim (requires `EmbeddingDimension=1024`), L2-normalized, `query:`/`passage:` prefixed | `Core/Services/Embedding/EmbeddingService.cs` | Manual |
| Hugging Face endpoint embeddings — `bge-code-v1`, 1536-dim, `<instruct>`/`<query>` query prompt, raw documents (scale-to-zero warm-up retry) | `Core/Services/Embedding/HuggingFaceEmbedder.cs` | Demo *(the hosted demo is HF-backed)* |
| Hosted `/embed` fallback for the Indexer (capped by `HostedEmbeddingFileCap`) | `Core/Services/Search/HybridSearchService.cs` | Demo |
| Batch embedding (`EmbedQueryBatch` / `EmbedPassageBatch`) | `Core/Services/Embedding/IEmbedder.cs` | Demo |

## Chat (RAG)

| Capability | Implementation | Verified by |
|------------|----------------|-------------|
| Groq LLM chat (`llama-3.3-70b`, OpenAI-compatible) | `Core/Services/Chat/GroqProvider.cs` | Demo |
| Grounded RAG — retrieve top chunks, answer from snippets, return sources (`POST /chat`) | `Server/Server.cs:MapChatEndpoints` | Demo |
| Feedback logging — thumbs up/down (`POST /chat/feedback`) | `Core/Services/Chat/BaseChatService.cs:LogFeedback` | Manual |
| Known-answers cache (per-user, normalized question) | `BaseChatService.{Add,Check}KnownAnswers` | Manual |
| Retry-with-more-detail on thumbs-down (desktop) | `BaseChatService.RetryWithMoreDetailAsync` | Manual |

## Indexing

| Capability | Implementation | Verified by |
|------------|----------------|-------------|
| Roslyn semantic chunking (class/method boundaries, 200–400 tokens, overlap) | `Indexer/Indexing/RoslynChunker.cs` | **Unit** — `RoslynChunkerTests` |
| Roslyn metadata extraction (namespace, type, base, interfaces, members, enums) | `Indexer/Indexing/RoslynMetadataExtractor.cs` | **Unit** — `RoslynMetadataTests` |
| Zero-downtime reindex — build to staging, completion-guard, atomic `sp_rename` swap | `Core/Services/Storage/SchemaInitializer.cs:SwapStagingToLiveAsync` | Manual |
| Staging-table isolation (live index stays queryable during rebuild) | `SchemaInitializer.EnsureStagingTablesAsync` | Manual |
| DiskANN vector index creation (deferred until ≥100 vectors) | `Core/Services/Storage/SchemaInitializer.cs` | Demo |

## Sources

| Capability | Implementation | Verified by |
|------------|----------------|-------------|
| Azure DevOps **TFVC** | `Core/Services/Sources/TfvcSourceProvider.cs` | Manual |
| Azure DevOps **Git** (branch selectable) | `Core/Services/Sources/GitSourceProvider.cs` | Manual |
| **GitHub** (single zipball download, no Git dependency) | `Core/Services/Sources/GitHubSourceProvider.cs` | **Unit** — `GitHubServiceTests` (URL parsing) · Demo (indexes `eShopOnWeb`) |
| Include/exclude glob filtering (excludes win; backslash-normalized) | `Core/Services/Sources/SourceFilterOptions.cs:ShouldInclude` | **Unit** — `SourceFilterTests` |

## Configuration & tuning

| Capability | Implementation | Verified by |
|------------|----------------|-------------|
| Config precedence — per-exe `config.json` → per-user override → env vars | `Core/Config.cs:LoadFromFile,LoadUserOverrides` | **Unit** — `ConfigTests` |
| RRF fusion weights (`RrfVectorWeight` 60 / `RrfChunkFtsWeight` 30 / `RrfFileFtsWeight` 30, k=60) | `Core/Config.cs` | **Unit** — `ConfigTests` (parse) · Demo (effect) |
| Candidate thresholds (`MaxVectorDistance` 0.5, `MinFtsRank` 10) | `Core/Config.cs` | **Unit** — `ConfigTests` |
| Reranker pool size (`RerankerInputSize` 30) | `Core/Config.cs` | **Unit** — `ConfigTests` |
| Dimension-driven vector schema (`EmbeddingDimension` 1536 default; 1024 for local e5) flows into `VECTOR(n)`, DiskANN, `dbo.SearchCode` | `Core/Config.cs` · `Core/Services/Storage/SchemaInitializer.cs` | Demo (runs 1536) |
| Hosted model prompts/name (`EmbeddingQueryInstruction`, `RerankerInstruction`, `RerankerModelName`) | `Core/Config.cs` | Demo (hosted paths use them) |
| Response caps (`MaxContentLength` 1500, `HostedEmbeddingFileCap` 1000) | `Core/Config.cs` | Manual |

## Security

| Capability | Implementation | Verified by |
|------------|----------------|-------------|
| Server-side-only secrets — Groq key + HF token never shipped to any client | `Server/Server.cs:RegisterServices` · `GroqProvider.cs` | Demo (thin clients call `/chat`) |
| Secrets via env vars (`GROQ_API_KEY`, `HF_TOKEN`) — win over file | `Core/Config.cs` | Demo (App Settings on the demo) |
| Encrypted secrets at rest (`secrets.enc`, `Server --set-secret NAME VALUE`) | `Core/Services/Utilities/SecretStore.cs` | Manual |

## Clients

| Capability | Implementation | Verified by |
|------------|----------------|-------------|
| Web UI — search + chat, module/filename filters, health indicator | `Server/wwwroot/` | Demo |
| Desktop chat viewer (WinForms, .NET 4.8, thin client of `/chat`) | `WinForms/GroqMainForm.cs` | Manual |
| Desktop indexer (single-window wizard; also runs headless) | `Indexer/IndexerForm.cs` | Manual |

## Server API & operations

| Capability | Implementation | Verified by |
|------------|----------------|-------------|
| Windows Service install/uninstall (`Server --install` / `--uninstall`, self-elevating) | `Server/Server.cs:TryHandleServiceCommand` | Manual |
| Health + status endpoints for monitoring | `Server/Server.cs:MapHealthEndpoint` / `MapInfoEndpoints` | Demo |

### HTTP endpoints

| Method | Route | Purpose |
|--------|-------|---------|
| GET  | `/health` | Index availability + file/vector counts (liveness/monitoring). |
| POST | `/query` | Hybrid RRF search over a question. |
| POST | `/embed` | Embed one text (server ONNX model). |
| POST | `/embed_batch` | Embed many texts in one forward pass. |
| POST | `/search_by_filename` | Match on file names only. |
| POST | `/chat` | Retrieval-augmented Q&A grounded in code; returns answer + sources. |
| POST | `/chat/feedback` | Log thumbs up/down on a chat answer. |
| GET  | `/systems` | File-type facet counts (drives the UI filter). |
| GET  | `/collections` | Index statistics (file counts). |
| GET  | `/status` | Plain-text status page (endpoints, backend, counts). |

---

## Test coverage summary

**30 tests, all green** — 19 methods (16 `[Fact]` + 3 `[Theory]`) across 5 suites in `AzureDevOpsForager.Tests`:

| Suite | Focus |
|-------|-------|
| `ConfigTests` | Config load, typed-value parsing, missing-file safety, env/override precedence. |
| `RoslynChunkerTests` | Chunk boundaries, line spans, member-name surfacing, empty input. |
| `RoslynMetadataTests` | Namespace/class/base/kind, method + property + enum extraction. |
| `SourceFilterTests` | Include/exclude glob logic, exclude-wins, path normalization. |
| `GitHubServiceTests` | Repo-URL parsing across HTTPS/SCP/`.git`/empty shapes. |

The unit suite targets the deterministic, logic-heavy units (chunking, metadata, config, filtering,
URL parsing). The retrieval + chat pipeline is covered end-to-end by the live demo rather than by
unit tests, since it depends on SQL Server 2025 vectors and hosted model endpoints.
