# Azure DevOps Forager

[**▶ Live demo — azuredevops.aidataforager.com**](https://azuredevops.aidataforager.com)  ·  [User Guide](docs/USER_GUIDE.md)  ·  [Developer Guide](docs/DEVELOPER_GUIDE.md)  ·  [Functional Matrix](docs/FUNCTIONAL_MATRIX.md)  ·  MIT licensed

**Semantic + lexical code search over your codebase, powered by SQL Server 2025 native vectors, a cross-encoder reranker, and pluggable embeddings — run fully offline on local ONNX, or point at a Hugging Face endpoint — with a grounded LLM chat on top.**

Azure DevOps Forager indexes a codebase (Azure DevOps TFVC, Azure DevOps Git, or GitHub) into SQL Server 2025's
native `VECTOR` type, then answers questions with a **hybrid retrieval pipeline** — dense vector search +
two full-text signals fused with Reciprocal Rank Fusion, re-ranked by a `bge-reranker-v2-m3` cross-encoder,
and (optionally) explained by an LLM that only ever sees the snippets the server retrieved.

Embedding and reranking each run **either in-process via local ONNX or remotely via a Hugging Face Inference
Endpoint** — the same vectors and the same pipeline either way. Run it zero-local-ONNX against Hugging Face,
fully offline on local models, or try the hosted demo.

No data leaves your infrastructure except the question and the retrieved snippets, and the LLM API key
lives **only on the server** — the desktop and web clients are thin clients that never hold a key.

---

## Why it's interesting

- **SQL Server 2025 native vectors.** Embeddings are stored in a real `VECTOR(1024)` column with a
  DiskANN vector index and queried with `VECTOR_SEARCH` — no separate vector database to run.
- **Hybrid retrieval with RRF.** A single stored procedure (`dbo.SearchCode`) fuses three ranked signals
  — dense vector similarity, chunk-level full-text, and file-level full-text — using Reciprocal Rank
  Fusion, so exact-name matches and semantic matches both surface.
- **Cross-encoder reranking.** The RRF candidate pool is re-scored by `bge-reranker-v2-m3`, an
  XLM-RoBERTa cross-encoder, for a precision boost over bi-encoder-only ranking. It runs either locally
  via ONNX or against a Hugging Face endpoint, behind one `IReranker` interface, and is fail-soft — if
  the reranker is unavailable the results simply fall back to their RRF order.
- **Pluggable embeddings — local or hosted.** Queries and passages are embedded with `e5-large-v2`
  (1024-dim, L2-normalized, `query:` / `passage:` prefixed) behind an `IEmbedder` interface. The same
  vectors come from **local ONNX Runtime** (`EmbeddingService`, fully offline, no API cost) or a
  **Hugging Face Inference Endpoint** (`HuggingFaceEmbedder`) — self-hosters choose per deployment.
- **No ~1.3 GB model download when hosted.** Point at Hugging Face and the Server and Indexer load no
  local ONNX at all; a GPU-backed endpoint reindexes an estimated ~10–15× faster than local CPU ONNX
  (minutes → seconds, hardware-dependent), and scale-to-zero endpoints are handled with a warm-up retry.
- **Semantic chunking with Roslyn.** C# is parsed with Roslyn, so chunks fall on class/method boundaries
  and carry real metadata (namespace, base class, interfaces, method/property names, type kind, …).
- **Zero-downtime reindex.** A full rebuild indexes into staging tables and atomically swaps them in only
  after a completion check — the live index is never left half-built.
- **Secrets stay server-side and encrypted.** Named secrets (the Groq chat key, the Hugging Face token)
  live only on the server — as environment variables (`GROQ_API_KEY`, `HF_TOKEN`), or encrypted at rest
  in a single `secrets.enc`. The web UI and desktop viewer call the server's `/chat` endpoint; no key is
  ever shipped to a client.

---

## Architecture

```
                ┌─────────────────────────────────┐         ┌──────────────────────────┐
   Web UI ─────▶│                                 │         │  SQL Server 2025         │
 (wwwroot)      │   Azure DevOps Forager Server   │────────▶│  CodeFiles / CodeChunks  │
                │   (ASP.NET Core, :8000)         │  query  │  VECTOR(1024) + DiskANN  │
 Desktop ──────▶│   /query /chat /systems         │         │  + full-text indexes     │
  chat          │   /health  + web UI             │         │  dbo.SearchCode (RRF)    │
                │                                 │         └──────────────────────────┘
                │   e5 embed → RRF →              │──▶ HF endpoint (embed + rerank), or local ONNX
                │   bge rerank → Groq             │──▶ Groq API (key + HF token = server secrets)
                └─────────────────────────────────┘

   Indexer (WinForms or headless)
     pick a source ─▶ TFVC / Azure DevOps Git / GitHub
     Roslyn chunk + e5 embed (local ONNX or HF) ─▶ staging tables ─▶ atomic swap ─▶ live index
```

### Projects

| Project | Target | Role |
|---------|--------|------|
| `AzureDevOpsForager.Core` | netstandard2.0 | Embeddings, search, reranking, chat provider, sources, schema/DDL, config |
| `AzureDevOpsForager.Server` | net8.0 | ASP.NET Core server — `/query`, `/chat`, `/systems`, `/health`, web UI; holds the server secrets (Groq key, HF token); reads SQL |
| `AzureDevOpsForager.Indexer` | net8.0-windows | Single-window WinForms indexer (also runnable headless) — pick a source + target DB, build the index |
| `AzureDevOpsForager.WinForms` | net48 | Desktop chat viewer — a thin client of the server's `/chat` |
| `AzureDevOpsForager.Shared` | net48 | Shared WinForms UI base + utilities |

---

## How search works

1. **Embed the query** with `e5-large-v2` (prefixed `query:`), L2-normalized to a `VECTOR(1024)`.
2. **`dbo.SearchCode`** runs three ranked signals in one round-trip:
   - dense `VECTOR_SEARCH` over `CodeChunks.Embedding` (top-N candidate pool),
   - `FREETEXT` over chunk content,
   - `FREETEXT` over file content,
   and fuses them with **Reciprocal Rank Fusion** (`weight × 1/(k + rank)` with k=60, default weights 60/30/30).
3. **Over-fetch** the top RRF candidates and **rerank** them with the `bge-reranker-v2-m3` cross-encoder
   (each `(query, chunk)` pair scored directly), then keep the top *N*.
4. If embeddings are unavailable, the pipeline **degrades gracefully** to full-text-only search.

For chat, the server runs that retrieval, builds a context block from the top hits, and asks the LLM to
answer **grounded in the retrieved code** — returning the answer plus its sources.

---

## Quick start

### Option A — try the hosted demo (zero config)

The shipped clients default to a hosted demo server (`ServerUrl` in config), which serves a sample index
built over a public OSS repository. Just run the desktop viewer, or open the demo server's URL in a
browser, and ask a question. No database, no models, no key required.

### Option B — self-host

**Prerequisites**
- SQL Server 2025 (or Azure SQL) with the vector preview enabled and Full-Text Search installed.
- .NET 8 SDK (for the server + indexer) and .NET Framework 4.8 (for the desktop viewer).
- Embeddings + reranking can run several ways (see [Embedding & reranking](#embedding--reranking)):
  the two local ONNX models, **or** Hugging Face endpoint URLs + an `HF_TOKEN` (no local models).

**1. Get the embedding + reranking models — or skip for Hugging Face**

For the fully local path, download the two ONNX models:
```powershell
./models/download-models.ps1
```
To run against Hugging Face instead, skip this step entirely — set `HuggingFaceEmbedUrl` /
`HuggingFaceRerankUrl` in `config.json` and supply an `HF_TOKEN` (see below).

**2. Create the schema**

Run `Schema/CreateSchema.sql` against a fresh database (the Indexer also creates it automatically on first
connect).

**3. Configure**
```bash
cp config.sample.json config.json     # then edit
```
Set at least `SqlConnectionString` and `AzdoVectorConnectionString` (usually the same value), then either
`OnnxModelPath` (local) or `HuggingFaceEmbedUrl` + `HuggingFaceRerankUrl` (hosted). `config.json` is
gitignored — it can hold a connection string and the non-secret HF URLs. The `HF_TOKEN` and Groq key are
**not** config keys (see the run step below).

**4. Build the index**

Run the Indexer, choose a source (TFVC / Azure DevOps Git / GitHub), point it at your target database,
and click **Build Index**. (GitHub is the lowest-friction source — it downloads the repo as a single
zipball; the default is `dotnet-architecture/eShopOnWeb`.)

**5. Run the server**

The shell commands below assume bash/WSL; in PowerShell use `$env:GROQ_API_KEY='...'` (and `Copy-Item`
in place of `cp` above).
```bash
# Secrets are server-side only. Supply each via an env var, or store it encrypted once:
export GROQ_API_KEY=gsk_...        # chat key (optional — search works without it)
export HF_TOKEN=hf_...             # Hugging Face token (only if using HF endpoints)
#   …or write them to the encrypted secrets.enc (env var still wins at read time):
dotnet run --project AzureDevOpsForager.Server -- --set-secret GROQ_API_KEY gsk_...
dotnet run --project AzureDevOpsForager.Server -- --set-secret HF_TOKEN hf_...

dotnet run --project AzureDevOpsForager.Server
```
Open `http://localhost:8000` for the web UI, or launch the desktop viewer
(`AzureDevOpsForager.WinForms`).

---

## Embedding & reranking

Azure DevOps Forager uses two models — `e5-large-v2` for embeddings and `bge-reranker-v2-m3` for
cross-encoder reranking — and each can run **either locally via ONNX or remotely via a Hugging Face
Inference Endpoint**, behind the `IEmbedder` and `IReranker` interfaces. The output is identical: 1024-dim,
L2-normalized, `query:` / `passage:`-prefixed vectors, and the same ranking pipeline, no matter which
backend is wired in.

**Which backend runs** is decided at startup, and the preference order differs between the two hosts —
the Server prefers **Hugging Face → local ONNX → full-text**, while the Indexer prefers **local ONNX →
Hugging Face → hosted `/embed` → disabled**:

1. **Hugging Face** — used when a HF embed URL *and* a token are both present (`Config.HuggingFaceEnabled`).
   The Server and Indexer then load **no local ONNX** — no ~1.3 GB model download — and a GPU-backed
   endpoint reindexes an estimated ~10–15× faster than local CPU ONNX (minutes → seconds, hardware-dependent).
   Scale-to-zero endpoints are handled with a warm-up retry, and reranking
   POSTs to `<HuggingFaceRerankUrl>/rerank`.
2. **Local ONNX** — the fallback when the model files exist on disk. Fully offline, no account, no API cost.
3. **Hosted `/embed` service** — an Indexer-only fallback: when no local model and no HF endpoint is
   configured, the Indexer embeds remotely against a hosted Server `/embed` service (`EmbeddingServiceUrl`,
   default the demo server). `HostedEmbeddingFileCap` bounds how many files this path will embed.
4. **Full-text only** — if none is available, the pipeline degrades gracefully to lexical search.

The reranker is optional either way — set `RerankerEnabled` to `false` to run vector + RRF only — and it is
fail-soft: if a HF rerank call fails, results fall back to their RRF order.

**Local models** (not committed — they're large):

| Model | Purpose | Files (under `models/`) |
|-------|---------|-------------------------|
| `e5-large-v2` | 1024-dim sentence embeddings | `e5-large-v2/e5-large-v2.onnx`, `vocab.txt` |
| `bge-reranker-v2-m3` | cross-encoder reranking | `bge-reranker-v2-m3-onnx/model.onnx`, `sentencepiece.bpe.model` |

`./models/download-models.ps1` exports both models from the official Hugging Face weights via
Python/optimum (requires Python 3.9+; downloads ~2.5 GB of weights on first run). Both are permissively
licensed (MIT) and redistributable.

**Hugging Face** needs only the two endpoint URLs in `config.json` (`HuggingFaceEmbedUrl`,
`HuggingFaceRerankUrl` — not secret) plus an `HF_TOKEN` supplied as an environment variable or stored
encrypted via `Server --set-secret HF_TOKEN hf_xxx`.

---

## Configuration

All settings live in `config.json` next to the server/indexer (see `config.sample.json`). Everything has a
working default; you override only what you need.

| Key | What it does |
|-----|--------------|
| `ServerUrl` | URL the clients call for `/query` and `/chat` (default `https://azuredevops.aidataforager.com`, the hosted demo) |
| `Port` | Server listen port |
| `SqlConnectionString` | Target SQL Server / Azure SQL database |
| `AzdoVectorConnectionString` | SQL DB the Indexer writes the vector index into — usually the same as `SqlConnectionString` |
| `OnnxModelPath` | Path to local `e5-large-v2.onnx` (used when Hugging Face is not configured) |
| `HuggingFaceEmbedUrl` / `HuggingFaceRerankUrl` | Hugging Face Inference Endpoint URLs for embedding + reranking (not secret; leave blank to use local ONNX) |
| `EmbeddingServiceUrl` | Hosted Server `/embed` service the Indexer uses to embed remotely when no local model and no HF endpoint is configured (default: the demo server) |
| `RerankerModelPath` / `RerankerEnabled` / `RerankerInputSize` | Local cross-encoder model path, on/off, candidate-pool size |
| `RrfVectorWeight` / `RrfChunkFtsWeight` / `RrfFileFtsWeight` | RRF fusion weights |
| `MinFtsRank` / `MaxVectorDistance` | Candidate-admission thresholds |
| `SourceType` | `tfvc`, `git`, or `github` |
| `GitHubRepoUrl` / `GitBranch` / `GitHubToken` | GitHub source settings |
| `AzureUrl` / `AzureProject` / `AzureTfvcRoot` / `GitRepository` / `AzurePAT` | Azure DevOps source settings |
| `IncludeGlobs` / `ExcludeGlobs` | Which files to index (e.g. `**/*.cs`) |

**Secrets are never config keys.** The **Groq API key** and the **Hugging Face token** are read from the
`GROQ_API_KEY` / `HF_TOKEN` environment variables on the server, or from a single encrypted `secrets.enc`
(write with `Server --set-secret NAME VALUE`); the env var wins over the file. Neither is ever embedded in
a shipped artifact or sent to a client.

---

## Tech stack

.NET 8 / .NET Standard 2.0 / .NET Framework 4.8 · ASP.NET Core · WinForms · SQL Server 2025
(`VECTOR`, `VECTOR_SEARCH`, DiskANN, Full-Text Search) · embeddings + reranking (`e5-large-v2`,
`bge-reranker-v2-m3`) via ONNX Runtime **or** Hugging Face Inference Endpoints · Roslyn · Groq
(llama-3.3-70b).

## Documentation

- [docs/USER_GUIDE.md](docs/USER_GUIDE.md) — installing, indexing a codebase, and using search + chat.
- [docs/DEVELOPER_GUIDE.md](docs/DEVELOPER_GUIDE.md) — architecture, the retrieval pipeline, and extending it.
- [docs/FUNCTIONAL_MATRIX.md](docs/FUNCTIONAL_MATRIX.md) — every capability, where it lives in the code, and how it's verified.

## License

MIT — see [LICENSE](LICENSE).
