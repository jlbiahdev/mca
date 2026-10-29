// Tests des fonctions "données" de la page STYX Monitoring.
// Lancement : node --test tests/frontend/monitoring.test.js (aucune dépendance)
const test = require('node:test');
const assert = require('node:assert/strict');
const page = require('../../frontend/pages/monitoring/monitoring.js');

const START = Date.parse('2026-10-28T10:00:00Z');

function sampleSnapshot() {
  return {
    generatedAt: '2026-10-28T11:00:00Z',
    from: '2026-10-28T10:00:00Z',
    to: '2026-10-28T11:00:00Z',
    bucketSeconds: 60,
    kpi: { Submitted: 1, Running: 2, Finished: 3, Failed: 0, Canceled: 0 },
    series: [
      { state: 'Submitted', counts: [1, 0] },
      { state: 'Running', counts: [0, 2] },
      { state: 'Finished', counts: [3, 0] },
      { state: 'Failed', counts: [0, 0] },
      { state: 'Canceled', counts: [0, 0] },
    ],
    jobs: [
      { env: 'LOCAL', id: 1024, name: 'ALM_RUN', state: 'Running', progress: 40, taskCount: 24, updatedAt: '2026-10-28T10:30:00Z' },
    ],
    errors: [],
  };
}

function jobMessage(changes) {
  return Object.assign({
    env: 'LOCAL', jobId: 1024, name: null, state: 'Finished', previousState: 'Running',
    progress: 100, receivedAt: '2026-10-28T10:01:30Z',
  }, changes);
}

test('le snapshot remplit KPI, courbes et tableau', () => {
  const data = page.dataFromSnapshot(sampleSnapshot());
  assert.equal(data.kpi.Running, 2);
  assert.deepEqual(data.series.Running, [0, 2]);
  assert.equal(data.jobs.length, 1);
});

test('un changement d\'état fait passer le job d\'un compteur à l\'autre', () => {
  const data = page.dataFromSnapshot(sampleSnapshot());
  const stateChanged = page.applyJobChanged(data, jobMessage());
  assert.equal(stateChanged, true);
  assert.equal(data.kpi.Running, 1);
  assert.equal(data.kpi.Finished, 4);
  assert.equal(data.jobs[0].state, 'Finished');
});

test('une simple progression ne change ni les KPI ni le graphique', () => {
  const data = page.dataFromSnapshot(sampleSnapshot());
  const stateChanged = page.applyJobChanged(data, jobMessage({ state: 'Running', previousState: 'Running', progress: 65 }));
  assert.equal(stateChanged, false);
  assert.equal(data.kpi.Running, 2);
  assert.deepEqual(data.series.Running, [0, 2]);
  assert.equal(data.jobs[0].progress, 65);
});

test('un événement déjà compté dans le snapshot n\'est pas compté deux fois', () => {
  const data = page.dataFromSnapshot(sampleSnapshot());
  // Le job est déjà "Running" dans le tableau.
  page.applyJobChanged(data, jobMessage({ state: 'Running', previousState: 'Submitted' }));
  assert.equal(data.kpi.Submitted, 1);
  assert.equal(data.kpi.Running, 2);
});

test('un nouveau job arrive en tête du tableau', () => {
  const data = page.dataFromSnapshot(sampleSnapshot());
  page.applyJobChanged(data, jobMessage({ jobId: 2000, name: 'ORSA_RUN', state: 'Submitted', previousState: null }));
  assert.equal(data.jobs[0].id, 2000);
  assert.equal(data.jobs[0].taskCount, null);
  assert.equal(data.kpi.Submitted, 2);
});

test('le tableau ne dépasse jamais le nombre maximal de jobs', () => {
  const data = page.dataFromSnapshot(sampleSnapshot());
  for (let id = 1; id <= page.MAX_JOBS + 5; id++) {
    page.applyJobChanged(data, jobMessage({ jobId: id, state: 'Submitted', previousState: null }));
  }
  assert.equal(data.jobs.length, page.MAX_JOBS);
});

test('les courbes s\'allongent quand le temps dépasse la période', () => {
  const data = page.dataFromSnapshot(sampleSnapshot());
  page.addPointToChart(data, 'Failed', START + 5 * 60 * 1000);
  assert.equal(data.series.Failed.length, 6);
  assert.equal(data.series.Failed[5], 1);
  assert.equal(data.series.Submitted.length, 6);
});

