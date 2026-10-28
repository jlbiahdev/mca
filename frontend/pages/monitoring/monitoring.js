/* ── FETEAD — STYX Monitoring (Module 08)
   Temps réel : snapshot REST au chargement / changement de filtre / reconnexion,
   puis deltas SignalR. Aucun rafraîchissement périodique des données.
─────────────────────────────────────────────────────────────────────────── */

// L'API sert elle-même le frontend : URLs relatives.
const MON_API = '/monitoring';
const MON_HUB = '/monitoringHub';

const MON_TRACKED_STATES = ['Submitted', 'Running', 'Finished', 'Failed', 'Canceled'];
const MON_STATE_LABELS = {
  Submitted: 'Soumis', Running: 'Démarrés', Finished: 'Terminés', Failed: 'Échoués', Canceled: 'Annulés',
};
const MON_RANGE_PRESETS = { '1h': 3600e3, '24h': 86400e3, '7d': 7 * 86400e3 };
const MON_DEFAULT_PRESET = '24h';
const MON_TIMELINE_MAX = 200;
const MON_JOBS_LIMIT = 50;
const MON_STORAGE_KEY = 'fetead-monitoring';
const MON_HUB_RETRY_MS = 10000;

/* ════════════════════════════════════════════════════════════════════════
   MODÈLE — fonctions pures, sans DOM (testées sous Node)
════════════════════════════════════════════════════════════════════════ */

function createModel() {
  return {
    from: 0,
    to: 0,
    generatedAt: 0,
    bucketSeconds: 60,
    kpi: emptyKpi(),
    series: emptySeries(1),
    jobs: [],
    jobLimit: MON_JOBS_LIMIT,
  };
}

function emptyKpi() {
  const kpi = {};
  MON_TRACKED_STATES.forEach(state => { kpi[state] = 0; });
  return kpi;
}

function emptySeries(length) {
  const series = {};
  MON_TRACKED_STATES.forEach(state => { series[state] = new Array(length).fill(0); });
  return series;
}

function loadSnapshotIntoModel(model, snapshot) {
  model.from = Date.parse(snapshot.from);
  model.to = Date.parse(snapshot.to);
  model.generatedAt = Date.parse(snapshot.generatedAt);
  model.bucketSeconds = snapshot.bucketSeconds;

  model.kpi = emptyKpi();
  MON_TRACKED_STATES.forEach(state => {
    model.kpi[state] = (snapshot.kpi && snapshot.kpi[state]) || 0;
  });

  const length = Math.max(1, ...(snapshot.series || []).map(s => s.counts.length));
  model.series = emptySeries(length);
  (snapshot.series || []).forEach(s => {
    if (model.series[s.state]) model.series[s.state] = s.counts.slice();
  });

  model.jobs = (snapshot.jobs || []).map(job => Object.assign({}, job));
  return model;
}

/**
 * Applique un JobChanged. L'état connu de la ligne prime sur previousState :
 * un événement déjà reflété par le snapshot n'est ainsi pas compté deux fois.
 */
function applyJobChanged(model, evt, nowMs) {
  const index = model.jobs.findIndex(job => job.env === evt.env && job.id === evt.jobId);
  const existing = index >= 0 ? model.jobs[index] : null;
  const previousState = existing ? existing.state : (evt.previousState || null);
  const stateChanged = previousState !== evt.state;

  if (stateChanged) {
    if (previousState && model.kpi[previousState] > 0) model.kpi[previousState]--;
    if (model.kpi[evt.state] !== undefined) model.kpi[evt.state]++;
    addToSeries(model, evt.state, nowMs);
  }

  const row = existing || {
    env: evt.env, id: evt.jobId, name: null, state: evt.state, progress: 0, taskCount: null, updatedAt: null,
  };
  if (evt.name) row.name = evt.name;
  row.state = evt.state;
  if (evt.progress !== null && evt.progress !== undefined) row.progress = evt.progress;
  row.updatedAt = evt.receivedAt;

  if (index >= 0) model.jobs.splice(index, 1);
  model.jobs.unshift(row);
  if (model.jobs.length > model.jobLimit) model.jobs.length = model.jobLimit;

  return { stateChanged, previousState };
}

