"use strict";

(() => {
  const COLUMN_WIDTH_DEBOUNCE_MS = 450;
  const SEARCH_INPUT_DEBOUNCE_MS = 40;
  const view = document.documentElement.dataset.view || "main";
  const app = document.getElementById("app");
  const bridge = window.chrome && window.chrome.webview;
  const initialLanguage = () => {
    try {
      const parameter = new URLSearchParams(window.location.search).get("lang");
      if (parameter && window.SW_STRINGS && window.SW_STRINGS[parameter]) return parameter;
    } catch (error) { /* query parameters are optional */ }
    return (window.SW_STRINGS && window.SW_DEFAULT_LANG) || "zh-CN";
  };
  let language = initialLanguage();
  const t = key => window.SW_T(language, key);
  let state = {};
  let selectedPath = "";
  let searchTimer = 0;
  let widthTimer = 0;
  let renderFrame = 0;
  let loadingMore = false;
  let preferencesOpen = false;
  let preferencesDirty = false;
  let preferencesSaving = false;
  let inputSequence = 0;
  let lastPaintSequence = -1;
  let requestedWindowOffset = -1;
  let lastQueryRevision = -1;
  let selectedIndex = -1;
  let latestQueryUiId = -1;
  let latestClientQuerySequence = -1;
  let latestQueryUiPhase = "idle";
  let queryProgressFadeTimer = 0;
  let activeFileDragGestureId = "";
  let pendingNativeFileDrag = null;
  const mainPageCache = new Map();
  const MAIN_PAGE_CACHE_LIMIT = 4;

  const post = (type, payload = {}, requestId) => {
    if (!bridge) return;
    const message = { type, payload };
    if (requestId) message.requestId = requestId;
    bridge.postMessage(message);
  };

  function applyDocumentLanguage() {
    document.documentElement.lang = language;
    document.title = t(view === "preferences" ? "title.preferences"
      : view === "property" ? "title.property"
      : view === "taskpane" ? "title.taskpane"
      : "title.search");
  }

  const text = value => value == null ? "" : String(value);
  const formatProgressPercent = value => value == null ? "--%" : `${value.toFixed(2)}%`;
  const escapeText = value => text(value).replace(/[&<>"']/g, character => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"
  })[character]);

  const searchViews = new Set(["main", "maintoolbar", "taskpane"]);
  const isDesktopSearchView = view === "main" || view === "maintoolbar";
  const supportsPreferences = view === "main" || view === "preferences";
  applyDocumentLanguage();
  if (view === "preferences") renderPreferencesShell();
  else if (searchViews.has(view)) renderSearchShell();
  else renderPropertyShell();

  if (bridge) {
    bridge.addEventListener("message", event => receive(event.data));
  }
  post("ready", { view });

  function receive(message) {
    if (!message || typeof message !== "object" || typeof message.type !== "string" ||
        !message.payload || typeof message.payload !== "object") return;
    if (message.type === "state") {
      const payload = { ...message.payload };
      const nextLanguage = typeof payload.language === "string" &&
        window.SW_STRINGS && window.SW_STRINGS[payload.language] ? payload.language : "";
      if (searchViews.has(view) && Array.isArray(payload.results)) {
        const batch = payload.results;
        const start = Number.isInteger(payload.resultStart) ? payload.resultStart : 0;
        if (view === "main" && payload.resultsMode !== "none") {
          if (payload.resultSetId !== state.resultSetId) {
            mainPageCache.clear();
            selectedIndex = -1;
          }
          payload.results = batch.slice(0, 500);
          const base = Number.isInteger(payload.resultBaseOffset) ? payload.resultBaseOffset : start;
          const key = `${payload.resultSetId}:${base}`;
          mainPageCache.delete(key);
          mainPageCache.set(key, { base, results: payload.results });
          while (mainPageCache.size > MAIN_PAGE_CACHE_LIMIT) {
            mainPageCache.delete(mainPageCache.keys().next().value);
          }
        } else if (payload.resultsMode === "none") {
          delete payload.results;
        } else if (payload.resultsMode === "append" && Array.isArray(state.results) &&
            payload.resultSetId === state.resultSetId && start === state.results.length) {
          state.results.push(...batch);
          delete payload.results;
        } else if (payload.resultsMode === "append") {
          delete payload.results;
          post("ready", { view });
        } else {
          payload.results = batch.slice();
        }
      }
      if (view === "main" && Number.isInteger(payload.resultBaseOffset) &&
          payload.resultBaseOffset === requestedWindowOffset) requestedWindowOffset = -1;
      state = { ...state, ...payload };
      if (nextLanguage && nextLanguage !== language) {
        language = nextLanguage;
        applyDocumentLanguage();
        if (view === "preferences") renderPreferencesShell();
        else if (searchViews.has(view)) renderSearchShell();
        else renderPropertyShell();
      }
      if (searchViews.has(view)) {
        if (view === "main" && payload.resultsMode === "none") updateSearchStatusState();
        else updateSearchState();
        if (Object.hasOwn(message.payload, "preferences")) updatePreferencesState(message.payload.preferences);
      } else if (view === "preferences") {
        if (Object.hasOwn(message.payload, "preferences")) updatePreferencesState(message.payload.preferences);
      } else updatePropertyState();
    } else if (message.type === "focusSearch") {
      focusSearch();
    } else if (message.type === "fileDrag.gesture" && view === "main") {
      const gestureId = message.payload && message.payload.gestureId;
      if (typeof gestureId === "string" && /^[0-9a-f]{32}$/i.test(gestureId)) {
        activeFileDragGestureId = gestureId;
        tryArmNativeFileDrag();
      }
    }
  }

  function renderSearchShell() {
    if (view === "maintoolbar") {
      renderMainToolbarShell();
      return;
    }

    app.innerHTML = `<section class="shell">
      <div class="toolbar">
        <span class="search-wrap"><span id="scopeToken" class="scope-token" hidden><span id="scopePath"></span><button id="clearScope" type="button" title="${escapeText(t("scope.exit"))}" aria-label="${escapeText(t("scope.exit"))}">×</button></span><input id="search" class="search" type="search" autocomplete="off" spellcheck="false" aria-label="${escapeText(t("search.aria"))}" placeholder="${escapeText(t("search.placeholder"))}"></span>
        ${view === "taskpane"
          ? `<span id="modeButton" class="mode mode-fixed" role="status" aria-label="${escapeText(t("mode.fixed.aria"))}">${escapeText(t("mode.library"))}</span>`
          : desktopModeButtonsMarkup()}
        <button id="retry" hidden>${escapeText(t("retry"))}</button>
        <button id="settings" title="${escapeText(t("settings"))}" aria-label="${escapeText(t("settings"))}">⚙</button>
      </div>
      ${view === "taskpane" ? "" : `<div id="results" class="results" tabindex="0" aria-label="${escapeText(t("results.aria"))}"><div id="grid" class="grid"><div id="header" class="header"></div><div id="rows" class="rows"></div></div></div><div class="statusbar"><span id="status">${escapeText(t("status.searching"))}</span><div id="indexProgress" class="index-progress" role="progressbar" aria-label="${escapeText(t("index.aria"))}" aria-valuemin="0" aria-valuemax="100" aria-valuetext="${escapeText(window.SW_FORMAT(t("index.label"), "--%", "--%", "--%"))}"><span class="index-progress-track" aria-hidden="true"><span id="fileProgressFill" class="index-progress-fill file"></span><span id="cachedCoverageProgressFill" class="index-progress-fill cached-coverage"></span><span id="currentMetadataProgressFill" class="index-progress-fill current-metadata"></span></span><span class="index-progress-label-layer" aria-hidden="true"><span id="indexProgressLabel" class="index-progress-label">${escapeText(window.SW_FORMAT(t("index.label"), "--%", "--%", "--%"))}</span><span id="indexProgressLabelOnDark" class="index-progress-label on-dark">${escapeText(window.SW_FORMAT(t("index.label"), "--%", "--%", "--%"))}</span></span></div><span class="status-meta"><span id="version"></span><span id="summary"></span></span></div>`}
    </section>
    <div id="preferencesModal" class="preferences-modal" role="dialog" aria-modal="true" aria-labelledby="preferencesTitle" hidden>${preferencesPanelMarkup()}</div>`;

    const search = document.getElementById("search");
    search.addEventListener("input", () => {
      const sequence = ++inputSequence;
      beginLocalQueryProgress(sequence);
      const startedAt = performance.now();
      clearTimeout(searchTimer);
      searchTimer = setTimeout(() => sendSearch(sequence, startedAt), SEARCH_INPUT_DEBOUNCE_MS);
    });
    search.addEventListener("keydown", event => {
      const scopeToken = document.getElementById("scopeToken");
      if (!scopeToken || scopeToken.hidden) return;
      const clearFromEmptyBackspace = event.key === "Backspace" && search.value.length === 0 &&
        search.selectionStart === 0 && search.selectionEnd === 0;
      if (event.key !== "Escape" && !clearFromEmptyBackspace) return;
      event.preventDefault();
      event.stopPropagation();
      post("restoreScope", {});
    });
    document.getElementById("clearScope").addEventListener("click", () => {
      post("restoreScope", {});
      search.focus();
    });
    if (isDesktopSearchView) {
      bindDesktopModeButtons();
    }
    const settingsButton = document.getElementById("settings");
    if (view !== "main") settingsButton.hidden = true;
    settingsButton.addEventListener("click", openPreferences);
    bindPreferencesControls();
    document.getElementById("retry").addEventListener("click", () => post("retry", { target: "search" }));
    const resultViewport = document.getElementById("results");
    if (resultViewport) {
      resultViewport.addEventListener("scroll", onResultsScroll, { passive: true });
      resultViewport.addEventListener("keydown", onGridKeyDown);
    }
    document.addEventListener("pointerdown", event => {
      dismissContextMenu();
    });
    document.addEventListener("pointerup", clearPendingNativeFileDrag, true);
    document.addEventListener("pointercancel", clearPendingNativeFileDrag, true);
    focusSearch();
  }

  // Keep the form markup in one place.  The desktop host opens this same
  // form in a standalone WebView window, while the legacy full-page shell
  // still uses it in a modal.
  // Language endonyms stay fixed across locales; only "auto" is translated.
  function languageOptionsMarkup() {
    const options = [`<option value="auto">${escapeText(t("prefs.language.auto"))}</option>`];
    (window.SW_LANG_OPTIONS || []).forEach(option => {
      options.push(`<option value="${escapeText(option.value)}">${escapeText(option.label)}</option>`);
    });
    return options.join("");
  }

  function preferencesPanelMarkup() {
    return `<section class="preferences-panel">
      <header class="preferences-header"><h1 id="preferencesTitle">${escapeText(t("prefs.title"))}</h1><button id="closePreferences" class="icon-button" type="button" aria-label="${escapeText(t("prefs.close"))}">×</button></header>
      <div class="preferences-body">
        <section class="preferences-card"><h2>${escapeText(t("prefs.language.title"))}</h2>
          <label class="toggle-row" for="preferenceLanguage"><span><strong>${escapeText(t("prefs.language.label"))}</strong><small>${escapeText(t("prefs.language.hint"))}</small></span><select id="preferenceLanguage" class="preference-select">${languageOptionsMarkup()}</select></label>
        </section>
        <section class="preferences-card"><h2>${escapeText(t("prefs.card.integration"))}</h2>
          <label class="toggle-row"><span><strong>${escapeText(t("prefs.taskpane.title"))}</strong><small>${escapeText(t("prefs.taskpane.desc"))}</small></span><input id="preferenceTaskpane" class="switch" type="checkbox"></label>
          <label class="toggle-row"><span><strong>${escapeText(t("prefs.folderMenu.title"))}</strong><small>${escapeText(t("prefs.folderMenu.desc"))}</small></span><input id="preferenceFolderMenu" class="switch" type="checkbox"></label>
          <label class="toggle-row"><span><strong>${escapeText(t("prefs.fileProps.title"))}</strong><small>${escapeText(t("prefs.fileProps.desc"))}</small></span><input id="preferenceFilePropertiesMenu" class="switch" type="checkbox"></label>
          <label class="toggle-row"><span><strong>${escapeText(t("prefs.startup.title"))}</strong><small>${escapeText(t("prefs.startup.desc"))}</small></span><input id="preferenceStartup" class="switch" type="checkbox"></label>
        </section>
        <section class="preferences-card"><h2>${escapeText(t("prefs.order.title"))}</h2>
          <label for="preferencePropertyDisplayOrder">${escapeText(t("prefs.order.label"))}</label>
          <textarea id="preferencePropertyDisplayOrder" rows="6" spellcheck="false" placeholder="图号&#10;名称&#10;材料名称"></textarea>
          <p class="hint">${escapeText(t("prefs.order.hint"))}</p>
        </section>
        <p id="preferencesStatus" class="preferences-status" role="status" aria-live="polite"></p>
      </div>
      <footer class="preferences-actions"><button id="cancelPreferences" type="button">${escapeText(t("prefs.cancel"))}</button><button id="savePreferences" class="primary" type="button">${escapeText(t("prefs.save"))}</button></footer>
    </section>`;
  }

  function bindPreferencesControls() {
    document.getElementById("closePreferences")?.addEventListener("click", closePreferences);
    document.getElementById("cancelPreferences")?.addEventListener("click", closePreferences);
    document.getElementById("savePreferences")?.addEventListener("click", savePreferences);
    document.querySelectorAll(".preferences-panel input, .preferences-panel textarea, .preferences-panel select").forEach(input => input.addEventListener("input", () => {
      preferencesDirty = true;
    }));
    document.addEventListener("keydown", event => {
      if (event.key === "Escape" && preferencesOpen) closePreferences();
    });
  }

  function renderPreferencesShell() {
    app.innerHTML = `<section class="preferences-page" aria-labelledby="preferencesTitle">${preferencesPanelMarkup()}</section>`;
    bindPreferencesControls();
    preferencesOpen = true;
    preferencesDirty = false;
    preferencesSaving = false;
    document.getElementById("preferencesStatus").textContent = t("prefs.status.loading");
    updatePreferencesState(state.preferences);
    post("preferences.open", {});
    setTimeout(() => document.getElementById("closePreferences")?.focus(), 0);
  }

  // The main desktop window combines this compact WebView toolbar with a
  // native ExplorerBrowser result control.  Do not render a second HTML grid:
  // only the Shell control can expose the original files, their registered
  // thumbnails, standard context menu and CF_HDROP drag data.
  function renderMainToolbarShell() {
    app.innerHTML = `<section class="shell">
      <div class="toolbar">
        <span class="search-wrap"><span id="scopeToken" class="scope-token" hidden><span id="scopePath"></span><button id="clearScope" type="button" title="${escapeText(t("scope.exit"))}" aria-label="${escapeText(t("scope.exit"))}">×</button></span><input id="search" class="search" type="search" autocomplete="off" spellcheck="false" aria-label="${escapeText(t("search.aria"))}" placeholder="${escapeText(t("search.placeholder.toolbar"))}"></span>
        ${desktopModeButtonsMarkup()}
        <button id="retry" hidden>${escapeText(t("retry"))}</button>
        <button id="settings" title="${escapeText(t("settings"))}" aria-label="${escapeText(t("settings"))}">⚙</button>
      </div>
    </section>`;

    const search = document.getElementById("search");
    search.addEventListener("input", () => {
      const sequence = ++inputSequence;
      beginLocalQueryProgress(sequence);
      const startedAt = performance.now();
      clearTimeout(searchTimer);
      searchTimer = setTimeout(() => sendSearch(sequence, startedAt), SEARCH_INPUT_DEBOUNCE_MS);
    });
    search.addEventListener("keydown", event => {
      const scopeToken = document.getElementById("scopeToken");
      if (!scopeToken || scopeToken.hidden) return;
      const clearFromEmptyBackspace = event.key === "Backspace" && search.value.length === 0 &&
        search.selectionStart === 0 && search.selectionEnd === 0;
      if (event.key !== "Escape" && !clearFromEmptyBackspace) return;
      event.preventDefault();
      event.stopPropagation();
      post("restoreScope", {});
    });
    document.getElementById("clearScope").addEventListener("click", () => {
      post("restoreScope", {});
      search.focus();
    });
    bindDesktopModeButtons();
    // The full settings form is hosted by a separate Preferences WebView.
    // Do not create the legacy modal inside this short toolbar WebView.
    document.getElementById("settings").addEventListener("click", () => post("preferences.open", {}));
    document.getElementById("retry").addEventListener("click", () => post("retry", { target: "search" }));
    document.addEventListener("pointerdown", event => {
      dismissContextMenu();
    });
    focusSearch();
  }

  function sendSearch(sequence = ++inputSequence, startedAt = performance.now()) {
    beginLocalQueryProgress(sequence);
    post("search", {
      query: document.getElementById("search").value,
      clientSequence: sequence,
      inputDelayMs: Math.max(0, Math.min(5000, Math.round(performance.now() - startedAt)))
    });
  }

  function desktopModeButtonsMarkup() {
    return `<span id="modeGroup" class="mode-group" role="group" aria-label="${escapeText(t("mode.group"))}"><button type="button" data-mode="all" aria-pressed="true">${escapeText(t("mode.all"))}</button><button type="button" data-mode="name" aria-pressed="false">${escapeText(t("mode.name"))}</button><button type="button" data-mode="property" aria-pressed="false">${escapeText(t("mode.property"))}</button></span>`;
  }

  function bindDesktopModeButtons() {
    const group = document.getElementById("modeGroup");
    if (!group) return;
    group.addEventListener("click", event => {
      const option = event.target.closest("button[data-mode]");
      if (!option || !group.contains(option) || option.getAttribute("aria-pressed") === "true") return;
      updateDesktopModeButtons(option.dataset.mode);
      post("mode", { mode: option.dataset.mode });
      // Flush the visible query after the mode command. If typing is still
      // debounced, the host cancels the stale-mode request and submits the
      // visible query with this newly selected mode.
      sendSearch();
    });
  }

  function updateDesktopModeButtons(mode) {
    document.querySelectorAll("#modeGroup button[data-mode]").forEach(button => {
      button.setAttribute("aria-pressed", String(button.dataset.mode === mode));
    });
  }

  function updateSearchState() {
    const search = document.getElementById("search");
    const queryRevision = Number.isInteger(state.queryRevision) ? state.queryRevision : 0;
    if (queryRevision !== lastQueryRevision) {
      clearTimeout(searchTimer);
      ++inputSequence;
      if (typeof state.query === "string") search.value = state.query;
      lastQueryRevision = queryRevision;
    } else if (document.activeElement !== search && typeof state.query === "string") search.value = state.query;
    const scopeToken = document.getElementById("scopeToken");
    const scopePath = document.getElementById("scopePath");
    const visibleScope = view !== "taskpane" && state.scope && state.scope.folder === true &&
      ["folder", "library"].includes(state.scope.kind) &&
      typeof state.scope.rootPath === "string" && state.scope.rootPath.trim().length > 0;
    scopeToken.hidden = !visibleScope;
    scopePath.textContent = visibleScope ? `${state.scope.kind === "library" ? t("scope.library.prefix") : ""}${state.scope.rootPath}` : "";
    scopeToken.title = visibleScope ? state.scope.rootPath : "";
    const allowedModes = ["all", "name", "property"];
    const mode = view === "taskpane" ? "library" : (allowedModes.includes(state.mode) ? state.mode : "all");
    const labels = { all: t("mode.all"), name: t("mode.name"), property: t("mode.property"), library: t("mode.library") };
    if (view === "taskpane") document.getElementById("modeButton").textContent = labels[mode];
    else updateDesktopModeButtons(mode);
    updateSearchStatusState();
    loadingMore = false;
    const renderStartedAt = performance.now();
    if (view === "main") renderGrid();
    const timing = state.performance;
    if (timing && Number.isInteger(timing.clientSequence) && timing.clientSequence !== lastPaintSequence) {
      lastPaintSequence = timing.clientSequence;
      requestAnimationFrame(() => {
        const renderMs = Math.max(0, Math.round(performance.now() - renderStartedAt));
        const totalMs = Math.max(0, Math.round((timing.inputDelayMs || 0) + (timing.hostMs || 0) + renderMs));
        post("performance.firstPaint", {
          clientSequence: timing.clientSequence,
          totalMs,
          renderMs,
          resultCount: Array.isArray(state.results) ? state.results.length : 0
        });
      });
    }
  }

  function updateSearchStatusState() {
    document.getElementById("retry").hidden = !state.canRetry;
    const status = document.getElementById("status");
    const summary = document.getElementById("summary");
    const version = document.getElementById("version");
    if (status) status.textContent = state.status || (state.busy ? t("status.searching") : "");
    if (summary) summary.textContent = text(state.resultSummary || "");
    if (version) version.textContent = state.version ? `v${state.version}` : "";
    if (isDesktopSearchView) updateQueryProgress();
    if (view === "main") updateIndexProgress();
  }

  function beginLocalQueryProgress(sequence) {
    if (!isDesktopSearchView || !Number.isInteger(sequence)) return;
    latestClientQuerySequence = Math.max(latestClientQuerySequence, sequence);
    setQueryProgressPhase("debouncing");
  }

  function updateQueryProgress() {
    const progress = state.queryProgress;
    if (!progress || typeof progress !== "object" ||
        !Number.isInteger(progress.queryUiId) || progress.queryUiId < 0 ||
        !Number.isInteger(progress.clientSequence) || progress.clientSequence < 0 ||
        !["debouncing", "running", "idle"].includes(progress.phase)) return;
    if (progress.queryUiId < latestQueryUiId ||
        progress.clientSequence < latestClientQuerySequence) return;
    if (progress.queryUiId === latestQueryUiId &&
        progress.clientSequence === latestClientQuerySequence &&
        progress.phase === latestQueryUiPhase) return;
    latestQueryUiId = progress.queryUiId;
    latestClientQuerySequence = progress.clientSequence;
    latestQueryUiPhase = progress.phase;
    setQueryProgressPhase(progress.phase);
  }

  function setQueryProgressPhase(phase) {
    const searchWrap = document.querySelector(".search-wrap");
    const search = document.getElementById("search");
    if (!searchWrap || !search) return;
    clearTimeout(queryProgressFadeTimer);
    searchWrap.dataset.queryPhase = phase;
    const active = phase === "debouncing" || phase === "running";
    searchWrap.classList.toggle("query-progress-active", active);
    searchWrap.classList.toggle("query-progress-fading", phase === "idle");
    search.setAttribute("aria-busy", active ? "true" : "false");
    if (phase === "idle") {
      queryProgressFadeTimer = setTimeout(() => {
        if (searchWrap.dataset.queryPhase === "idle") searchWrap.classList.remove("query-progress-fading");
      }, 360);
    }
  }

  function updateIndexProgress() {
    const root = document.getElementById("indexProgress");
    const fileFill = document.getElementById("fileProgressFill");
    const cachedCoverageFill = document.getElementById("cachedCoverageProgressFill");
    const currentMetadataFill = document.getElementById("currentMetadataProgressFill");
    const label = document.getElementById("indexProgressLabel");
    const labelOnDark = document.getElementById("indexProgressLabelOnDark");
    if (!root || !fileFill || !cachedCoverageFill || !currentMetadataFill || !label || !labelOnDark) return;
    const progress = state.indexProgress && typeof state.indexProgress === "object"
      ? state.indexProgress : {};
    const normalize = value => Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : null;
    const filePercent = normalize(progress.filePercent);
    const cachedCoveragePercent = normalize(progress.cachedCoveragePercent);
    const currentMetadataPercent = normalize(progress.currentMetadataPercent);
    fileFill.style.width = filePercent == null ? "0%" : `${filePercent}%`;
    cachedCoverageFill.style.width = cachedCoveragePercent == null ? "0%" : `${cachedCoveragePercent}%`;
    currentMetadataFill.style.width = currentMetadataPercent == null ? "0%" : `${currentMetadataPercent}%`;
    root.classList.toggle("file-unknown", filePercent == null);
    root.classList.toggle("cached-coverage-unknown", cachedCoveragePercent == null);
    root.classList.toggle("current-metadata-unknown", currentMetadataPercent == null);
    root.style.setProperty("--index-progress-dark-end", `${currentMetadataPercent == null ? 0 : currentMetadataPercent}%`);
    const progressLabel = window.SW_FORMAT(t("index.label"),
      formatProgressPercent(filePercent),
      formatProgressPercent(cachedCoveragePercent),
      formatProgressPercent(currentMetadataPercent));
    label.textContent = progressLabel;
    labelOnDark.textContent = progressLabel;
    root.title = progressLabel;
    root.setAttribute("aria-valuetext", progressLabel);
  }

  function defaultColumns() {
    const columns = [
      { id: "fileName", name: t("column.fileName"), width: 230 }, { id: "kind", name: t("column.kind"), width: 80 },
      { id: "matchedProperty", name: t("column.matchedProperty"), width: 130 }, { id: "matchedValue", name: t("column.matchedValue"), width: 170 },
      { id: "source", name: t("column.source"), width: 95 }, { id: "directory", name: t("column.directory"), width: 310 }
    ];
    return view === "taskpane" ? columns.filter(column => !["source", "directory"].includes(column.id)) : columns;
  }

  // A sort is a Broker query state, not a client-side page sort. The third
  // state deliberately clears the sort so the Broker can restore its default
  // relevance order across the entire result set.
  function nextSortDirection(columnId) {
    const activeDirection = state.sort && state.sort.column === columnId ? state.sort.direction : null;
    return activeDirection === "asc" ? "desc" : activeDirection === "desc" ? "none" : "asc";
  }

  function renderGrid() {
    const suppliedColumns = Array.isArray(state.columns) && state.columns.length ? state.columns : defaultColumns();
    const columns = suppliedColumns.filter(column => column && column.id !== "fullPath" && column.id !== "cacheStatus" &&
      (view !== "taskpane" || !["source", "directory"].includes(column.id)));
    const results = Array.isArray(state.results) ? state.results : [];
    const resultBaseOffset = Number.isInteger(state.resultBaseOffset) && state.resultBaseOffset >= 0 ? state.resultBaseOffset : 0;
    const totalResultCount = Number.isInteger(state.totalResultCount) && state.totalResultCount >= 0
      ? state.totalResultCount : resultBaseOffset + results.length;
    const header = document.getElementById("header");
    header.innerHTML = "";
    columns.forEach(column => {
      const cell = document.createElement("div");
      cell.className = "cell head-cell sortable";
      cell.style.width = `${clampWidth(column.width)}px`;
      cell.dataset.column = column.id;
      cell.textContent = column.name || column.id;
      const sortDirection = state.sort && state.sort.column === column.id &&
        (state.sort.direction === "asc" || state.sort.direction === "desc")
        ? state.sort.direction : null;
      cell.dataset.sortDirection = sortDirection || "none";
      cell.setAttribute("aria-sort", sortDirection === "asc" ? "ascending" : sortDirection === "desc" ? "descending" : "none");
      if (sortDirection) {
        cell.classList.add("sort-active");
        const mark = document.createElement("span"); mark.className = "sort-mark";
        mark.textContent = sortDirection === "desc" ? "\u25bc" : "\u25b2";
        cell.append(mark);
      }
      cell.addEventListener("click", event => {
        if (event.target.classList.contains("resize-handle")) return;
        const direction = nextSortDirection(column.id);
        post("sort", { column: column.id, direction });
      });
      const handle = document.createElement("span"); handle.className = "resize-handle";
      handle.addEventListener("pointerdown", event => startResize(event, column, cell));
      cell.append(handle); header.append(cell);
    });

    const resultsViewport = document.getElementById("results");
    const rowHeight = document.body.classList.contains("compact") ? 26 : 28;
    const rows = document.getElementById("rows");
    const virtualRowCount = view === "taskpane" ? resultBaseOffset + results.length : totalResultCount;
    rows.style.height = `${virtualRowCount * rowHeight}px`;
    rows.style.width = `${columns.reduce((total, column) => total + clampWidth(column.width), 0)}px`;
    renderVisibleRows(columns, results, rowHeight, resultsViewport.scrollTop, resultsViewport.clientHeight, resultBaseOffset);
  }

  function renderViewportRows() {
    const suppliedColumns = Array.isArray(state.columns) && state.columns.length ? state.columns : defaultColumns();
    const columns = suppliedColumns.filter(column => column && column.id !== "fullPath" && column.id !== "cacheStatus" &&
      (view !== "taskpane" || !["source", "directory"].includes(column.id)));
    const results = Array.isArray(state.results) ? state.results : [];
    const resultBaseOffset = Number.isInteger(state.resultBaseOffset) && state.resultBaseOffset >= 0 ? state.resultBaseOffset : 0;
    const viewport = document.getElementById("results");
    const rowHeight = document.body.classList.contains("compact") ? 26 : 28;
    renderVisibleRows(columns, results, rowHeight, viewport.scrollTop, viewport.clientHeight, resultBaseOffset);
  }

  function renderVisibleRows(columns, results, rowHeight, scrollTop, viewportHeight, resultBaseOffset = 0) {
    const rows = document.getElementById("rows");
    const first = Math.max(resultBaseOffset, Math.floor(scrollTop / rowHeight) - 6);
    const last = Math.min(resultBaseOffset + results.length, Math.ceil((scrollTop + viewportHeight) / rowHeight) + 7);
    rows.innerHTML = "";
    const fragment = document.createDocumentFragment();
    for (let index = first; index < last; index++) {
      const result = results[index - resultBaseOffset];
      const row = document.createElement("div");
      row.className = `row${result.fullPath === selectedPath ? " selected" : ""}`;
      row.style.top = `${index * rowHeight}px`;
      row.style.width = rows.style.width;
      row.dataset.index = index - resultBaseOffset;
      columns.forEach(column => {
        const cell = document.createElement("div");
        cell.className = `cell${column.id === "fileName" ? " file-name" : ""}`;
        cell.style.width = `${clampWidth(column.width)}px`; cell.dataset.column = column.id;
        const value = text(result[column.id]);
        cell.title = value;
        if (column.id === "fileName") {
          const icon = document.createElement("span");
          icon.className = `shell-file-icon${isAssemblyFile(result.fileName) ? " assembly" : ""}`;
          icon.setAttribute("aria-hidden", "true");
          const label = document.createElement("span");
          label.className = "shell-file-label";
          label.textContent = value;
          cell.append(icon, label);
        } else {
          cell.textContent = value;
        }
        row.append(cell);
      });
      if (view === "main") row.addEventListener("pointerdown", event => beginNativeFileDrag(event, result));
      row.addEventListener("click", () => {
        selectedPath = result.fullPath;
        selectedIndex = index;
        renderGrid();
      });
      row.addEventListener("dblclick", () => postResultAction("open", result, index - resultBaseOffset));
      row.addEventListener("contextmenu", event => showContextMenu(event, result, index - resultBaseOffset));
      fragment.append(row);
    }
    rows.append(fragment);
  }

  function isAssemblyFile(fileName) {
    return typeof fileName === "string" && fileName.toLowerCase().endsWith(".sldasm");
  }

  function beginNativeFileDrag(event, result) {
    if (view !== "main" || event.button !== 0 || !result || typeof result.fullPath !== "string") return;
    const resultSetId = Number.isInteger(state.resultSetId) && state.resultSetId >= 0 ? state.resultSetId : null;
    const resultRevision = Number.isInteger(state.resultRevision) && state.resultRevision >= 0 ? state.resultRevision : null;
    if (resultSetId == null) return;
    pendingNativeFileDrag = {
      pointerId: event.pointerId,
      path: result.fullPath,
      resultSetId,
      resultRevision,
      armed: false
    };
    tryArmNativeFileDrag();
  }

  function tryArmNativeFileDrag() {
    const pending = pendingNativeFileDrag;
    if (view !== "main" || !pending || pending.armed || !activeFileDragGestureId) return;
    pending.armed = true;
    post("fileDrag.arm", {
      schema: 1,
      gestureId: activeFileDragGestureId,
      resultSetId: pending.resultSetId,
      resultRevision: pending.resultRevision,
      path: pending.path
    });
  }

  function clearPendingNativeFileDrag(event) {
    if (!pendingNativeFileDrag || !event || event.pointerId === pendingNativeFileDrag.pointerId) {
      pendingNativeFileDrag = null;
      activeFileDragGestureId = "";
    }
  }

  function startResize(event, column, cell) {
    event.preventDefault(); event.stopPropagation();
    const startX = event.clientX; const startWidth = clampWidth(column.width);
    event.currentTarget.setPointerCapture(event.pointerId);
    const move = moveEvent => {
      column.width = clampWidth(startWidth + moveEvent.clientX - startX);
      document.querySelectorAll(`[data-column="${column.id}"]`).forEach(target => target.style.width = `${column.width}px`);
      clearTimeout(widthTimer);
      widthTimer = setTimeout(
        () => post("columnWidth", { column: column.id, width: column.width }),
        COLUMN_WIDTH_DEBOUNCE_MS);
    };
    const finish = () => {
      event.currentTarget.removeEventListener("pointermove", move);
      clearTimeout(widthTimer);
      renderGrid();
      post("columnWidth", { column: column.id, width: column.width });
    };
    event.currentTarget.addEventListener("pointermove", move);
    event.currentTarget.addEventListener("pointerup", finish, { once: true });
    event.currentTarget.addEventListener("pointercancel", finish, { once: true });
  }

  function onResultsScroll(event) {
    const target = event.currentTarget;
    cancelAnimationFrame(renderFrame);
    renderFrame = requestAnimationFrame(renderViewportRows);
    if (view === "main") {
      const results = Array.isArray(state.results) ? state.results : [];
      const base = Number.isInteger(state.resultBaseOffset) ? state.resultBaseOffset : 0;
      const total = Number.isInteger(state.totalResultCount) ? state.totalResultCount : base + results.length;
      const rowHeight = document.body.classList.contains("compact") ? 26 : 28;
      const first = Math.max(0, Math.floor(target.scrollTop / rowHeight));
      const last = Math.min(total, Math.ceil((target.scrollTop + target.clientHeight) / rowHeight));
      const outsideWindow = results.length === 0 || first < base + 30 || last > base + results.length - 30;
      if (outsideWindow && total > results.length) {
        const limit = 200;
        const offset = Math.max(0, Math.min(Math.max(0, total - limit), first - 50));
        if (offset !== base && offset !== requestedWindowOffset) {
          const cached = mainPageCache.get(`${state.resultSetId}:${offset}`);
          if (cached) {
            mainPageCache.delete(`${state.resultSetId}:${offset}`);
            mainPageCache.set(`${state.resultSetId}:${offset}`, cached);
            state = { ...state, resultBaseOffset: cached.base, results: cached.results };
            renderViewportRows();
            // Cached rows are display-only until the desktop model has made
            // the same window current again. Rebind it now so a later action
            // carries the identity of the broker-owned current row.
            requestedWindowOffset = offset;
            post("loadMore", { offset, limit });
          } else {
            requestedWindowOffset = offset;
            post("loadMore", { offset, limit });
          }
        }
      }
    }
  }

  function createDesktopResultReference(result, resultIndex) {
    const path = result && typeof result.fullPath === "string" ? result.fullPath : "";
    const resultSetId = Number.isInteger(state.resultSetId) && state.resultSetId >= 0 ? state.resultSetId : null;
    const resultRevision = Number.isInteger(state.resultRevision) && state.resultRevision >= 0 ? state.resultRevision : null;
    const resultStart = Number.isInteger(state.resultBaseOffset) && state.resultBaseOffset >= 0 ? state.resultBaseOffset : null;
    if (!path || resultSetId == null || resultStart == null ||
        !Number.isInteger(resultIndex) || resultIndex < 0 || resultIndex >= 500) return null;
    return { schema: 2, path, resultSetId, resultRevision, resultStart, resultIndex };
  }

  function postResultAction(action, result, resultIndex) {
    const path = result && typeof result.fullPath === "string" ? result.fullPath : "";
    if (!path) {
      showContextActionFeedback(t("feedback.pathUnavailable"), true);
      return;
    }
    if (view !== "main") {
      if (action === "open") post("openResult", { path });
      else post("contextAction", { schema: 1, action, path });
      return;
    }
    const reference = createDesktopResultReference(result, resultIndex);
    if (!reference) {
      showContextActionFeedback(t("feedback.staleReference"), true);
      return;
    }
    post("contextAction", { ...reference, action });
  }

  function showContextMenu(event, result, resultIndex) {
    event.preventDefault(); event.stopPropagation(); dismissContextMenu();
    const rowPath = result && typeof result.fullPath === "string" ? result.fullPath : "";
    if (!rowPath) { showContextActionFeedback(t("feedback.pathUnavailable"), true); return; }
    const desktopReference = view === "main" ? createDesktopResultReference(result, resultIndex) : null;
    if (view === "main" && !desktopReference) {
      showContextActionFeedback(t("feedback.staleReference"), true);
      return;
    }
    selectedPath = rowPath;
    const menu = document.createElement("div");
    menu.className = "context-menu";
    menu.dataset.path = rowPath;
    const actions = [["open", t("ctx.open")], ["reveal", t("ctx.reveal")], ["copyPath", t("ctx.copyPath")], ["viewProperties", t("ctx.viewProperties")]];
    actions
      .forEach(([action, label]) => {
      const button = document.createElement("button");
      button.type = "button";
      button.dataset.action = action;
      button.textContent = label;
        menu.append(button);
      });
    menu.addEventListener("pointerdown", pointerEvent => pointerEvent.stopPropagation());
    menu.addEventListener("click", clickEvent => {
      const button = clickEvent.target.closest("button[data-action]");
      if (!button || !menu.contains(button)) return;
      clickEvent.preventDefault();
      clickEvent.stopPropagation();
      const action = button.dataset.action;
      const path = menu.dataset.path;
      if (desktopReference) post("contextAction", { ...desktopReference, action });
      else post("contextAction", { schema: 1, action, path });
      dismissContextMenu();
      showContextActionFeedback(action === "copyPath" ? t("feedback.copying") : t("feedback.running"), false);
    });
    document.body.append(menu);
    const bounds = menu.getBoundingClientRect();
    const margin = 8;
    const left = Math.max(margin, Math.min(event.clientX, innerWidth - bounds.width - margin));
    const top = Math.max(margin, Math.min(event.clientY, innerHeight - bounds.height - margin));
    menu.style.left = `${left}px`;
    menu.style.top = `${top}px`;
  }

  function showContextActionFeedback(message, failed) {
    const status = document.getElementById("status");
    if (!status) return;
    status.innerHTML = `<i class="cache-dot${failed ? " error" : ""}"></i>${escapeText(message)}`;
  }

  function dismissContextMenu(event) {
    if (event && event.target.closest && event.target.closest(".context-menu")) return;
    document.querySelector(".context-menu")?.remove();
  }

  function onGridKeyDown(event) {
    const results = Array.isArray(state.results) ? state.results : [];
    let index = results.findIndex(item => item.fullPath === selectedPath);
    if (event.key === "ArrowDown") index = Math.min(results.length - 1, index + 1);
    else if (event.key === "ArrowUp") index = Math.max(0, index - 1);
    else if (event.key === "Enter" && index >= 0) postResultAction("open", results[index], index);
    else return;
    event.preventDefault();
    if (index >= 0) {
      selectedPath = results[index].fullPath;
      selectedIndex = index;
      renderGrid();
      document.querySelector(`[data-index="${index}"]`)?.scrollIntoView({ block: "nearest" });
    }
  }

  function openPreferences() {
    if (!supportsPreferences) return;
    preferencesOpen = true;
    preferencesDirty = false;
    preferencesSaving = false;
    const modal = document.getElementById("preferencesModal");
    if (modal) modal.hidden = false;
    document.getElementById("preferencesStatus").textContent = t("prefs.status.loading");
    updatePreferencesState(state.preferences);
    post("preferences.open", {});
    setTimeout(() => document.getElementById("closePreferences")?.focus(), 0);
  }

  function closePreferences() {
    if (!preferencesOpen) return;
    preferencesOpen = false;
    preferencesDirty = false;
    preferencesSaving = false;
    const modal = document.getElementById("preferencesModal");
    if (modal) modal.hidden = true;
    post("preferences.close", {});
    document.getElementById("settings")?.focus();
  }

  function savePreferences() {
    const status = document.getElementById("preferencesStatus");

    preferencesDirty = false;
    preferencesSaving = true;
    status.textContent = t("prefs.status.saving");
    status.dataset.kind = "info";
    post("preferences.save", {
      taskpane: document.getElementById("preferenceTaskpane").checked,
      folderMenu: document.getElementById("preferenceFolderMenu").checked,
      filePropertiesMenu: document.getElementById("preferenceFilePropertiesMenu").checked,
      startup: document.getElementById("preferenceStartup").checked,
      propertyDisplayOrder: document.getElementById("preferencePropertyDisplayOrder").value
        .split(/\r?\n/).map(value => value.trim()).filter(Boolean),
      language: document.getElementById("preferenceLanguage").value
    });
  }

  function updatePreferencesState(next) {
    if (!supportsPreferences) return;
    if (!next || typeof next !== "object") return;
    const values = next.values && typeof next.values === "object" ? next.values : next;
    const busy = !!next.busy;
    const error = text(next.error || "");
    const preserveDraft = preferencesDirty || (preferencesSaving && (busy || !!error));
    if (!preserveDraft) {
      setChecked("preferenceTaskpane", values.taskpane);
      setChecked("preferenceFolderMenu", values.folderMenu);
      setChecked("preferenceFilePropertiesMenu", values.filePropertiesMenu);
      setChecked("preferenceStartup", values.startup);
      if (Array.isArray(values.propertyDisplayOrder)) document.getElementById("preferencePropertyDisplayOrder").value = values.propertyDisplayOrder.join("\n");
      const languageSelect = document.getElementById("preferenceLanguage");
      if (languageSelect && typeof values.language === "string" && values.language) languageSelect.value = values.language;
    }

    if (preferencesSaving && !busy) preferencesSaving = false;
    const status = document.getElementById("preferencesStatus");
    const saveFailures = next.saveResults && typeof next.saveResults === "object"
      ? ["taskpane", "folderMenu", "filePropertiesMenu", "startup"]
          .map(key => next.saveResults[key])
          .filter(item => item && item.attempted && !item.succeeded)
          .map(item => text(item.message))
          .filter(Boolean)
      : [];
    status.textContent = error || saveFailures.join("；") || text(next.status || "");
    status.dataset.kind = error ? "error" : "info";
    document.getElementById("savePreferences").disabled = busy;
    document.getElementById("cancelPreferences").disabled = busy && !!next.blockClose;
  }

  function setChecked(id, value) {
    if (typeof value === "boolean") document.getElementById(id).checked = value;
  }

  function renderPropertyShell() {
    app.innerHTML = `<section class="property-shell"><div class="file-banner"><h1 id="propertyName">${escapeText(t("property.title"))}</h1><div id="propertyPath" class="file-path"></div></div>
      <div class="property-toolbar"><label>${escapeText(t("property.show"))}<select id="propertyMode"><option value="custom">${escapeText(t("property.mode.custom"))}</option><option value="configuration">${escapeText(t("property.mode.configuration"))}</option></select></label><label id="configurationWrap" hidden>${escapeText(t("property.configuration"))}<select id="configuration"></select></label></div><div id="propertyContent" class="property-content"><section class="property-group"><h2>${escapeText(t("property.group.fallback"))}</h2></section></div>
      <div class="statusbar"><span id="propertyStatus">${escapeText(t("property.status.ready"))}</span><span id="propertyKind"></span></div></section>`;
    document.getElementById("configuration").addEventListener("change", event => post("property.select", { configuration: event.target.value }));
    document.getElementById("propertyMode").addEventListener("change", event => post("property.mode", { mode: event.target.value }));
  }

  function updatePropertyState() {
    document.getElementById("propertyName").textContent = text(state.fileName || t("property.title"));
    document.getElementById("propertyPath").textContent = text(state.fullPath || "");
    document.getElementById("propertyKind").textContent = text(state.documentKind || "");
    document.getElementById("propertyStatus").textContent = text(state.status || t("property.status.ready"));
    const select = document.getElementById("configuration");
    const displayMode = ["custom", "configuration"].includes(state.displayMode) ? state.displayMode : "custom";
    document.getElementById("propertyMode").value = displayMode;
    document.getElementById("configurationWrap").hidden = displayMode !== "configuration" || !Array.isArray(state.configurations) || state.configurations.length === 0;
    select.innerHTML = (Array.isArray(state.configurations) ? state.configurations : []).map(item => {
      const value = typeof item === "string" ? item : item.name; return `<option value="${escapeText(value)}">${escapeText(value)}</option>`;
    }).join("");
    select.value = text(state.selectedConfiguration || "");
    const content = document.getElementById("propertyContent"); content.innerHTML = "";
    (Array.isArray(state.groups) ? state.groups : []).forEach(group => content.append(propertyGroup(group.name || group.title, group.properties || group.items)));
  }

  function propertyGroup(name, properties) {
    const section = document.createElement("section"); section.className = "property-group";
    const heading = document.createElement("h2"); heading.textContent = text(name || t("property.row.fallback")); section.append(heading);
    (Array.isArray(properties) ? properties : []).forEach(property => {
      const row = document.createElement("div"); row.className = "property-row";
      const key = document.createElement("div"); key.textContent = text(property.name || property.key);
      const value = document.createElement("div"); value.textContent = text(property.value); row.append(key, value); section.append(row);
    }); return section;
  }

  function focusSearch() { setTimeout(() => { const input = document.getElementById("search"); input?.focus(); input?.select(); }, 0); }
  function clampWidth(value) { const numeric = Number(value); return Math.max(48, Math.min(2000, Number.isFinite(numeric) ? numeric : 120)); }
})();