test('seules les tâches en échec vont dans la timeline', () => {
  assert.equal(page.taskTimelineEntry({ state: 'Finished', env: 'POC', taskId: 1 }), null);

  const entry = page.taskTimelineEntry({ state: 'Failed', env: 'POC', taskId: 1, externalId: 77, jobId: 987, reason: 'exit 1', receivedAt: 'x' });
  assert.equal(entry.text, 'Tâche 77 · Job 987');
  assert.equal(entry.reason, 'exit 1');
});

test('1 h / 24 h / 7 j sont en direct, la période personnalisée est figée', () => {
  assert.equal(page.isLivePeriod('24h'), true);
  assert.equal(page.isLivePeriod('custom'), false);
  assert.deepEqual(page.periodRange('1h', 3600000), { from: 0, to: 3600000 });
  assert.deepEqual(page.periodRange('custom', 0, 10, 20), { from: 10, to: 20 });
});

test('les graduations de l\'axe vertical sont des entiers', () => {
  assert.deepEqual(page.chartTicks(0), [0, 1, 2, 3, 4]);
  assert.deepEqual(page.chartTicks(10), [0, 3, 6, 9, 12]);
});

test('le graphique contient une courbe par état', () => {
  const svg = page.buildChartSvg(page.dataFromSnapshot(sampleSnapshot()), 1000, 240);
  ['Submitted', 'Running', 'Finished', 'Failed', 'Canceled'].forEach(state => {
    assert.match(svg, new RegExp('series-' + state));
  });
});

// ── Filtres du tableau ───────────────────────────────────────────────────

test('filtre ID : égalité exacte', () => {
  assert.equal(page.matchesFilters({ id: 1024, name: 'ALM_RUN' }, { id: 1024, name: '' }), true);
  assert.equal(page.matchesFilters({ id: 10245, name: 'ALM_RUN' }, { id: 1024, name: '' }), false);
});

test('filtre nom : contient le texte, sans tenir compte des majuscules', () => {
  assert.equal(page.matchesFilters({ id: 1, name: 'ALM_RUN_202610' }, { id: null, name: 'run' }), true);
  assert.equal(page.matchesFilters({ id: 1, name: 'ORSA_RUN' }, { id: null, name: 'alm' }), false);
  assert.equal(page.matchesFilters({ id: 1, name: null }, { id: null, name: 'alm' }), false);
});

test('sans filtre, tous les jobs passent', () => {
  assert.equal(page.matchesFilters({ id: 1, name: null }, { id: null, name: '' }), true);
});

test('un nouveau job hors filtre compte dans les KPI mais n\'entre pas dans le tableau', () => {
  const data = page.dataFromSnapshot(sampleSnapshot());
  const filters = { id: null, name: 'ALM' };
  page.applyJobChanged(data, jobMessage({ jobId: 2000, name: 'ORSA_RUN', state: 'Submitted', previousState: null }), filters);
  assert.equal(data.kpi.Submitted, 2);
  assert.equal(data.jobs.some(j => j.id === 2000), false);
});

test('un job déjà affiché reste mis à jour même avec un filtre', () => {
  const data = page.dataFromSnapshot(sampleSnapshot());
  page.applyJobChanged(data, jobMessage({ progress: 80, state: 'Running', previousState: 'Running' }), { id: 1024, name: '' });
  assert.equal(data.jobs[0].progress, 80);
});

// ── Détail d'un job ──────────────────────────────────────────────────────

test('l\'état d\'une tâche du détail est mis à jour par son external_id', () => {
  const tasks = [{ externalId: 77, state: 'Running' }, { externalId: 78, state: 'Running' }];
  assert.equal(page.applyTaskChanged(tasks, { externalId: 78, state: 'Failed' }), true);
  assert.equal(tasks[1].state, 'Failed');
  assert.equal(tasks[0].state, 'Running');
});

test('une tâche inconnue ou sans external_id ne change rien', () => {
  const tasks = [{ externalId: 77, state: 'Running' }];
  assert.equal(page.applyTaskChanged(tasks, { externalId: 99, state: 'Failed' }), false);
  assert.equal(page.applyTaskChanged(tasks, { externalId: null, state: 'Failed' }), false);
  assert.equal(tasks[0].state, 'Running');
});