/** Ajoute un événement au graphique ; prolonge les séries si le temps dépasse la plage. */
function addToSeries(model, state, nowMs) {
  if (!model.series[state] || !model.bucketSeconds) return;

  const index = Math.floor((nowMs - model.from) / (model.bucketSeconds * 1000));
  if (index < 0) return;

  MON_TRACKED_STATES.forEach(s => {
    while (model.series[s].length <= index) model.series[s].push(0);
  });
  model.series[state][index]++;
  model.to = Math.max(model.to, nowMs);
}

/** Les tâches n'alimentent la timeline qu'en cas d'échec, pour ne pas noyer les jobs. */
function taskTimelineEntry(evt) {
  if (evt.state !== 'Failed') return null;
  const job = evt.jobId !== null && evt.jobId !== undefined ? ` · Job ${evt.jobId}` : '';
  return {
    at: evt.receivedAt,
    env: evt.env,
    state: evt.state,
    text: `Tâche ${evt.externalId ?? evt.taskId}${job}`,
    reason: evt.reason || null,
  };
}

function jobTimelineEntry(evt) {
  return {
    at: evt.receivedAt,
    env: evt.env,
    state: evt.state,
    text: `Job ${evt.jobId}${evt.name ? ' · ' + evt.name : ''}`,
    reason: null,
  };
}

function computeRange(preset, nowMs, customFrom, customTo) {
  if (preset === 'custom') return { from: customFrom, to: customTo };
  const span = MON_RANGE_PRESETS[preset] || MON_RANGE_PRESETS[MON_DEFAULT_PRESET];
  return { from: nowMs - span, to: nowMs };
}

function isLivePreset(preset) {
  return preset !== 'custom';
}

/** Graduations entières de l'axe Y : 4 intervalles réguliers. */
function chartTicks(maxValue) {
  const step = Math.max(1, Math.ceil(maxValue / 4));
  return [0, step, step * 2, step * 3, step * 4];
}

