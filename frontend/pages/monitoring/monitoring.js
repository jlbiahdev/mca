/* ═══════════════════════════════════════════════════════════════════════
   FETEAD — STYX Monitoring (Module 08)

   Fonctionnement :
   1. Au chargement, on demande à l'API l'état de la période (le "snapshot").
   2. Ensuite, le serveur nous envoie chaque changement via SignalR.
   3. On recharge le snapshot seulement quand l'utilisateur change un filtre
      ou après une coupure. Jamais de rechargement automatique toutes les X secondes.
═══════════════════════════════════════════════════════════════════════ */

// ── Configuration ────────────────────────────────────────────────────────
// L'API sert elle-même cette page : on utilise des adresses relatives.
const API = '/monitoring';
const HUB = '/monitoringHub';

const TRACKED_STATES = ['Submitted', 'Running', 'Finished', 'Failed', 'Canceled'];
const STATE_LABELS = {
  Submitted: 'Soumis',
  Running: 'Démarrés',
  Finished: 'Terminés',
  Failed: 'Échoués',
  Canceled: 'Annulés',
};
const ONE_HOUR = 60 * 60 * 1000;
const PERIODS = { '1h': ONE_HOUR, '24h': 24 * ONE_HOUR, '7d': 7 * 24 * ONE_HOUR };
const DEFAULT_PERIOD = '24h';
const MAX_JOBS = 50;
const MAX_TIMELINE_ENTRIES = 200;
const STORAGE_KEY = 'fetead-monitoring';

// ── État de la page ──────────────────────────────────────────────────────
let data = emptyData();          // ce qui est affiché : KPI, courbes, jobs
let environments = [];           // [{ env: 'LOCAL', status: 'Listening' }, …]
let selectedEnvs = [];           // codes cochés, ex. ['LOCAL', 'POC']
let period = DEFAULT_PERIOD;     // '1h', '24h', '7d' ou 'custom'
let timeline = [];               // derniers événements, le plus récent en premier

let isLoading = false;           // un snapshot est en cours de chargement
let eventsDuringLoading = [];    // événements reçus pendant ce chargement
let lastRequestNumber = 0;       // pour ignorer une réponse arrivée trop tard


/* ═══════════════════════════════════════════════════════════════════════
   1. DONNÉES — fonctions sans affichage (testées dans tests/frontend)
═══════════════════════════════════════════════════════════════════════ */

function emptyData() {
  const kpi = {};
  const series = {};
  TRACKED_STATES.forEach(state => {
    kpi[state] = 0;
    series[state] = [0];
  });
  return { from: 0, to: 0, generatedAt: 0, bucketSeconds: 60, kpi, series, jobs: [] };
}

/** Transforme la réponse de GET /monitoring/snapshot en données de la page. */
function dataFromSnapshot(snapshot) {
  const result = emptyData();
  result.from = Date.parse(snapshot.from);
  result.to = Date.parse(snapshot.to);
  result.generatedAt = Date.parse(snapshot.generatedAt);
  result.bucketSeconds = snapshot.bucketSeconds;

  TRACKED_STATES.forEach(state => {
    result.kpi[state] = snapshot.kpi[state] || 0;
  });

  snapshot.series.forEach(serie => {
    if (result.series[serie.state]) {
      result.series[serie.state] = serie.counts.slice();
    }
  });

  result.jobs = snapshot.jobs.map(job => Object.assign({}, job));
  return result;
}

/**
 * Applique un message "JobChanged" aux données.
 * Renvoie true si l'état du job a changé (et pas seulement sa progression).
 */
