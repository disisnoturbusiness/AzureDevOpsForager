/* ===================================================================
   Azure DevOps Forager — web UI (vanilla JS, same-origin)

   Endpoints (all relative / same-origin):
     POST /query
        req:  { question, moduleFilter, nResults }   (camelCase; server binds case-insensitively)
        res:  { ids:[[path,...]], documents:[[content,...]],
                metadatas:[[ {chunk_name, chunk_type, score, match_source,
                              start_line, end_line, namespace, distance, _file_path,
                              class_name, signature, ...}, ... ]],
                error: string|null }
     GET  /health     -> { status, ftsFileCount, vectorPointCount, vectorStatus, error }
     GET  /systems    -> [ { name, count } ]            (optional; 404 -> hidden)
     POST /chat       -> { answer, sources:[...] }       (optional; 404 -> graceful message)
     POST /chat/feedback { question, answer, helpful:bool }  (optional; 404 -> silent)
   =================================================================== */

(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);

  // ---- elements ----
  const tabSearch = $("tabSearch"), tabChat = $("tabChat");
  const panelSearch = $("panelSearch"), panelChat = $("panelChat");

  const searchForm = $("searchForm"), searchInput = $("searchInput");
  const moduleFilter = $("moduleFilter"), nResultsSel = $("nResults");
  const searchBtn = $("searchBtn"), searchStatus = $("searchStatus");
  const resultsEl = $("results"), searchEmpty = $("searchEmpty");

  const chatForm = $("chatForm"), chatInput = $("chatInput"), chatBtn = $("chatBtn");
  const chatLog = $("chatLog"), chatEmpty = $("chatEmpty");

  const healthEl = $("health"), healthDot = $("healthDot"), healthText = $("healthText");

  // ---- theme (day / night), persisted in localStorage; default dark ----
  const themeToggle = $("themeToggle");
  const THEME_KEY = "adf-theme";
  function applyTheme(t) {
    const light = (t === "light");
    if (light) document.documentElement.setAttribute("data-theme", "light");
    else document.documentElement.removeAttribute("data-theme");
    if (themeToggle) {
      themeToggle.textContent = light ? "☾" : "☀";
      themeToggle.setAttribute("aria-label", light ? "Switch to dark theme" : "Switch to light theme");
    }
  }
  applyTheme(localStorage.getItem(THEME_KEY) || "dark");
  if (themeToggle) themeToggle.addEventListener("click", () => {
    const next = (localStorage.getItem(THEME_KEY) === "light") ? "dark" : "light";
    localStorage.setItem(THEME_KEY, next);
    applyTheme(next);
  });

  // ===================================================================
  // helpers
  // ===================================================================
  function esc(s) {
    return String(s == null ? "" : s)
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  }

  function num(v, digits) {
    const n = parseFloat(v);
    if (!isFinite(n)) return null;
    return digits == null ? n : n.toFixed(digits);
  }

  // Human-readable elapsed time: ms under a second, "3.2 s" for single-digit seconds, whole
  // seconds up to a minute, then "1m 56s" past a minute — so a cold start reads as its real
  // magnitude ("1m 56s", clearly under 2 min) instead of a wall of digits or an ambiguous "116 s".
  function formatDuration(ms) {
    if (ms < 1000) return Math.round(ms) + " ms";
    const totalSec = ms / 1000;
    if (totalSec < 10) return totalSec.toFixed(1) + " s";        // e.g. 3.2 s
    const whole = Math.round(totalSec);
    if (whole < 60) return whole + " s";                         // e.g. 45 s
    return Math.floor(whole / 60) + "m " + (whole % 60) + "s";   // e.g. 1m 56s
  }

  // Split "Some\\Path\\File.cs" -> show last segment emphasized
  function renderPath(path, ns) {
    if (!path) return "";
    const parts = String(path).split(/[\\/]/);
    const file = parts.pop();
    const dir = parts.join("/");
    const nsHtml = ns ? ` <span class="ns">[${esc(ns)}]</span>` : "";
    return (dir ? esc(dir) + "/" : "") + "<b>" + esc(file) + "</b>" + nsHtml;
  }

  // ===================================================================
  // question detection — decides answer-vs-results without the user choosing
  // ===================================================================

  // Words that open a question. Deliberately a fixed list rather than a model call: this runs on every
  // keystroke-free submit and must be instant, and the cost of a wrong guess is asymmetric — see below.
  const QUESTION_OPENERS = new Set([
    "how", "what", "why", "when", "where", "who", "which", "whose", "whom",
    "can", "could", "do", "does", "did", "is", "are", "was", "were",
    "should", "would", "will", "shall", "has", "have", "am"
  ]);

  /**
   * True when a query reads as a question and should be answered rather than listed.
   *
   * The guard against identifiers matters more than the opener list. "PaymentMethod", "IEmailSender"
   * and "HttpService.GetAsync()" must never be routed to the answer path: for an identifier you want
   * the code, and running one through the LLM costs a Groq call to bury the thing you asked for.
   *
   * The failure modes are not symmetric, so this biases toward search. A question mistakenly run as a
   * search still shows real matching code — mildly worse. An identifier mistakenly run as an answer
   * hides the code behind prose and spends money doing it.
   */
  function isQuestion(raw) {
    const q = (raw || "").trim();
    if (!q) return false;

    // A trailing question mark is unambiguous intent; take it regardless of shape.
    if (q.endsWith("?")) return true;

    const words = q.split(/\s+/);
    if (words.length < 3) return false;            // "how" or "PaymentMethod" alone is not a question

    const first = words[0].toLowerCase().replace(/[^a-z]/g, "");
    if (!QUESTION_OPENERS.has(first)) return false;

    // Anything carrying code punctuation is a lookup even if it starts with a question word.
    if (/[._()\[\]<>{}#]/.test(q)) return false;

    return true;
  }

  // ===================================================================
  // health + facets
  // ===================================================================
  async function loadHealth() {
    try {
      const r = await fetch("/health", { headers: { Accept: "application/json" } });
      if (!r.ok) throw new Error("status " + r.status);
      const h = await r.json();
      const status = h.status ?? h.Status;
      const files = h.ftsFileCount ?? h.FtsFileCount;
      const vectors = h.vectorPointCount ?? h.VectorPointCount;

      // HTTP 200 does not mean the stores are ready. While the serverless SQL database is
      // resuming, /health answers 200 with status "error", zero counts and a login-failure
      // message. Rendering those counts verbatim tells a first-time visitor the corpus is
      // empty ("0 files") — worse than saying nothing — so show a warm-up and re-poll.
      if (status === "error") {
        healthEl.classList.add("bad");
        healthEl.classList.remove("ok");
        healthText.textContent = "warming up";
        setTimeout(loadHealth, 15000);
        return;
      }

      // "degraded" means the query path is up but the vector store is not fully healthy, so the
      // counts are real and worth showing — just don't present it as a clean bill of health.
      const degraded = status === "degraded";
      healthEl.classList.toggle("ok", !degraded);
      healthEl.classList.toggle("bad", degraded);
      const fileTxt = files != null ? Number(files).toLocaleString() + " files" : "online";
      const vecTxt = vectors != null && Number(vectors) > 0 ? " · " + Number(vectors).toLocaleString() + " chunks" : "";
      healthText.textContent = fileTxt + vecTxt + (degraded ? " · vectors degraded" : "");
    } catch (e) {
      healthEl.classList.add("bad");
      healthEl.classList.remove("ok");
      healthText.textContent = "backend unreachable";
      // The backend may just be cold-starting (scale-to-zero app + serverless SQL).
      // Re-poll on a slow cadence so the pill self-heals instead of freezing on the
      // first failed check. Success schedules nothing, so this stops once it's up.
      setTimeout(loadHealth, 30000);
    }
  }

  function populateModules(systems) {
    // keep the existing "All" option, append unique names
    const seen = new Set(["all"]);
    systems.forEach((s) => {
      const name = s.name ?? s.Name;
      if (!name) return;
      const key = String(name).toLowerCase();
      if (seen.has(key)) return;
      seen.add(key);
      const opt = document.createElement("option");
      opt.value = name;
      const count = s.count ?? s.Count;
      opt.textContent = count != null ? `${name} (${count})` : name;
      moduleFilter.appendChild(opt);
    });
  }

  async function loadSystems() {
    // Optional facets endpoint. Degrade silently on 404 (falls back to /health modules).
    try {
      const r = await fetch("/systems", { headers: { Accept: "application/json" } });
      if (!r.ok) return; // 404 or not wired up yet
      const data = await r.json();
      if (Array.isArray(data) && data.length) populateModules(data);
    } catch (_) { /* hidden gracefully */ }
  }

  // ===================================================================
  // SEARCH
  // ===================================================================
  // One input, no mode picker. A question is answered; anything else is listed. Both render into the
  // same area below the form, so the page behaves identically either way and the only thing that
  // changes is whether an answer sits above the matching code.
  searchForm.addEventListener("submit", async (e) => {
    e.preventDefault();
    const q = searchInput.value.trim();
    if (!q) { searchInput.focus(); return; }

    const asking = isQuestion(q);

    searchBtn.disabled = true;
    searchBtn.classList.add("is-loading");
    searchStatus.classList.remove("err");
    searchStatus.textContent = asking ? "answering…" : "searching…";
    searchEmpty.style.display = "none";
    resultsEl.innerHTML = "";
    // Same cold-start hint either way (first request after idle warms the embedding endpoint).
    const warmTimer = setTimeout(() => {
      searchStatus.textContent = "waking up the server - the search endpoints and the database can take up to 2 minutes to spin up on the first query after an idle period...";
    }, 6000);

    const t0 = performance.now();
    try {
      if (asking) await runAsk(q, t0);
      else await runSearch(q, t0);
    } catch (err) {
      renderSearchError(err.message || (asking ? "Answer failed" : "Search failed"));
    } finally {
      clearTimeout(warmTimer);
      searchBtn.disabled = false;
      searchBtn.classList.remove("is-loading");
    }
  });

  /** Ranked chunk list — the lookup path, for identifiers and keyword queries. */
  async function runSearch(q, t0) {
    const body = {
      question: q,
      moduleFilter: moduleFilter.value || "All",
      nResults: parseInt(nResultsSel.value, 10) || 5
    };

    const r = await fetch("/query", {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify(body)
    });
    if (!r.ok) throw new Error("Server returned HTTP " + r.status);
    const data = await r.json();
    if (data.error) { renderSearchError(data.error); return; }

    renderResults(
      (data.ids && data.ids[0]) || [],
      (data.documents && data.documents[0]) || [],
      (data.metadatas && data.metadatas[0]) || [],
      Math.round(performance.now() - t0)
    );
  }

  /** Grounded answer plus the chunks it was grounded in, rendered as the same cards search uses. */
  async function runAsk(q, t0) {
    const r = await fetch("/chat", {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify({ question: q, moduleFilter: moduleFilter.value || "All" })
    });
    if (r.status === 404) throw new Error("The answer endpoint isn't available on this server.");
    if (!r.ok) throw new Error("Server returned HTTP " + r.status);

    const data = await r.json();
    const answer = data.answer ?? data.Answer ?? "(no answer returned)";
    const results = data.results ?? data.Results ?? null;
    const ms = Math.round(performance.now() - t0);

    const ids = (results && results.ids && results.ids[0]) || [];
    const docs = (results && results.documents && results.documents[0]) || [];
    const metas = (results && results.metadatas && results.metadatas[0]) || [];

    searchStatus.innerHTML =
      `answered in <span class="hl">${formatDuration(ms)}</span>` +
      (ids.length ? ` · <span class="hl">${ids.length}</span> source${ids.length === 1 ? "" : "s"}` : "");

    const block = document.createElement("div");
    block.className = "answer-block";
    block.innerHTML = `<div class="answer-body">${renderMarkdown(answer)}</div>`;

    if (ids.length) {
      const wrap = document.createElement("div");
      wrap.className = "sources";
      wrap.innerHTML = `<div class="sources-title">Sources</div>`;
      const cards = document.createElement("div");
      cards.className = "source-results";
      ids.forEach((path, i) => cards.appendChild(buildResult(i, path, docs[i] || "", metas[i] || {}, false)));
      wrap.appendChild(cards);
      block.appendChild(wrap);
    }

    block.appendChild(buildFeedback(q, answer));
    resultsEl.appendChild(block);
  }

  function renderSearchError(msg) {
    resultsEl.innerHTML = "";
    searchStatus.classList.add("err");
    searchStatus.textContent = "✕ " + msg;
    searchEmpty.style.display = "block";
    searchEmpty.querySelector("p").textContent = "Something went wrong. " + msg;
  }

  function renderResults(ids, docs, metas, ms) {
    resultsEl.innerHTML = "";
    if (!ids.length) {
      searchStatus.textContent = `no results in ${formatDuration(ms)}`;
      searchEmpty.style.display = "block";
      searchEmpty.querySelector(".empty-glyph").textContent = "∅";
      searchEmpty.querySelector("p").textContent = "No matches found. Try broadening the query or clearing the filters.";
      return;
    }

    searchStatus.innerHTML =
      `<span class="hl">${ids.length}</span> result${ids.length === 1 ? "" : "s"} in <span class="hl">${formatDuration(ms)}</span>`;

    ids.forEach((path, i) => {
      const meta = metas[i] || {};
      const content = docs[i] || "";
      resultsEl.appendChild(buildResult(i, path, content, meta));
    });
  }

  function matchBadge(src) {
    const v = (src || "").toLowerCase();
    let cls = "badge-muted", label = src || "—";
    if (v === "hybrid") { cls = "badge-hybrid"; label = "Hybrid"; }
    else if (v === "vector") { cls = "badge-vector"; label = "Vector"; }
    else if (v === "fulltext" || v === "full-text" || v === "fts") { cls = "badge-fulltext"; label = "FullText"; }
    return `<span class="badge ${cls}">${esc(label)}</span>`;
  }

  function buildResult(idx, path, content, meta, autoOpen = true) {
    const chunkName = meta.chunk_name || meta.class_name || "(chunk)";
    const chunkType = meta.chunk_type || "";
    const ns = meta.namespace || "";
    const score = num(meta.score, 4);
    const distance = num(meta.distance, 4);
    const start = meta.start_line, end = meta.end_line;
    const hasLines = start && end && (start !== "0" || end !== "0");

    const card = document.createElement("article");
    card.className = "result";
    card.style.animationDelay = (idx * 45) + "ms";

    const metaBits = [matchBadge(meta.match_source)];
    if (score != null) metaBits.push(`<span class="metric">score <b>${esc(score)}</b></span>`);
    if (distance != null) metaBits.push(`<span class="metric">dist <b>${esc(distance)}</b></span>`);
    if (hasLines) metaBits.push(`<span class="lines">L${esc(start)}–${esc(end)}</span>`);

    const typeChip = chunkType ? `<span class="chunk-type">${esc(chunkType)}</span>` : "";

    card.innerHTML = `
      <div class="result-head" role="button" tabindex="0" aria-expanded="false">
        <span class="result-rank">${idx + 1}</span>
        <div class="result-main">
          <div class="result-title">
            <span class="chunk-name">${esc(chunkName)}</span>
            ${typeChip}
          </div>
          <div class="file-path">${renderPath(path, ns)}</div>
          <div class="meta-row">${metaBits.join("")}</div>
        </div>
        <svg class="chevron" width="16" height="16" viewBox="0 0 24 24" fill="none"
             stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
          <polyline points="9 6 15 12 9 18"></polyline>
        </svg>
      </div>
      <div class="snippet-wrap"><div class="snippet-inner">
        <pre class="snippet"><code></code></pre>
      </div></div>
    `;

    // set code as text (no HTML injection)
    card.querySelector(".snippet code").textContent = content || "// (no snippet returned for this chunk)";

    // copy button injected into the meta row
    const copy = document.createElement("button");
    copy.className = "copy-mini";
    copy.type = "button";
    copy.textContent = "copy";
    copy.addEventListener("click", (e) => {
      e.stopPropagation();
      copyText(content || "", copy);
    });
    card.querySelector(".meta-row").appendChild(copy);

    const head = card.querySelector(".result-head");
    const toggle = () => {
      const open = card.classList.toggle("open");
      head.setAttribute("aria-expanded", String(open));
    };
    head.addEventListener("click", toggle);
    head.addEventListener("keydown", (e) => {
      if (e.key === "Enter" || e.key === " ") { e.preventDefault(); toggle(); }
    });

    // Auto-expand the top hit only when it IS the result — i.e. a plain search, where the best match is
    // what you came for. Under an answer the code has already been explained above, so opening a card
    // uninvited just pushes the rest of the sources off screen. There they all start collapsed.
    if (idx === 0 && autoOpen) { card.classList.add("open"); head.setAttribute("aria-expanded", "true"); }

    return card;
  }

  function copyText(text, btn) {
    const done = () => { const o = btn.textContent; btn.textContent = "copied ✓"; setTimeout(() => (btn.textContent = o), 1200); };
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(done).catch(() => fallbackCopy(text, done));
    } else { fallbackCopy(text, done); }
  }
  function fallbackCopy(text, done) {
    const ta = document.createElement("textarea");
    ta.value = text; ta.style.position = "fixed"; ta.style.opacity = "0";
    document.body.appendChild(ta); ta.select();
    try { document.execCommand("copy"); done(); } catch (_) {}
    document.body.removeChild(ta);
  }

  // ===================================================================
  // CHAT
  // ===================================================================
  let chatBusy = false;

  // The separate Ask panel is gone — one input routes by question detection. This handler is kept
  // behind a null guard so the file still works if the panel is ever reinstated, and so removing the
  // markup cannot take the whole script down with a null dereference at load.
  chatForm && chatForm.addEventListener("submit", async (e) => {
    e.preventDefault();
    if (chatBusy) return;
    const q = chatInput.value.trim();
    if (!q) { chatInput.focus(); return; }

    chatEmpty && (chatEmpty.style.display = "none");
    appendQuestion(q);
    chatInput.value = "";
    chatBusy = true;
    chatBtn.disabled = true;
    chatBtn.classList.add("is-loading");

    const thinking = appendThinking();
    // If the request runs long it's almost certainly a scale-to-zero HF endpoint cold-starting;
    // say so instead of sitting on "foraging...". Cleared as soon as the answer (or error) is in.
    const warmTimer = setTimeout(() => {
      thinking.textContent = "Waking up the server - the search endpoints and the database can take up to 2 minutes to spin up on the first request after an idle period...";
    }, 6000);

    try {
      const r = await fetch("/chat", {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        body: JSON.stringify({ question: q })
      });

      if (r.status === 404) {
        thinking.remove();
        appendAnswerError(
          "The chat endpoint isn't available on this server yet. " +
          "Conversational answers are being wired up separately — meanwhile, the Search tab is fully live."
        );
        return;
      }
      if (!r.ok) throw new Error("HTTP " + r.status);

      const data = await r.json();
      thinking.remove();
      const answer = data.answer ?? data.Answer ?? "(no answer returned)";
      const sources = data.sources ?? data.Sources ?? [];
      const results = data.results ?? data.Results ?? null;
      appendAnswer(answer, sources, q, results);
    } catch (err) {
      thinking.remove();
      appendAnswerError("Couldn't reach the chat service. " + (err.message || ""));
    } finally {
      clearTimeout(warmTimer);
      chatBusy = false;
      chatBtn.disabled = false;
      chatBtn.classList.remove("is-loading");
      chatInput.focus();
    }
  });

  function scrollChat() { chatLog.scrollTop = chatLog.scrollHeight; window.scrollTo({ top: document.body.scrollHeight, behavior: "smooth" }); }

  function appendQuestion(q) {
    const el = document.createElement("div");
    el.className = "msg msg-q";
    el.textContent = q;
    chatLog.appendChild(el);
    scrollChat();
  }

  function appendThinking() {
    const el = document.createElement("div");
    el.className = "msg msg-a thinking";
    el.textContent = "foraging the codebase…";
    chatLog.appendChild(el);
    scrollChat();
    return el;
  }

  function appendAnswerError(msg) {
    const el = document.createElement("div");
    el.className = "msg msg-a err";
    el.innerHTML = `<div class="answer-body"><p>${esc(msg)}</p></div>`;
    chatLog.appendChild(el);
    scrollChat();
  }

  function appendAnswer(answer, sources, question, results) {
    const el = document.createElement("div");
    el.className = "msg msg-a";

    el.innerHTML = `<div class="answer-body">${renderMarkdown(answer)}</div>`;

    // Sources render as the SAME cards the search results use — one per chunk, expandable, with the
    // code, the match source and the scores. A flat list of file-path chips loses everything that
    // makes a hit judgeable, and collapses two chunks from one file into a single entry. The chunks
    // are already in the response; there is nothing to link out to, so nothing should.
    const ids = (results && results.ids && results.ids[0]) || [];
    const docs = (results && results.documents && results.documents[0]) || [];
    const metas = (results && results.metadatas && results.metadatas[0]) || [];

    if (ids.length) {
      const wrap = document.createElement("div");
      wrap.className = "sources";
      wrap.innerHTML = `<div class="sources-title">Sources</div>`;
      const cards = document.createElement("div");
      cards.className = "source-results";
      ids.forEach((path, i) => cards.appendChild(buildResult(i, path, docs[i] || "", metas[i] || {}, false)));
      wrap.appendChild(cards);
      el.appendChild(wrap);
    } else {
      // Fallback for a server that still returns only paths (or a chat error response with none).
      const srcList = normalizeSources(sources);
      if (srcList.length) {
        const wrap = document.createElement("div");
        wrap.className = "sources";
        wrap.innerHTML = `<div class="sources-title">Sources</div>` +
          srcList.map((s) => `<span class="source-chip">${esc(s)}</span>`).join("");
        el.appendChild(wrap);
      }
    }

    el.appendChild(buildFeedback(question, answer));
    chatLog.appendChild(el);
    scrollChat();
  }

  function normalizeSources(sources) {
    if (!Array.isArray(sources)) return [];
    return sources.map((s) => {
      if (typeof s === "string") return s;
      if (s && typeof s === "object") return s.path || s.file || s.filePath || s._file_path || s.name || JSON.stringify(s);
      return String(s);
    }).filter(Boolean);
  }

  // ---- feedback ----
  function buildFeedback(question, answer) {
    const wrap = document.createElement("div");
    wrap.className = "feedback";
    wrap.innerHTML = `<span>Was this helpful?</span>`;
    const up = mkFb("up", "👍"), down = mkFb("down", "👎");
    wrap.appendChild(up); wrap.appendChild(down);

    function mkFb(kind, glyph) {
      const b = document.createElement("button");
      b.type = "button"; b.className = "fb-btn " + kind; b.textContent = glyph;
      b.setAttribute("aria-label", kind === "up" ? "Helpful" : "Not helpful");
      b.addEventListener("click", () => sendFeedback(question, answer, kind === "up", wrap, b));
      return b;
    }
    return wrap;
  }

  async function sendFeedback(question, answer, helpful, wrap, btn) {
    wrap.querySelectorAll(".fb-btn").forEach((b) => { b.disabled = true; });
    btn.classList.add("sel");
    try {
      const r = await fetch("/chat/feedback", {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        body: JSON.stringify({ question, answer, helpful })
      });
      // degrade gracefully whether 404 or ok — feedback is best-effort
      const note = document.createElement("span");
      note.className = "fb-thanks";
      note.textContent = r.ok ? "thanks!" : "noted (offline)";
      wrap.appendChild(note);
    } catch (_) {
      const note = document.createElement("span");
      note.className = "fb-thanks";
      note.textContent = "noted (offline)";
      wrap.appendChild(note);
    }
  }

  // ===================================================================
  // minimal, safe markdown (fenced code + inline code + bold + lists + paragraphs)
  // input is escaped FIRST, so no HTML can be injected.
  // ===================================================================
  function renderMarkdown(src) {
    const text = String(src == null ? "" : src);
    const blocks = [];
    // pull out fenced code blocks first
    let work = text.replace(/```([a-zA-Z0-9_+-]*)\n?([\s\S]*?)```/g, (m, lang, code) => {
      const i = blocks.length;
      blocks.push(`<pre class="block"><code>${esc(code.replace(/\n$/, ""))}</code></pre>`);
      return ` BLOCK${i} `;
    });

    work = esc(work);

    // inline code
    work = work.replace(/`([^`\n]+)`/g, (m, c) => `<code class="inline">${c}</code>`);
    // bold
    work = work.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
    work = work.replace(/__([^_]+)__/g, "<strong>$1</strong>");

    // split into paragraphs / lists by blank lines
    const chunks = work.split(/\n{2,}/).map((para) => {
      const lines = para.split("\n");
      const isUl = lines.every((l) => /^\s*[-*]\s+/.test(l)) && lines.length > 0;
      const isOl = lines.every((l) => /^\s*\d+[.)]\s+/.test(l)) && lines.length > 0;
      if (isUl) return "<ul>" + lines.map((l) => `<li>${l.replace(/^\s*[-*]\s+/, "")}</li>`).join("") + "</ul>";
      if (isOl) return "<ol>" + lines.map((l) => `<li>${l.replace(/^\s*\d+[.)]\s+/, "")}</li>`).join("") + "</ol>";
      if (para.indexOf(" BLOCK") === 0) return para; // standalone code block placeholder
      return `<p>${para.replace(/\n/g, "<br>")}</p>`;
    });

    let out = chunks.join("");
    // restore code blocks
    out = out.replace(/(?:<p>)? BLOCK(\d+) (?:<\/p>)?/g, (m, i) => blocks[+i] || "");
    return out;
  }

  // ===================================================================
  // keep-warm heartbeat
  // While a tab is VISIBLE, ping /query every 10 min so the scale-to-zero HF embed/rerank
  // endpoints and the serverless SQL stay hot for whoever's looking. One /query exercises
  // the whole chain (site -> SQL -> embed -> rerank), so it warms all three in a single
  // call. Hidden tabs are skipped: setInterval keeps firing in a backgrounded tab, so
  // without the visibility gate one pinned tab bills the HF endpoints around the clock.
  // ===================================================================
  const HEARTBEAT_MS = 10 * 60 * 1000; // 10 minutes
  let heartbeatBusy = false;
  let lastBeatAt = 0;

  function tabVisible() {
    return document.visibilityState === "visible";
  }

  async function heartbeat() {
    if (heartbeatBusy) return;              // don't stack pings if one runs long (cold start)
    if (!tabVisible()) return;              // nobody's looking — let the backend cool down
    heartbeatBusy = true;
    lastBeatAt = Date.now();
    healthText.textContent = "keeping warm…";
    try {
      await fetch("/query", {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        body: JSON.stringify({ question: "warmup keepalive", nResults: 1 })
      });
      await loadHealth();                   // refresh the pill with live counts — proof it's warm
    } catch (_) {
      /* best-effort; the next tick tries again — loadHealth on the next beat repairs the pill */
    } finally {
      heartbeatBusy = false;
    }
  }

  // Returning to a tab that has gone cold should warm it right away instead of making the
  // viewer wait out the rest of the interval. Rate-limited to one beat per HEARTBEAT_MS so
  // flipping between tabs can't stack up pings.
  document.addEventListener("visibilitychange", () => {
    if (tabVisible() && Date.now() - lastBeatAt >= HEARTBEAT_MS) heartbeat();
  });

  // ===================================================================
  // boot
  // ===================================================================
  loadHealth();
  loadSystems();
  searchInput.focus();
  setInterval(heartbeat, HEARTBEAT_MS);
})();
