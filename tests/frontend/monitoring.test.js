// Tests du modèle de la page STYX Monitoring (aucune dépendance : node --test).
const test = require('node:test');
const assert = require('node:assert/strict');
const mon = require('../../frontend/pages/monitoring/monitoring.js');

const FROM = Date.parse('2026-10-28T10:00:00Z');

function snapshot(overrides) {
  return Object.assign({
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
  }, overrides);
}

function loaded(overrides) {
  return mon.loadSnapshotIntoModel(mon.createModel(), snapshot(overrides));
}

function jobEvent(overrides) {
  return Object.assign({
    env: 'LOCAL', jobId: 1024, name: null, state: 'Finished', previousState: 'Running',
    progress: 100, at: null, receivedAt: '2026-10-28T10:01:30Z',
  }, overrides);
}

test('le snapshot initialise KPI, séries et tableau', () => {
  const model = loaded();
  assert.equal(model.kpi.Running, 2);
  assert.deepEqual(model.series.Running, [0, 2]);
  assert.equal(model.jobs.length, 1);
  assert.equal(model.bucketSeconds, 60);
});

test('un changement d\'état déplace le job d\'un compteur à l\'autre', () => {
  const model = loaded();
  const result = mon.applyJobChanged(model, jobEvent(), Date.parse('2026-10-28T10:01:30Z'));
  assert.equal(result.stateChanged, true);
  assert.equal(model.kpi.Running, 1);
  assert.equal(model.kpi.Finished, 4);
  assert.equal(model.jobs[0].state, 'Finished');
  assert.equal(model.jobs[0].progress, 100);
});

test('une progression seule ne modifie ni KPI ni graphique', () => {
  const model = loaded();
  const result = mon.applyJobChanged(model, jobEvent({ state: 'Running', previousState: 'Running', progress: 65 }), FROM);
  assert.equal(result.stateChanged, false);
  assert.equal(model.kpi.Running, 2);
  assert.deepEqual(model.series.Running, [0, 2]);
  assert.equal(model.jobs[0].progress, 65);
});

test('un événement déjà reflété par le snapshot n\'est pas compté deux fois', () => {
  const model = loaded();
  // La ligne est déjà "Running" : l'événement Submitted → Running ne change rien.
  const result = mon.applyJobChanged(model, jobEvent({ state: 'Running', previousState: 'Submitted' }), FROM);
  assert.equal(result.stateChanged, false);
  assert.equal(model.kpi.Submitted, 1);
  assert.equal(model.kpi.Running, 2);
});

test('un nouveau job est inséré en tête et la liste reste bornée', () => {
  const model = loaded();
  model.jobLimit = 1;
  mon.applyJobChanged(model, jobEvent({ jobId: 2000, name: 'ORSA_RUN', state: 'Submitted', previousState: null }), FROM);
  assert.equal(model.jobs.length, 1);
  assert.equal(model.jobs[0].id, 2000);
  assert.equal(model.jobs[0].taskCount, null);
  assert.equal(model.kpi.Submitted, 2);
});

test('les séries se prolongent quand le temps dépasse la plage', () => {
  const model = loaded();
  mon.addToSeries(model, 'Failed', FROM + 5 * 60 * 1000);
  assert.equal(model.series.Failed.length, 6);
  assert.equal(model.series.Failed[5], 1);
  assert.equal(model.series.Submitted.length, 6);
});

test('un état non suivi n\'alimente pas le graphique', () => {
  const model = loaded();
  mon.addToSeries(model, 'Queued', FROM);
  assert.equal(model.series.Queued, undefined);
});

test('seules les tâches en échec vont dans la timeline', () => {
  assert.equal(mon.taskTimelineEntry({ state: 'Finished', env: 'POC', taskId: 1 }), null);
  const entry = mon.taskTimelineEntry({ state: 'Failed', env: 'POC', taskId: 1, externalId: 77, jobId: 987, reason: 'exit 1', receivedAt: 'x' });
  assert.equal(entry.text, 'Tâche 77 · Job 987');
  assert.equal(entry.reason, 'exit 1');
});

test('les préréglages sont en direct, la période personnalisée est figée', () => {
  assert.equal(mon.isLivePreset('24h'), true);
  assert.equal(mon.isLivePreset('custom'), false);
  assert.deepEqual(mon.computeRange('1h', 3600e3), { from: 0, to: 3600e3 });
  assert.deepEqual(mon.computeRange('custom', 0, 10, 20), { from: 10, to: 20 });
});

test('les graduations de l\'axe Y sont entières', () => {
  assert.deepEqual(mon.chartTicks(0), [0, 1, 2, 3, 4]);
  assert.deepEqual(mon.chartTicks(10), [0, 3, 6, 9, 12]);
});

test('le graphique produit une courbe par état suivi', () => {
  const svg = mon.buildChartSvg(loaded(), 1000, 240);
  ['Submitted', 'Running', 'Finished', 'Failed', 'Canceled'].forEach(state => {
    assert.match(svg, new RegExp(`series-${state}`));
  });
});