function applyJobChanged(pageData, message) {
  let job = pageData.jobs.find(j => j.env === message.env && j.id === message.jobId);

  // Si le job est déjà dans le tableau, son état affiché fait foi :
  // un événement déjà pris en compte par le snapshot n'est pas compté deux fois.
  const oldState = job ? job.state : message.previousState;
  const stateChanged = oldState !== message.state;

  if (stateChanged) {
    if (oldState && pageData.kpi[oldState] > 0) pageData.kpi[oldState]--;
    if (pageData.kpi[message.state] !== undefined) pageData.kpi[message.state]++;
    addPointToChart(pageData, message.state, Date.parse(message.receivedAt));
  }

  if (!job) {
    job = { env: message.env, id: message.jobId, name: null, state: message.state, progress: 0, taskCount: null };
  }
  if (message.name) job.name = message.name;
  if (message.progress !== null && message.progress !== undefined) job.progress = message.progress;
  job.state = message.state;
  job.updatedAt = message.receivedAt;

  // Le job modifié passe en tête du tableau.
  pageData.jobs = [job].concat(pageData.jobs.filter(j => j !== job)).slice(0, MAX_JOBS);

  return stateChanged;
}

/** Ajoute 1 à la courbe d'un état, dans l'intervalle qui contient "time". */
function addPointToChart(pageData, state, time) {
  if (!pageData.series[state]) return;

  const index = Math.floor((time - pageData.from) / (pageData.bucketSeconds * 1000));
  if (index < 0) return;

  // Le temps avance : on allonge toutes les courbes si besoin.
  TRACKED_STATES.forEach(s => {
    while (pageData.series[s].length <= index) pageData.series[s].push(0);
  });

  pageData.series[state][index]++;
}

/** Ligne de timeline pour un job. */
function jobTimelineEntry(message) {
  const name = message.name ? ' · ' + message.name : '';
  return { at: message.receivedAt, env: message.env, state: message.state, text: 'Job ' + message.jobId + name, reason: null };
}

/** Ligne de timeline pour une tâche : seulement les échecs, sinon la timeline serait noyée. */
function taskTimelineEntry(message) {
  if (message.state !== 'Failed') return null;

  const taskNumber = message.externalId !== null && message.externalId !== undefined ? message.externalId : message.taskId;
  const job = message.jobId !== null && message.jobId !== undefined ? ' · Job ' + message.jobId : '';
  return { at: message.receivedAt, env: message.env, state: message.state, text: 'Tâche ' + taskNumber + job, reason: message.reason || null };
}

/** Début et fin de la période choisie, en millisecondes. */
function periodRange(periodCode, now, customFrom, customTo) {
  if (periodCode === 'custom') return { from: customFrom, to: customTo };
  return { from: now - (PERIODS[periodCode] || PERIODS[DEFAULT_PERIOD]), to: now };
}

/** Les périodes 1 h / 24 h / 7 j sont en direct ; la période personnalisée est figée. */
function isLivePeriod(periodCode) {
  return periodCode !== 'custom';
}

/** Graduations de l'axe vertical : 5 valeurs entières régulières. */
function chartTicks(maxValue) {
  const step = Math.max(1, Math.ceil(maxValue / 4));
  return [0, step, step * 2, step * 3, step * 4];
}

