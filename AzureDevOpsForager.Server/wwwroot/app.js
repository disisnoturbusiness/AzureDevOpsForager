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
  // mode switching
  // ===================================================================
  function setMode(mode) {
    const isSearch = mode === "search";
    tabSearch.classList.toggle("is-active", isSearch);
    tabChat.classList.toggle("is-active", !isSearch);
    tabSearch.setAttribute("aria-selected", String(isSearch));
    tabChat.setAttribute("aria-selected", String(!isSearch));
    panelSearch.classList.toggle("is-hidden", !isSearch);
    panelChat.classList.toggle("is-hidden", isSearch);
    setTimeout(() => (isSearch ? searchInput : chatInput).focus(), 60);
  }
  tabSearch.addEventListener("click", () => setMode("search"));
  tabChat.addEventListener("click", () => setMode("chat"));

  // ===================================================================
  // health + facets
  // ===================================================================
  async function loadHealth() {
    try {
      const r = await fetch("/health", { headers: { Accept: "application/json" } });
      if (!r.ok) throw new Error("status " + r.status);
      const h = await r.json();
      const files = h.ftsFileCount ?? h.FtsFileCount;
      const vectors = h.vectorPointCount ?? h.VectorPointCount;
      healthEl.classList.add("ok");
      healthEl.classList.remove("bad");
      const fileTxt = files != null ? Number(files).toLocaleString() + " files" : "online";
      const vecTxt = vectors != null && Number(vectors) > 0 ? " · " + Number(vectors).toLocaleString() + " chunks" : "";
      healthText.textContent = fileTxt + vecTxt;
    } catch (e) {
      healthEl.classList.add("bad");
      healthEl.classList.remove("ok");
      healthText.textContent = "backend unreachable";
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
  searchForm.addEventListener("submit", async (e) => {
    e.preventDefault();
    const q = searchInput.value.trim();
    if (!q) { searchInput.focus(); return; }

    searchBtn.disabled = true;
    searchBtn.classList.add("is-loading");
    searchStatus.classList.remove("err");
    searchStatus.textContent = "searching…";
    searchEmpty.style.display = "none";
    // Same cold-start hint for search (first query after idle warms the embedding endpoint).
    const warmTimer = setTimeout(() => {
      searchStatus.textContent = "waking up the server - the search endpoints and the database can take up to 2 minutes to spin up on the first query after an idle period...";
    }, 6000);

    const body = {
      question: q,
      moduleFilter: moduleFilter.value || "All",
      nResults: parseInt(nResultsSel.value, 10) || 5
    };

    const t0 = performance.now();
    try {
      const r = await fetch("/query", {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        body: JSON.stringify(body)
      });
      if (!r.ok) throw new Error("Server returned HTTP " + r.status);
      const data = await r.json();

      if (data.error) {
        renderSearchError(data.error);
        return;
      }

      const ids = (data.ids && data.ids[0]) || [];
      const docs = (data.documents && data.documents[0]) || [];
      const metas = (data.metadatas && data.metadatas[0]) || [];
      const ms = Math.round(performance.now() - t0);

      renderResults(ids, docs, metas, ms);
    } catch (err) {
      renderSearchError(err.message || "Search failed");
    } finally {
      clearTimeout(warmTimer);
      searchBtn.disabled = false;
      searchBtn.classList.remove("is-loading");
    }
  });

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
      searchStatus.textContent = `no results in ${ms} ms`;
      searchEmpty.style.display = "block";
      searchEmpty.querySelector(".empty-glyph").textContent = "∅";
      searchEmpty.querySelector("p").textContent = "No matches found. Try broadening the query or clearing the filters.";
      return;
    }

    searchStatus.innerHTML =
      `<span class="hl">${ids.length}</span> result${ids.length === 1 ? "" : "s"} in <span class="hl">${ms}</span> ms`;

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

  function buildResult(idx, path, content, meta) {
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

    // auto-expand the top result
    if (idx === 0) { card.classList.add("open"); head.setAttribute("aria-expanded", "true"); }

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

  chatForm.addEventListener("submit", async (e) => {
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
      appendAnswer(answer, sources, q);
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

  function appendAnswer(answer, sources, question) {
    const el = document.createElement("div");
    el.className = "msg msg-a";

    let html = `<div class="answer-body">${renderMarkdown(answer)}</div>`;

    const srcList = normalizeSources(sources);
    if (srcList.length) {
      html += `<div class="sources"><div class="sources-title">Sources</div>` +
        srcList.map((s) => `<span class="source-chip">${esc(s)}</span>`).join("") + `</div>`;
    }

    el.innerHTML = html;
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
  // boot
  // ===================================================================
  loadHealth();
  loadSystems();
  searchInput.focus();
})();
