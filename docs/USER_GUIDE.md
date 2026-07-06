# Azure DevOps Forager — User Guide

Welcome! This guide is for **using** Azure DevOps Forager — searching a codebase and asking questions about it. You do not need to read or change any code to follow along. Everything here is written for the person sitting in front of the app, and it is deliberately thorough, with real worked examples of what to type and what you'll get back.

If you just want to *try it right now*, jump to [First Run — Two Ways](#first-run--two-ways) and pick "Option A — the zero-config demo." You can be searching a real codebase in under a minute.

---

## Table of Contents

1. [What It Is](#what-it-is)
2. [First Run — Two Ways](#first-run--two-ways)
3. [Running a Semantic Search (Web UI)](#running-a-semantic-search-web-ui)
4. [Running a Semantic Search (Desktop Chat Client)](#running-a-semantic-search-desktop-chat-client)
5. [Worked Examples](#worked-examples)
6. [How to Read the Results](#how-to-read-the-results)
7. [The Indexer — Building Your Own Index](#the-indexer--building-your-own-index)
8. [The 1,000-File Fair-Use Cap](#the-1000-file-fair-use-cap)
9. [Troubleshooting](#troubleshooting)
10. [Where Things Live (Files & Logs) — Quick Reference](#where-things-live-files--logs--quick-reference)

---

## What It Is

Azure DevOps Forager is a **hybrid code search engine** with a **grounded AI chat** on top. "Hybrid" means it combines two very different ways of finding code and blends the results, so you get the best of both:

- **Semantic (vector) search** — understands the *meaning* of your query. You can describe a concept in plain English ("retry with exponential backoff") and it finds the code that does that thing, even if none of your words appear literally in the file.
- **Lexical (full-text) search** — matches the *actual words and names* in the code. When you know a class name, a method name, or an exact token, this makes sure exact matches surface reliably.

You don't manage any of the machinery — the embeddings, the vector index, the keyword ranking, and the optional AI answer all happen behind the server. The short version of *why the results are good*: the semantic and keyword rankings are fused, so a strong exact-name match and a strong "does-what-you-described" match can both rise to the top; and in **Ask** mode a grounded LLM explains the top hits using *only* the retrieved code, citing its sources.

> Curious about the internals — SQL Server 2025 native vectors, the RRF fusion, the `bge-reranker-v2-m3` cross-encoder reranker, the `e5-large-v2` embedding model? That's all covered in [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md). You don't need any of it to use the app. (Both the embedding and reranking models can run either locally or on a remote Hugging Face endpoint — that choice is explained in [Where Embedding Happens — Your Three Options](#4-optional-where-embedding-happens--your-three-options) below.)

**Privacy model, in one line:** your code stays on your infrastructure. Only your question and the snippets the server retrieved are ever sent to the LLM — and any secret keys (the LLM API key, and a Hugging Face token if you use hosted models) live **only on the server**, never in the desktop or web client.

---

## First Run — Two Ways

There are two ways to get going. Most people trying the app for the first time want **Option A**.

### Option A — The Zero-Config Demo (nothing to set up)

The shipped clients are pre-pointed at a **hosted demo server** with a **sample index** already built over a public open-source repository. You don't need a database, you don't need to download any models, and you don't need an API key.

- **Web UI:** open the demo server's URL in a browser. The clients default to `https://azuredevops.aidataforager.com`, which serves the browser UI at `/`.
- **Desktop chat client:** just launch `AzureDevOpsForager.WinForms`. It's a thin client and already knows the demo server address.

That's it — start typing a query (in Search mode) or a question (in Ask mode). Skip ahead to [Running a Semantic Search](#running-a-semantic-search-web-ui) and try the [Worked Examples](#worked-examples).

### Option B — Self-Host (your database, your code)

Self-hosting means you run the server yourself, pointed at **your own SQL Server 2025 (or Azure SQL)** database, containing **your own indexed code**. The high-level path is:

1. **Build an index** of your codebase using the **Indexer** (pick a source, pick a destination database, click Build). See [The Indexer](#the-indexer--building-your-own-index) — this is the main event and is covered in detail below.
2. **Run the server**, pointed at that database. It serves the web UI at its port (default **8000**) and exposes the search/chat API.
3. **Open the web UI** at `http://localhost:8000`, or launch the desktop client (pointed at your own server).

The one prerequisite that matters most: the destination must be a **SQL Server 2025** instance (or Azure SQL) with vector support, because the native `VECTOR` type is what makes semantic search work. Full-Text Search must also be available for the lexical half. (The AI chat additionally needs a Groq API key set on the server — but plain search works fine without it.)

For the embedding and reranking models, self-hosters have a choice — you can run them **locally** (download the ONNX model bundles, fully offline, no account) or point the server and Indexer at **Hugging Face hosted endpoints** (no ~1.3 GB local download; a GPU-backed endpoint reindexes an estimated ~10–15× faster than local CPU ONNX — minutes become seconds, though exact speed depends on your hardware and endpoint). Both are laid out in [Where Embedding Happens — Your Three Options](#4-optional-where-embedding-happens--your-three-options) below.

The nice part: when you finish a build, the Indexer offers to **wire your local server to that database for you**, so you don't have to hand-edit any config to start searching what you just indexed.

---

## Running a Semantic Search (Web UI)

Open the server's URL in a browser (the demo, or your own `http://localhost:8000`). You'll see the **Azure DevOps Forager** header with a small backend-health indicator in the top-right (a colored dot plus a status message — it confirms the search backend is reachable).

There are two modes, switched with the tabs near the top:

- **⌕ Search** — ranked code results with snippets.
- **❯ Ask** — natural-language chat with a grounded answer plus cited sources.

### To run a Search

1. Make sure the **Search** tab is selected (it is by default).
2. Click the big search box and type your query. It can be a natural-language concept *or* exact names — the hybrid engine handles both.
3. (Optional) Use the controls under the box to refine:
   - **Module** — a filter dropdown ("All modules" by default). It's populated from the file-type facets of whatever is indexed.
   - **Filename hint** — an optional box (e.g. `UserService.cs`) to nudge results toward a particular file.
   - **Results** — how many results to return (3, 5, 10, 15, or 25; default 5).
4. Press **Enter** or click **Search**.

Results appear as a ranked list of files with code snippets. See [How to Read the Results](#how-to-read-the-results).

### To Ask a question

1. Click the **❯ Ask** tab.
2. Type a question in natural language in the chat box at the bottom (e.g. *"How does the hybrid search RRF fusion work?"*).
3. Press **Enter** or click **Ask**.

The answer is written into the chat log, grounded in the indexed source, and it cites the files it drew from. (If the server has no chat key configured, Ask mode will tell you chat isn't configured — plain Search still works.)

> **Light/dark theme:** there's a ☀ toggle in the top-right if you prefer a light theme.

---

## Running a Semantic Search (Desktop Chat Client)

The desktop client (`AzureDevOpsForager.WinForms`) is a focused **chat** window — it's a thin client of the server's chat endpoint. Launch it and you'll get a greeting confirming the privacy model:

> *Azure DevOps Forager chat ready. Your code stays on your infrastructure — only your question and the snippets the server retrieves are sent to the LLM. Ask a question about the indexed codebase.*

To use it:

1. Type your question in the input box and send it. The status bar shows **"Asking the Forager server..."** while it works.
2. The answer appears in the transcript.
3. **Rate the answer** with the feedback (thumbs) buttons:
   - **Thumbs up** records that the answer was good (kept as a "known answer" so it can be replayed later).
   - **Thumbs down** doesn't just log a complaint — it **automatically re-asks your last question with more detail** and shows the improved answer. You then get to rate *that* one too. So a downvote gives you a better answer instead of a dead end.
4. **Clearing the chat** wipes the on-screen transcript and starts a fresh local session. (The server itself is stateless — each question is answered on its own — so there is no remote history to clear.) You'll see a system line confirming the reset.

The status bar reads **"Ready - Ask a question"** when it's idle and waiting for you.

---

## Worked Examples

Here are three concrete queries to try, with what to expect. These assume the demo index (built over a public .NET repository) or any similar C# codebase.

### Example 1 — A natural-language concept query (semantic)

**Type into Search:**

```
retry with exponential backoff
```

**What to expect:** you probably don't have a file literally named "exponential backoff." That's exactly where semantic search shines — the engine embeds your phrase and finds code whose *meaning* matches: retry loops, `Task.Delay` calls that grow each attempt, Polly retry policies, `WaitAndRetry`, resilience/HTTP handler configuration, and similar. The best matches float to the top even though your exact words may not appear in them. This is the query to reach for when you know *what the code should do* but not what it's called.

### Example 2 — A filename-hint search (lexical assist)

**Type into Search:**

```
validate user credentials
```

**...and in the Filename hint box, type:**

```
UserService.cs
```

**What to expect:** the concept query ("validate user credentials") drives the semantic + full-text ranking, while the filename hint biases results toward files like `UserService.cs`. Use this pattern whenever you're fairly sure *which file* the thing lives in but want the most relevant chunk *within* that area surfaced first. If you know an exact class or method name, just typing it directly (e.g. `SignInManager`, `PasswordHasher`) also works — the lexical half of the hybrid engine matches exact tokens reliably, so exact-name searches don't get lost.

### Example 3 — A chat question (grounded Ask mode)

**Switch to the ❯ Ask tab (web) or use the desktop client, and type:**

```
How does the hybrid RRF fusion work?
```

**What to expect:** the server runs a hybrid search for that question, gathers the most relevant code chunks, and asks the LLM to explain **using only those snippets**. You'll get a plain-English explanation grounded in the actual implementation, followed by the **source files** it was based on. Because the answer is grounded, you can click into (or open) those cited files to verify the explanation against the real code — the assistant isn't free-associating from general knowledge, it's summarizing the code that was retrieved.

Other good Ask questions to try: *"Where is the database connection string configured?"*, *"How are code files chunked before indexing?"*, *"What model is used for embeddings?"*

---

## How to Read the Results

**In Search mode**, results are a **ranked list** — the most relevant match is at the top. Each result shows:

- **The file** it came from (the identifier/path of the chunk's source file).
- **A code snippet** — the specific chunk that matched, so you can judge relevance without opening the file.

Because ranking is hybrid, don't be surprised when a semantically-relevant file with none of your exact words ranks above a file that merely mentions one of your words in a comment — that's the reranker and RRF fusion doing their job. If you want *more* candidates to scan, bump the **Results** dropdown up to 10, 15, or 25.

**In Ask mode**, you get:

- **The answer** — prose, grounded in retrieved code.
- **Sources** — the list of files the answer was built from. Treat these as your "show your work" trail; open them to confirm.

A couple of practical tips:

- If results feel too broad, add a **Filename hint** or an exact class/method name to anchor them.
- If a concept search returns nothing useful, try rephrasing toward *what the code does* rather than *what you'd name it*.
- The engine **degrades gracefully**: if the semantic (embedding) stage is unavailable for any reason, it falls back to full-text-only search rather than failing — so you'll still get lexical matches.

---

## The Indexer — Building Your Own Index

This section is for self-hosters (Option B). The **Indexer** (`AzureDevOpsForager.Indexer`) is a single-window app: point it at a code **Source**, point it at a **Destination** database, and click **Build Index**. It opens ready to use, defaulting to the **GitHub** source and a **SQL Server** destination. Every field has a placeholder showing the expected format, and hovering a field shows a fuller tooltip.

The flow, top to bottom:

### 1. Pick a Source

Use the **Type** dropdown to choose where your code lives:

- **Azure DevOps (TFVC)** — fill in:
  - **Organization URL** — e.g. `https://dev.azure.com/your-org`
  - **Project** — e.g. `MyProject`
  - **Root path ($/…)** — the TFVC server path to index, e.g. `$/MyProject/Main/Src`
  - **Subfolder (optional)** — narrow the scope under the root; blank = everything under the root
  - **Personal Access Token** — an Azure DevOps PAT with **Code (Read)** scope

- **Azure DevOps (Git)** — fill in:
  - **Organization URL**, **Project**, **Repository** (e.g. `my-service`)
  - **Branch** — blank = the repo's default branch
  - **Personal Access Token** — PAT with **Code (Read)** scope

- **GitHub** — the lowest-friction source, and the default:
  - **Repository URL** — e.g. `https://github.com/owner/repo` (pre-filled with the public demo repo `dotnet-architecture/eShopOnWeb`, which you can change)
  - **Branch** — blank = default branch
  - **Token** — blank for public repos; provide one for private repos or to raise rate limits

### 2. Pick a Destination

Use the **Destination → Type** dropdown:

- **SQL Server** (on-prem / local):
  - **Server** — e.g. `localhost\SQLEXPRESS` or `MACHINE\INSTANCE`
  - **Database** — target DB name (defaults to `AzureDevOpsForager`); **created automatically if it doesn't exist**
  - **Windows Authentication** — checked by default (uses your Windows login). Uncheck it to reveal **User** / **Password** fields for a SQL login.

- **Azure SQL:**
  - **Server** — your `yourserver.database.windows.net` value from the portal
  - **Database** — defaults to `AzureDevOpsForager`, created automatically if missing
  - **User** / **Password** — Azure SQL always uses SQL authentication, so credentials are always required here

> **Important:** the destination instance must be **SQL Server 2025** (or Azure SQL) so the native vector type is available. That's what powers semantic search.

### 3. Options — which files to index

- **Include globs** — semicolon-separated patterns of files to index. Default: `**/*.cs` (all C# files).
- **Exclude globs** — semicolon-separated patterns to skip. Default: `**/bin/**;**/obj/**` (build output).

### 4. (Optional) Where Embedding Happens — Your Three Options

This is the one decision behind the scenes that shapes your whole setup: **where do the embedding (and reranking) models run?** There are **three** ways to answer that, and you can pick whichever fits — no code changes required.

1. **Use the hosted demo service** — the default, nothing to install.
2. **Point at Hugging Face (HF) hosted endpoints** — no local model download, GPU-fast (an estimated ~10–15× over local CPU ONNX), but needs an HF account/token.
3. **Run the models locally** — fully offline, no account, uncapped.

Here's each in plain terms.

#### Option 1 — Hosted demo service (default; leave everything blank)

Leave the **Model Override Path** blank and don't configure any HF endpoints, and this machine uses the **hosted demo embedding service**. Nothing to install and no account needed, but it's subject to the shared fair-use cap (see [The 1,000-File Fair-Use Cap](#the-1000-file-fair-use-cap)).

#### Option 2 — Hugging Face hosted endpoints (no local download)

Instead of downloading ~1.3 GB of models, you can point the app at **Hugging Face Inference Endpoints** that run the same two models for you remotely:

- **Embedding** — `e5-large-v2`, served by an HF endpoint (produces the identical 1024-dimension, L2-normalized, `query: `/`passage: `-prefixed vectors you'd get locally).
- **Reranking** — `bge-reranker-v2-m3`, served by an HF endpoint.

You configure this once, in the server/Indexer **`config.json`** (these URLs are **not** secret, so they live in plain config):

- `HuggingFaceEmbedUrl` — the URL of your embedding endpoint.
- `HuggingFaceRerankUrl` — the URL of your reranking endpoint (the app POSTs to `<url>/rerank`).

The **HF token is a secret**, so it does *not* go in `config.json`. Supply it either way:

- Set the **`HF_TOKEN`** environment variable on the machine running the server/Indexer, **or**
- Store it encrypted by running: `Server --set-secret HF_TOKEN hf_xxxxxxxx` (this writes it, encrypted, into `secrets.enc` — see [A note on secrets](#a-note-on-secrets) below).

HF is used **only when both** an embedding URL *and* a token are present. When it's active, the Server and Indexer load **no local ONNX models at all** — nothing to download, and a GPU-backed endpoint reindexes an estimated ~10–15× faster than local CPU ONNX (minutes → seconds; the exact figure is hardware-dependent). If your endpoint is a **scale-to-zero** one that goes cold when idle, that's handled: the app warms it up with an automatic retry, so the first request after an idle period just takes a little longer instead of failing.

> Reranking is **fail-soft**: if the rerank endpoint hiccups, results still come back (just ranked by the base hybrid scoring) rather than the search failing.

#### Option 3 — Run the models locally (offline, no account, uncapped)

Point the **Model Override Path** at a local model and this machine embeds **locally** — no file-count cap, no account, and nothing leaves your network for a hosted service. This is the offline / no-account option, and it works fully.

The easiest way to get the local model is the **Download** link next to the field. Click it and the Indexer will, with **no Python and no manual steps**:

1. Ask you to pick an install folder.
2. Download the embedding-model bundle (a ~1.3 GB file; it logs live percentage progress).
3. Unpack it into your folder.
4. Find the `.onnx`, set the **Model Override Path** for you, and **persist it** so the local server and clients pick it up automatically on their next run.

After a successful download you'll see: *"Model installed and path set. This machine will now embed locally (no file-count cap)."*

#### How the app decides which one to use

The selection is automatic, in this order:

1. **If a HF embed URL *and* an HF token are both present** → use the **HF hosted endpoints** (no local ONNX loaded).
2. **Otherwise, if a local model is available** (Model Override Path set, or the model files are present) → **embed locally**.
3. **Otherwise** → fall back to **full-text-only** search (still useful — just lexical, no semantic ranking).

So a recipient of the app can run it three ways: **zero local ONNX** (point at HF), **fully local** (download the ONNX models), or just **use the hosted demo**.

*(For how that selection is made in code, see [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) → Embedding.)*

#### A note on secrets

The server keeps all of its named secrets in a **single encrypted file, `secrets.enc`**, next to the server — currently the LLM chat key (`GROQ_API_KEY`) and the Hugging Face token (`HF_TOKEN`). You write a secret with:

```
Server --set-secret NAME VALUE
```

For example, `Server --set-secret HF_TOKEN hf_xxxxxxxx` or `Server --set-secret GROQ_API_KEY gsk_xxxxxxxx`.

Two things worth knowing:

- **The environment variable wins.** If both an environment variable (e.g. `HF_TOKEN`) and a stored value in `secrets.enc` exist for the same name, the environment variable is used. That makes it easy to override on a given machine without rewriting the file.
- This unified `secrets.enc` **replaces the old single-purpose `groqapikey.enc`** file — one encrypted store now holds every named secret.

### 5. Connect

Click **Connect & Init**. This is the safe, verify-first step. It:

- Tests the connection to your destination.
- If the **server is reachable but the database is missing**, it offers to **create the database** for you, then waits briefly until the new database accepts connections.
- Ensures the schema exists (tables + full-text setup).

You'll see `[OK] Schema ready.` in the log when it succeeds. Use Connect to validate your settings before committing to a full build.

### 6. Build Index

Click **Build Index**. What happens:

- It validates, ensures schema, and — **if the target already holds data** — requires a **deliberate double confirmation** before wiping it (two "Are you sure?" prompts, both defaulting to *No*). This prevents nuking a live index by reflex.
- The log shows a **start timestamp** and streams live progress as files are processed. (The Build button turns into a **Cancel** button during the run; cancelling stops cleanly after the current file and leaves the existing index intact.)
- On completion you'll see `[DONE] Index build complete.` and the **total run time** in the log.
- The full log of the run is written to **`lastlog.txt` next to the exe** (overwritten each run), so you can review run times and messages after the window closes.

Finally, the Indexer offers to **make the just-built database your local server's data source** ("Set this database as your local Server's data source?"). Say **Yes** and everything you just indexed becomes searchable in the UIs when you run the server locally — no manual config edits needed.

> **Zero-downtime rebuilds:** a full rebuild stages into separate tables and only swaps them into the live index after a completion check, so the live index is never left half-built during a re-index. (How the swap works: [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) → Data Model → staging→live swap.)

---

## The 1,000-File Fair-Use Cap

If you use the **hosted (shared) demo embedding service** — that is, you left the **Model Override Path blank** *and* haven't configured Hugging Face endpoints — a single build is limited to **1,000 files per run**. This protects the shared service from very large jobs.

When a source has more than 1,000 files, the Indexer stops and asks:

> *This source has N files, but the shared demo embedding service is limited to 1,000 files per run.*
> - **Yes** — index the first 1,000 files now.
> - **No** — cancel. To index everything, click **Download** (Options, above) to install the model locally, then rebuild.

So you have three clean ways to index everything:

1. **Take the top 1,000** for a quick sample, or
2. **Download the model** (one click, no Python — see [Option 3](#option-3--run-the-models-locally-offline-no-account-uncapped)) to embed **locally with no cap**, or
3. **Point at a Hugging Face endpoint** (see [Option 2](#option-2--hugging-face-hosted-endpoints-no-local-download)) — your own endpoint, no shared cap.

This cap **only applies to the shared demo service**. It's **ignored entirely when a local model is configured *or* when Hugging Face endpoints are configured** — in both of those cases you're embedding against your own model/endpoint, uncapped.

---

## Troubleshooting

### "Could not connect" / connection failures

- **Check the server value first.** For a named SQL Server instance, use the `.\INSTANCE` form (e.g. `.\SQLEXPRESS` for a local instance, or `MACHINE\INSTANCE`). A bare machine name without the instance is the most common mistake.
- **The instance must be SQL Server 2025** (or Azure SQL) — vector support is what the app relies on. Pointing at an older SQL Server will fail to provide the vector functionality.
- **Database missing?** That's expected and handled — on Connect/Build the Indexer offers to **create it** for you. If you just created it and get "created but not yet connectable," wait a moment and click **Connect** again (a brand-new DB can take a few seconds to accept connections).
- **Credentials:** if **Windows Authentication** is unchecked, a **User** is required. Double-check user/password for SQL and Azure SQL logins.
- **Azure SQL:** use the full `yourserver.database.windows.net` server name and remember Azure always uses SQL auth (no Windows auth option).

### The model download

- The bundle is **large (~1.3 GB)** — the log shows live percentage as it downloads and notes that unpacking "can take a moment." Give it time.
- If you see *"The downloaded archive didn't contain an .onnx file,"* the install folder didn't end up with a usable model — pick a fresh, writable folder and try again.
- The Download button ignores extra clicks while a download is already in progress, so one click is enough — just wait.
- Once installed, the path is set and persisted for you; you don't need to edit anything by hand.
- **Don't want the local download at all?** You don't have to do this — configuring **Hugging Face endpoints** instead means the Server and Indexer load no local models (nothing to download). See [Option 2](#option-2--hugging-face-hosted-endpoints-no-local-download).

### Hugging Face endpoints (hosted embedding/reranking)

- HF is only used when **both** `HuggingFaceEmbedUrl` (in `config.json`) **and** an HF token are present. If either is missing, the app quietly falls back to local ONNX (if the model files exist), or to full-text-only. So if you expected HF but semantic ranking looks like local behavior, check that *both* the URL and the token are set.
- The token comes from the **`HF_TOKEN`** environment variable **or** the encrypted `secrets.enc` (written via `Server --set-secret HF_TOKEN hf_xxx`). The environment variable wins if both are set.
- **First request slow after idle?** That's expected for a **scale-to-zero** endpoint — the app warms it up with an automatic retry, so give the first call a little longer rather than assuming it failed.
- **Reranking is fail-soft:** if the rerank endpoint errors, search still returns results (ranked by the base hybrid scoring) instead of failing.

### Chat says it isn't configured

- In Ask/chat mode, if you see *"Chat is not configured,"* the **server** doesn't have an AI key set. Plain **Search** still works fully without it. (Setting the key is a server-side task for whoever runs the server; it never touches the clients.)
- The chat key (`GROQ_API_KEY`) is supplied the same way as any other secret: the **environment variable** of that name, or the encrypted `secrets.enc` via `Server --set-secret GROQ_API_KEY gsk_xxx`. The environment variable wins if both are set.

### Where logs live

- **Indexer:** the full log of the most recent build is written to **`lastlog.txt`** in the same folder as the Indexer executable (overwritten each run). The on-screen log clears at the start of each build, so `lastlog.txt` is your after-the-fact record — including total run time.
- **Server:** the server prints a **startup banner** to its console showing the mode, port, and the resolved SQL Server/database it's pointed at, plus the **count of indexed files** once it's up (`[SQL FTS] Files indexed: N`). If that count is 0, the database you're pointed at has no index yet — (re)build it, or point the server at the right database.
- **Chat feedback:** thumbs up/down from chat is appended to a `chat_feedback.log` next to the server.

### Search returns nothing / weak results

- Confirm the server's startup banner shows a **non-zero indexed file count**. Zero means empty index.
- Try rephrasing a concept query toward *what the code does*, or add a **Filename hint** / exact name to anchor lexical matches.
- If the embedding stage is down, you'll still get full-text-only results (graceful degradation) — narrower, but not empty.

---

## Where Things Live (Files & Logs) — Quick Reference

| Thing | Location |
|-------|----------|
| Web UI | The server's URL at `/` (demo: `https://azuredevops.aidataforager.com`; self-host: `http://localhost:8000`) |
| Desktop chat client | `AzureDevOpsForager.WinForms` |
| Indexer | `AzureDevOpsForager.Indexer` |
| Indexer run log (with total run time) | `lastlog.txt` next to the Indexer exe |
| Server startup info / indexed-file count | The server's console banner |
| Chat feedback log | `chat_feedback.log` next to the server |
| Non-secret config (incl. `HuggingFaceEmbedUrl` / `HuggingFaceRerankUrl`) | `config.json` (server/Indexer) |
| Encrypted secrets (`GROQ_API_KEY`, `HF_TOKEN`) | `secrets.enc` next to the server (write via `Server --set-secret NAME VALUE`; env var wins) |
| Local embedding model | Wherever you installed it via **Download**; path stored as **Model Override Path** |
| Default destination database name | `AzureDevOpsForager` (created automatically if missing) |
| Default server port | `8000` |
| Hosted embedding fair-use cap | 1,000 files per run (only the shared demo service; not when local model or HF endpoints are configured) |

---

*Happy foraging! 🌾 Enter a query and surface the most relevant code across the vector and full-text indexes — or switch to Ask and let the grounded chat explain it, sources and all.*