/** Dessine le graphique en SVG (une courbe par état). */
function buildChartSvg(pageData, width, height) {
  const left = 40, right = 14, top = 12, bottom = 26;
  const plotWidth = width - left - right;
  const plotHeight = height - top - bottom;

  const pointCount = Math.max(...TRACKED_STATES.map(s => pageData.series[s].length));
  const maxValue = Math.max(...TRACKED_STATES.map(s => Math.max(...pageData.series[s])));
  const ticks = chartTicks(maxValue);
  const topValue = ticks[ticks.length - 1];

  const xOf = i => left + (pointCount === 1 ? plotWidth / 2 : (i * plotWidth) / (pointCount - 1));
  const yOf = value => top + plotHeight - (value / topValue) * plotHeight;

  let svg = `<svg viewBox="0 0 ${width} ${height}" role="img" aria-label="Évolution des DataJobs">`;

  // Lignes horizontales et valeurs de l'axe vertical
  ticks.forEach(tick => {
    const y = yOf(tick).toFixed(1);
    svg += `<line class="grid-line" x1="${left}" x2="${width - right}" y1="${y}" y2="${y}"/>`;
    svg += `<text class="axis-label" x="${left - 8}" y="${y}" text-anchor="end" dominant-baseline="middle">${tick}</text>`;
  });

  // 5 heures sur l'axe horizontal (avec la date si la période dépasse 24 h)
  const showDate = pointCount * pageData.bucketSeconds > 24 * 3600;
  for (let k = 0; k <= 4; k++) {
    const i = Math.round((k * (pointCount - 1)) / 4);
    const label = formatAxisTime(pageData.from + i * pageData.bucketSeconds * 1000, showDate);
    svg += `<text class="axis-label" x="${xOf(i).toFixed(1)}" y="${height - 6}" text-anchor="middle">${label}</text>`;
  }

  // Une courbe par état
  TRACKED_STATES.forEach(state => {
    const points = pageData.series[state].map((value, i) => xOf(i).toFixed(1) + ',' + yOf(value).toFixed(1)).join(' ');
    svg += `<polyline class="series series-${state}" points="${points}"/>`;
  });

  return svg + '</svg>';
}

function formatAxisTime(time, withDate) {
  const d = new Date(time);
  const two = n => String(n).padStart(2, '0');
  const hour = two(d.getHours()) + ':' + two(d.getMinutes());
  return withDate ? two(d.getDate()) + '/' + two(d.getMonth() + 1) + ' ' + hour : hour;
}


/* ═══════════════════════════════════════════════════════════════════════
   2. CHARGEMENT — appels à l'API
═══════════════════════════════════════════════════════════════════════ */

async function callApi(path) {
  const response = await fetch(API + path);
  if (!response.ok) {
    let message = response.status + ' ' + response.statusText;
    try { message = (await response.json()).message || message; } catch (e) { /* réponse non JSON */ }
    throw new Error(message);
  }
  return response.json();
}

async function loadEnvironments() {
  environments = await callApi('/environments');

  // Premier chargement : on reprend le choix mémorisé, sinon tout est coché.
  if (selectedEnvs.length === 0) {
    const allCodes = environments.map(e => e.env);
    const saved = (readSavedChoices().envs || []).filter(code => allCodes.includes(code));
    selectedEnvs = saved.length > 0 ? saved : allCodes;
  }

  renderEnvironmentButtons();
}

async function loadSnapshot() {
  // Chaque chargement a un numéro : si l'utilisateur change de filtre pendant
  // un chargement, la réponse de l'ancien chargement sera ignorée.
  lastRequestNumber++;
  const requestNumber = lastRequestNumber;

  if (selectedEnvs.length === 0) {
    data = emptyData();
    showMessage('Aucun environnement sélectionné.');
    renderAll();
    return;
  }

  const range = currentRange();
  if (!range) return;

  isLoading = true;
  eventsDuringLoading = [];

  const query = '?envs=' + encodeURIComponent(selectedEnvs.join(','))
    + '&from=' + encodeURIComponent(new Date(range.from).toISOString())
    + '&to=' + encodeURIComponent(new Date(range.to).toISOString());

  try {
    const snapshot = await callApi('/snapshot' + query);
    if (requestNumber !== lastRequestNumber) return; // réponse périmée

    data = dataFromSnapshot(snapshot);
    showSnapshotErrors(snapshot.errors);
    replayEventsReceivedDuringLoading();
    renderAll();
  } catch (error) {
    if (requestNumber === lastRequestNumber) showMessage('Chargement impossible : ' + error.message);
  } finally {
    if (requestNumber === lastRequestNumber) isLoading = false;
  }
}

/** Période affichée, ou null si la période personnalisée est invalide. */
function currentRange() {
  if (period !== 'custom') return periodRange(period, Date.now());

  const from = Date.parse($('#range-from').val());
  const to = Date.parse($('#range-to').val());
  if (isNaN(from) || isNaN(to) || to <= from) {
    showMessage('Période personnalisée invalide : la fin doit être après le début.');
    return null;
  }
  return periodRange('custom', Date.now(), from, to);
}