function buildChartSvg(model, width, height) {
  const pad = { left: 40, right: 14, top: 12, bottom: 26 };
  const plotW = width - pad.left - pad.right;
  const plotH = height - pad.top - pad.bottom;
  const points = Math.max(1, ...MON_TRACKED_STATES.map(s => model.series[s].length));
  const maxValue = Math.max(0, ...MON_TRACKED_STATES.map(s => Math.max(0, ...model.series[s])));
  const ticks = chartTicks(maxValue);
  const top = ticks[ticks.length - 1];

  const x = i => pad.left + (points === 1 ? plotW / 2 : (i * plotW) / (points - 1));
  const y = v => pad.top + plotH - (v / top) * plotH;

  const parts = [`<svg viewBox="0 0 ${width} ${height}" role="img" aria-label="Évolution des DataJobs">`];

  ticks.forEach(tick => {
    const ty = y(tick).toFixed(1);
    parts.push(`<line class="grid-line" x1="${pad.left}" x2="${width - pad.right}" y1="${ty}" y2="${ty}"/>`);
    parts.push(`<text class="axis-label" x="${pad.left - 8}" y="${ty}" text-anchor="end" dominant-baseline="middle">${tick}</text>`);
  });

  const longRange = (points * model.bucketSeconds) > 86400;
  const labelIndexes = [...new Set([0, 1, 2, 3, 4].map(k => Math.round((k * (points - 1)) / 4)))];
  labelIndexes.forEach(i => {
    const label = formatAxisTime(model.from + i * model.bucketSeconds * 1000, longRange);
    parts.push(`<text class="axis-label" x="${x(i).toFixed(1)}" y="${height - 6}" text-anchor="middle">${label}</text>`);
  });

  MON_TRACKED_STATES.forEach(state => {
    const coords = model.series[state].map((v, i) => `${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(' ');
    parts.push(`<polyline class="series series-${state}" points="${coords}"/>`);
  });

  parts.push('</svg>');
  return parts.join('');
}

function formatAxisTime(ms, withDate) {
  const d = new Date(ms);
  const pad2 = n => String(n).padStart(2, '0');
  const time = `${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
  return withDate ? `${pad2(d.getDate())}/${pad2(d.getMonth() + 1)} ${time}` : time;
}

/* ════════════════════════════════════════════════════════════════════════
   PAGE — état, rendu, SignalR
════════════════════════════════════════════════════════════════════════ */

const mon = {
  model: createModel(),
  environments: [],          // [{ env, status, since }]
  selectedEnvs: new Set(),
  selectionInitialized: false, // la sélection n'est initialisée qu'une fois (préférences ou tout)
  preset: MON_DEFAULT_PRESET,
  loading: false,
  pending: [],               // événements reçus pendant un chargement
  requestSeq: 0,             // ignore les réponses de snapshot périmées
  timeline: [],
};

function monEsc(value) {
  if (value === null || value === undefined) return '';
  return String(value)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;')
    .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function monFmt(iso) {
  if (!iso) return '—';
  return new Date(iso).toLocaleString('fr-FR', {
    day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit',
  });
}

function monSdot(state) {
  const safe = monEsc(state);
  return `<span class="s-wrap s-${safe}"><span class="s-dot"></span>${safe}</span>`;
}

function isLive() {
  return isLivePreset(mon.preset);
}

// ── Préférences (par navigateur, facultatives) ──
function readPrefs() {
  try { return JSON.parse(localStorage.getItem(MON_STORAGE_KEY)) || {}; } catch (e) { return {}; }
}

function savePrefs() {
  try {
    localStorage.setItem(MON_STORAGE_KEY, JSON.stringify({ envs: [...mon.selectedEnvs], preset: mon.preset }));
  } catch (e) { /* stockage indisponible : sans conséquence */ }
}

// ── API ──
async function monFetch(path) {
  const response = await fetch(MON_API + path);
  if (!response.ok) {
    let message = response.status + ' ' + response.statusText;
    try { message = (await response.json()).message || message; } catch (e) { /* corps non JSON */ }
    throw new Error(message);
  }
  return response.json();
}

async function loadEnvironments() {
  mon.environments = await monFetch('/environments');
  const known = mon.environments.map(e => e.env);
  const saved = (readPrefs().envs || []).filter(code => known.includes(code));
  if (!mon.selectionInitialized) {
    mon.selectedEnvs = new Set(saved.length ? saved : known);
    mon.selectionInitialized = true;
  }
  renderEnvTabs();
}

async function loadSnapshot() {
  const seq = ++mon.requestSeq;

  if (!mon.selectedEnvs.size) {
    mon.model = createModel();
    showMessage('Aucun environnement sélectionné.');
    renderAll();
    return;
  }

  const range = currentRange();
  if (!range) return;

  mon.loading = true;
  mon.pending = [];

  const query = '?envs=' + encodeURIComponent([...mon.selectedEnvs].join(','))
    + '&from=' + encodeURIComponent(new Date(range.from).toISOString())
    + '&to=' + encodeURIComponent(new Date(range.to).toISOString());

  try {
    const snapshot = await monFetch('/snapshot' + query);
    if (seq !== mon.requestSeq) return;

    mon.model = loadSnapshotIntoModel(createModel(), snapshot);
    showSnapshotErrors(snapshot.errors || []);
    replayPending();
    renderAll();
  } catch (error) {
    if (seq === mon.requestSeq) showMessage('Chargement impossible : ' + error.message);
  } finally {
    if (seq === mon.requestSeq) mon.loading = false;
  }
}

function currentRange() {
  if (mon.preset !== 'custom') return computeRange(mon.preset, Date.now());

  const from = Date.parse($('#range-from').val());
  const to = Date.parse($('#range-to').val());
  if (isNaN(from) || isNaN(to) || to <= from) {
    showMessage('Période personnalisée invalide : la fin doit suivre le début.');
    return null;
  }
  return computeRange('custom', Date.now(), from, to);
}

function replayPending() {
  const events = mon.pending;
  mon.pending = [];
  events
    .filter(item => Date.parse(item.evt.receivedAt) > mon.model.generatedAt)
    .forEach(item => (item.type === 'job' ? handleJobChanged(item.evt) : handleTaskChanged(item.evt)));
}

// ── Événements temps réel ──
function acceptsEvent(evt) {
  return isLive() && mon.selectedEnvs.has(evt.env);
}

function handleJobChanged(evt) {
  const result = applyJobChanged(mon.model, evt, Date.parse(evt.receivedAt) || Date.now());
  if (result.stateChanged) pushTimeline(jobTimelineEntry(evt));
}

function handleTaskChanged(evt) {
  const entry = taskTimelineEntry(evt);
  if (entry) pushTimeline(entry);
}

function onJobChanged(evt) {
  if (!acceptsEvent(evt)) return;
  if (mon.loading) { mon.pending.push({ type: 'job', evt }); return; }
  handleJobChanged(evt);
  renderKpi();
  renderJobs();
  renderChart();
}

function onTaskChanged(evt) {
  if (!acceptsEvent(evt)) return;
  if (mon.loading) { mon.pending.push({ type: 'task', evt }); return; }
  handleTaskChanged(evt);
}

function onEnvironmentStatusChanged(evt) {
  const environment = mon.environments.find(e => e.env === evt.env);
  if (environment) Object.assign(environment, evt);
  renderEnvTabs();

  // Reprise d'écoute : des NOTIFY ont pu être perdus pendant la coupure.
  if (evt.status === 'Listening' && mon.selectedEnvs.has(evt.env) && isLive()) loadSnapshot();
}

function pushTimeline(entry) {
  mon.timeline.unshift(entry);
  if (mon.timeline.length > MON_TIMELINE_MAX) mon.timeline.length = MON_TIMELINE_MAX;
  renderTimeline();
}

// ── Rendu ──
function renderAll() {
  renderModeLabel();
  renderKpi();
  renderChart();
  renderJobs();
  renderTimeline();
}

function renderModeLabel() {
  $('#mode-label').text(isLive() ? 'En direct' : 'Vue figée');
}

function envDotClass(status) {
  switch (status) {
    case 'Listening': return 'live';
    case 'Unavailable': return 'unavailable';
    default: return 'reconnecting';
  }
}

function renderEnvTabs() {
  if (!mon.environments.length) {
    $('#env-tabs').html('<span class="mon-hint">Aucun environnement configuré</span>');
    return;
  }
  const html = mon.environments.map(e => `
    <button class="mode-tab ${mon.selectedEnvs.has(e.env) ? 'active' : ''}" data-env="${monEsc(e.env)}" title="${monEsc(e.status)}">
      <span class="conn-dot ${envDotClass(e.status)}"></span>${monEsc(e.env)}
    </button>`).join('');
  $('#env-tabs').html(html);
}

function renderRangeTabs() {
  $('#range-tabs .mode-tab').each(function () {
    $(this).toggleClass('active', $(this).data('range') === mon.preset);
  });
  $('#custom-range').toggleClass('open', mon.preset === 'custom');
}

function renderKpi() {
  MON_TRACKED_STATES.forEach(state => $('#stat-' + state).text(mon.model.kpi[state] || 0));
}

function renderChart() {
  // Largeur réelle du conteneur : le SVG garde des textes à taille constante.
  const width = Math.max(320, Math.round($('#chart').width() || 1000));
  $('#chart').html(buildChartSvg(mon.model, width, 240));
}

function renderLegend() {
  const html = MON_TRACKED_STATES
    .map(state => `<span class="s-wrap s-${state}"><span class="s-dot"></span>${MON_STATE_LABELS[state]}</span>`)
    .join('');
  $('#chart-legend').html(html);
}

function renderJobs() {
  const jobs = mon.model.jobs;
  $('#jobs-count').text(jobs.length);
  if (!jobs.length) {
    $('#jobs-tbody').html('<tr class="loading-row"><td colspan="7">Aucun job sur la période</td></tr>');
    return;
  }
  const html = jobs.map(job => {
    const progress = Math.max(0, Math.min(100, Number(job.progress) || 0));
    return `<tr>
      <td class="td-env">${monEsc(job.env)}</td>
      <td class="td-id">#${monEsc(job.id)}</td>
      <td class="td-name" title="${monEsc(job.name)}">${monEsc(job.name) || '—'}</td>
      <td>${monSdot(job.state)}</td>
      <td><div class="mon-progress">
        <div class="mon-progress-track"><div class="mon-progress-fill" style="width:${progress}%"></div></div>
        <span class="mon-progress-value">${progress}%</span>
      </div></td>
      <td class="td-id">${job.taskCount === null || job.taskCount === undefined ? '—' : monEsc(job.taskCount)}</td>
      <td class="td-date">${monFmt(job.updatedAt)}</td>
    </tr>`;
  }).join('');
  $('#jobs-tbody').html(html);
}

function renderTimeline() {
  if (!mon.timeline.length) {
    $('#timeline-body').html('<div class="empty-row">En attente d\'événements…</div>');
    return;
  }
  const html = mon.timeline.map(entry => `
    <div class="mon-tl-row">
      <span class="mon-tl-time">${new Date(entry.at).toLocaleTimeString('fr-FR')}</span>
      <span class="mon-tl-env">[${monEsc(entry.env)}]</span>
      <span class="mon-tl-text">${monEsc(entry.text)}${entry.reason ? ` <span class="mon-tl-reason">${monEsc(entry.reason)}</span>` : ''}</span>
      ${monSdot(entry.state)}
    </div>`).join('');
  $('#timeline-body').html(html);
}

function showMessage(text) {
  $('#mon-message').text(text).addClass('visible');
}

function showSnapshotErrors(errors) {
  if (!errors.length) { $('#mon-message').removeClass('visible').text(''); return; }
  showMessage(errors.map(e => `${e.env} : ${e.message}`).join(' · '));
}

// ── SignalR ──
function setConnStatus(status) {
  const dot = $('#conn-dot');
  const label = $('#conn-label');
  dot.removeClass('live offline reconnecting');
  switch (status) {
    case 'live': dot.addClass('live'); label.text('En direct'); break;
    case 'offline': dot.addClass('offline'); label.text('Déconnecté'); break;
    case 'reconnecting': dot.addClass('reconnecting'); label.text('Reconnexion…'); break;
    default: label.text('—');
  }
}

function initHub() {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(MON_HUB)
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  connection.on('JobChanged', onJobChanged);
  connection.on('TaskChanged', onTaskChanged);
  connection.on('EnvironmentStatusChanged', onEnvironmentStatusChanged);

  connection.onreconnecting(() => setConnStatus('reconnecting'));
  connection.onreconnected(() => { setConnStatus('live'); resync(); });
  connection.onclose(() => { setConnStatus('offline'); scheduleHubStart(connection); });

  startHub(connection);
}

// Relance de la connexion (et non des données) quand le hub n'a jamais répondu.
function startHub(connection) {
  connection.start()
    .then(() => { setConnStatus('live'); resync(); })
    .catch(() => {
      setConnStatus('offline');
      // Sans hub, la page affiche quand même l'état de la période (sans mise à jour en direct).
      if (!mon.environments.length) resync();
      scheduleHubStart(connection);
    });
}

function scheduleHubStart(connection) {
  setTimeout(() => {
    if (connection.state === signalR.HubConnectionState.Disconnected) startHub(connection);
  }, MON_HUB_RETRY_MS);
}

/** Recharge statuts et snapshot : les événements manqués pendant une coupure sont rattrapés. */
function resync() {
  loadEnvironments()
    .then(loadSnapshot)
    .catch(error => showMessage('Chargement impossible : ' + error.message));
}

// ── Interactions ──
function bindControls() {
  $('#env-tabs').on('click', '.mode-tab', function () {
    const code = String($(this).data('env'));
    if (mon.selectedEnvs.has(code)) mon.selectedEnvs.delete(code); else mon.selectedEnvs.add(code);
    savePrefs();
    renderEnvTabs();
    loadSnapshot();
  });

  $('#range-tabs').on('click', '.mode-tab', function () {
    const preset = String($(this).data('range'));
    if (preset === 'custom') prefillCustomRange();
    mon.preset = preset;
    savePrefs();
    renderRangeTabs();
    renderModeLabel();
    if (preset !== 'custom') loadSnapshot();
  });

  $('#range-apply').on('click', loadSnapshot);
  $('#btnHelp').on('click', () => $('#helpOverlay').addClass('open'));
}

function prefillCustomRange() {
  if ($('#range-from').val() && $('#range-to').val()) return;
  const range = computeRange(mon.preset, Date.now());
  $('#range-from').val(toLocalInputValue(range.from));
  $('#range-to').val(toLocalInputValue(range.to));
}

function toLocalInputValue(ms) {
  const d = new Date(ms);
  const pad2 = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}T${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
}

function updateClock() {
  $('#clock').text(new Date().toLocaleTimeString('fr-FR'));
}

function initMonitoring() {
  const prefs = readPrefs();
  // Une période personnalisée n'est pas restaurée : on rouvre toujours en direct.
  mon.preset = MON_RANGE_PRESETS[prefs.preset] ? prefs.preset : MON_DEFAULT_PRESET;

  bindControls();
  $(window).on('resize', renderChart);
  renderRangeTabs();
  renderLegend();
  renderAll();

  updateClock();
  setInterval(updateClock, 1000); // horloge locale uniquement, aucune donnée

  initHub();
}

if (typeof window !== 'undefined' && window.jQuery) {
  $(initMonitoring);
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    createModel,
    loadSnapshotIntoModel,
    applyJobChanged,
    addToSeries,
    taskTimelineEntry,
    jobTimelineEntry,
    computeRange,
    isLivePreset,
    chartTicks,
    buildChartSvg,
  };
}