/** Événements arrivés pendant le chargement : on garde ceux postérieurs au snapshot. */
function replayEventsReceivedDuringLoading() {
  eventsDuringLoading
    .filter(item => Date.parse(item.message.receivedAt) > data.generatedAt)
    .forEach(item => {
      if (item.type === 'job') handleJobChanged(item.message);
      else handleTaskChanged(item.message);
    });
  eventsDuringLoading = [];
}

/** Recharge tout (après connexion ou reconnexion au hub). */
function reloadEverything() {
  loadEnvironments()
    .then(loadSnapshot)
    .catch(error => showMessage('Chargement impossible : ' + error.message));
}


/* ═══════════════════════════════════════════════════════════════════════
   3. TEMPS RÉEL — messages SignalR
═══════════════════════════════════════════════════════════════════════ */

function isDisplayed(message) {
  return isLivePeriod(period) && selectedEnvs.includes(message.env);
}

function handleJobChanged(message) {
  const stateChanged = applyJobChanged(data, message);
  if (stateChanged) addToTimeline(jobTimelineEntry(message));
}

function handleTaskChanged(message) {
  const entry = taskTimelineEntry(message);
  if (entry) addToTimeline(entry);
}

function onJobChanged(message) {
  if (!isDisplayed(message)) return;
  if (isLoading) { eventsDuringLoading.push({ type: 'job', message }); return; }

  handleJobChanged(message);
  renderKpi();
  renderJobs();
  renderChart();
}

function onTaskChanged(message) {
  if (!isDisplayed(message)) return;
  if (isLoading) { eventsDuringLoading.push({ type: 'task', message }); return; }

  handleTaskChanged(message);
}

function onEnvironmentStatusChanged(message) {
  const environment = environments.find(e => e.env === message.env);
  if (environment) environment.status = message.status;
  renderEnvironmentButtons();

  // L'écoute de cet environnement vient de reprendre : des événements ont pu
  // être perdus pendant la coupure, on recharge.
  if (message.status === 'Listening' && selectedEnvs.includes(message.env) && isLivePeriod(period)) {
    loadSnapshot();
  }
}

function addToTimeline(entry) {
  timeline.unshift(entry);
  timeline = timeline.slice(0, MAX_TIMELINE_ENTRIES);
  renderTimeline();
}

function setConnectionStatus(status) {
  const dot = $('#conn-dot');
  const label = $('#conn-label');
  dot.removeClass('live offline reconnecting');

  if (status === 'live') { dot.addClass('live'); label.text('En direct'); }
  if (status === 'offline') { dot.addClass('offline'); label.text('Déconnecté'); }
  if (status === 'reconnecting') { dot.addClass('reconnecting'); label.text('Reconnexion…'); }
}

function connectToHub() {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(HUB)
    // Délais entre deux tentatives de reconnexion (en ms).
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000, 30000, 60000, 60000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  connection.on('JobChanged', onJobChanged);
  connection.on('TaskChanged', onTaskChanged);
  connection.on('EnvironmentStatusChanged', onEnvironmentStatusChanged);

  connection.onreconnecting(() => setConnectionStatus('reconnecting'));
  connection.onreconnected(() => { setConnectionStatus('live'); reloadEverything(); });
  connection.onclose(() => setConnectionStatus('offline'));

  connection.start()
    .then(() => setConnectionStatus('live'))
    .catch(() => setConnectionStatus('offline'))
    // Hub joignable ou non, on affiche l'état de la période.
    .finally(reloadEverything);
}


/* ═══════════════════════════════════════════════════════════════════════
   4. AFFICHAGE
═══════════════════════════════════════════════════════════════════════ */

function escapeHtml(value) {
  if (value === null || value === undefined) return '';
  return String(value)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;')
    .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function formatDate(iso) {
  if (!iso) return '—';
  return new Date(iso).toLocaleString('fr-FR', {
    day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit',
  });
}

/** Pastille colorée + nom de l'état (même rendu que TaskFlow). */
function stateBadge(state) {
  const safe = escapeHtml(state);
  return `<span class="s-wrap s-${safe}"><span class="s-dot"></span>${safe}</span>`;
}

function renderAll() {
  $('#mode-label').text(isLivePeriod(period) ? 'En direct' : 'Vue figée');
  renderKpi();
  renderChart();
  renderJobs();
  renderTimeline();
}

function renderEnvironmentButtons() {
  if (environments.length === 0) {
    $('#env-tabs').html('<span class="mon-hint">Aucun environnement configuré</span>');
    return;
  }

  const dotClass = { Listening: 'live', Unavailable: 'unavailable' };
  const html = environments.map(e => `
    <button class="mode-tab ${selectedEnvs.includes(e.env) ? 'active' : ''}" data-env="${escapeHtml(e.env)}" title="${escapeHtml(e.status)}">
      <span class="conn-dot ${dotClass[e.status] || 'reconnecting'}"></span>${escapeHtml(e.env)}
    </button>`).join('');
  $('#env-tabs').html(html);
}

function renderPeriodButtons() {
  $('#range-tabs .mode-tab').each(function () {
    $(this).toggleClass('active', $(this).data('range') === period);
  });
  $('#custom-range').toggleClass('open', period === 'custom');
}

function renderKpi() {
  TRACKED_STATES.forEach(state => $('#stat-' + state).text(data.kpi[state]));
}

function renderChart() {
  // On dessine à la largeur réelle du bloc, pour garder des textes lisibles.
  const width = Math.max(320, Math.round($('#chart').width() || 1000));
  $('#chart').html(buildChartSvg(data, width, 240));
}

function renderLegend() {
  const html = TRACKED_STATES
    .map(state => `<span class="s-wrap s-${state}"><span class="s-dot"></span>${STATE_LABELS[state]}</span>`)
    .join('');
  $('#chart-legend').html(html);
}

function renderJobs() {
  $('#jobs-count').text(data.jobs.length);

  if (data.jobs.length === 0) {
    $('#jobs-tbody').html('<tr class="loading-row"><td colspan="7">Aucun job sur la période</td></tr>');
    return;
  }

  const html = data.jobs.map(job => {
    const progress = Math.max(0, Math.min(100, Number(job.progress) || 0));
    const taskCount = job.taskCount === null || job.taskCount === undefined ? '—' : escapeHtml(job.taskCount);
    return `<tr>
      <td class="td-env">${escapeHtml(job.env)}</td>
      <td class="td-id">#${escapeHtml(job.id)}</td>
      <td class="td-name" title="${escapeHtml(job.name)}">${escapeHtml(job.name) || '—'}</td>
      <td>${stateBadge(job.state)}</td>
      <td>
        <div class="mon-progress">
          <div class="mon-progress-track"><div class="mon-progress-fill" style="width:${progress}%"></div></div>
          <span class="mon-progress-value">${progress}%</span>
        </div>
      </td>
      <td class="td-id">${taskCount}</td>
      <td class="td-date">${formatDate(job.updatedAt)}</td>
    </tr>`;
  }).join('');
  $('#jobs-tbody').html(html);
}

function renderTimeline() {
  if (timeline.length === 0) {
    $('#timeline-body').html('<div class="empty-row">En attente d\'événements…</div>');
    return;
  }

  const html = timeline.map(entry => {
    const reason = entry.reason ? ` <span class="mon-tl-reason">${escapeHtml(entry.reason)}</span>` : '';
    return `<div class="mon-tl-row">
      <span class="mon-tl-time">${new Date(entry.at).toLocaleTimeString('fr-FR')}</span>
      <span class="mon-tl-env">[${escapeHtml(entry.env)}]</span>
      <span class="mon-tl-text">${escapeHtml(entry.text)}${reason}</span>
      ${stateBadge(entry.state)}
    </div>`;
  }).join('');
  $('#timeline-body').html(html);
}

function showMessage(text) {
  $('#mon-message').text(text).addClass('visible');
}

function showSnapshotErrors(errors) {
  if (errors.length === 0) {
    $('#mon-message').removeClass('visible').text('');
    return;
  }
  showMessage(errors.map(e => e.env + ' : ' + e.message).join(' · '));
}


/* ═══════════════════════════════════════════════════════════════════════
   5. ACTIONS DE L'UTILISATEUR
═══════════════════════════════════════════════════════════════════════ */

/** Choix mémorisés dans le navigateur (facultatif : la page marche sans). */
function readSavedChoices() {
  try { return JSON.parse(localStorage.getItem(STORAGE_KEY)) || {}; } catch (e) { return {}; }
}

function saveChoices() {
  try { localStorage.setItem(STORAGE_KEY, JSON.stringify({ envs: selectedEnvs, period })); } catch (e) { /* stockage indisponible */ }
}

function onEnvironmentClick() {
  const code = String($(this).data('env'));
  if (selectedEnvs.includes(code)) {
    selectedEnvs = selectedEnvs.filter(c => c !== code);
  } else {
    selectedEnvs.push(code);
  }
  saveChoices();
  renderEnvironmentButtons();
  loadSnapshot();
}

function onPeriodClick() {
  period = String($(this).data('range'));
  if (period === 'custom') fillCustomRangeIfEmpty();
  saveChoices();
  renderPeriodButtons();
  $('#mode-label').text(isLivePeriod(period) ? 'En direct' : 'Vue figée');

  // La période personnalisée se charge avec le bouton "Appliquer".
  if (period !== 'custom') loadSnapshot();
}

function fillCustomRangeIfEmpty() {
  if ($('#range-from').val() && $('#range-to').val()) return;
  const range = periodRange(DEFAULT_PERIOD, Date.now());
  $('#range-from').val(toInputValue(range.from));
  $('#range-to').val(toInputValue(range.to));
}

/** Format attendu par <input type="datetime-local"> : 2026-10-28T16:52 */
function toInputValue(time) {
  const d = new Date(time);
  const two = n => String(n).padStart(2, '0');
  return d.getFullYear() + '-' + two(d.getMonth() + 1) + '-' + two(d.getDate()) + 'T' + two(d.getHours()) + ':' + two(d.getMinutes());
}

function updateClock() {
  $('#clock').text(new Date().toLocaleTimeString('fr-FR'));
}

function startPage() {
  // On rouvre toujours en direct : une période personnalisée n'est pas restaurée.
  const saved = readSavedChoices();
  period = PERIODS[saved.period] ? saved.period : DEFAULT_PERIOD;

  $('#env-tabs').on('click', '.mode-tab', onEnvironmentClick);
  $('#range-tabs').on('click', '.mode-tab', onPeriodClick);
  $('#range-apply').on('click', loadSnapshot);
  $('#btnHelp').on('click', () => $('#helpOverlay').addClass('open'));
  $(window).on('resize', renderChart);

  renderPeriodButtons();
  renderLegend();
  renderAll();

  updateClock();
  setInterval(updateClock, 1000); // horloge du pied de page uniquement, aucune donnée

  connectToHub();
}

// Démarrage dans le navigateur
if (typeof window !== 'undefined' && window.jQuery) {
  $(startPage);
}

// Utilisé uniquement par les tests (Node), sans effet dans le navigateur.
if (typeof module !== 'undefined') {
  module.exports = {
    emptyData,
    dataFromSnapshot,
    applyJobChanged,
    addPointToChart,
    jobTimelineEntry,
    taskTimelineEntry,
    periodRange,
    isLivePeriod,
    chartTicks,
    buildChartSvg,
    MAX_JOBS,
  };
}
