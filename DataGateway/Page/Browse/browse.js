// Wires the page: list the sources, describe the chosen one, then build its query in the form, show it, and run it.
import { describeSource, gateway, listSources, querySource } from './api.js';
import { queryForm } from './form.js';
import * as model from './query.js';
import * as view from './view.js';

const byId = id => document.getElementById(id);
const runButton = byId('run');
const status = byId('run-status');
const tabs = [byId('tab-form'), byId('tab-json')];
const ready = 'Ctrl+Enter runs the query.';

// The chosen source's describe answer, the form's state, a message per marked field, and each open values list.
const page = { described: null, form: null, marks: new Map(), values: new Map() };

const actions = {
  changed() {
    page.marks = new Map();
    status.textContent = ready;
    draw();
  },
  typed: showRequest,
  showValues,
  closeValues(condition) {
    page.values.delete(condition.id);
    draw();
  }
};

async function start() {
  byId('gateway-name').textContent = gateway.name;
  document.title = `${gateway.name} browse`;
  if (Object.values(gateway.routes).includes(null))
    return notice(banner(`Gateway ${gateway.name} has no list, describe and query routes this page can reach. ` +
      'Map them with MapDataGateway, under route values this page has.'));
  const listed = await reach(listSources);
  if (listed === undefined) return;
  if (listed.status !== 200) return notice(failureOf(listed));
  const { sources } = listed.json;
  if (sources.length === 0)
    return notice(banner('The catalog has no active source. Register a view: README, Get started, step 4.'));
  byId('sources').replaceChildren(...view.sourceItems(sources, choose));
  await choose(sources[0].name);
}

async function choose(name) {
  const described = await reach(() => describeSource(name));
  if (described === undefined) return;
  if (described.status !== 200) return notice(failureOf(described));
  const { source, querySchema } = described.json;
  for (const button of byId('sources').querySelectorAll('button'))
    button.setAttribute('aria-current', String(button.dataset.source === source.name));
  byId('source-title').textContent = source.title;
  byId('source-description').textContent = source.description;
  byId('source-facts').textContent = view.sourceFacts(source);
  byId('columns').replaceChildren(...view.columnRows(querySchema.columns));
  page.described = described.json;
  page.form = model.initialForm(page.described);
  page.values.clear();
  actions.changed();
  byId('answer').hidden = true;
  byId('workspace').hidden = false;
}

// Redraws the form, keeping focus, and the caret where there is one, on the same field.
function draw() {
  const focused = document.activeElement;
  const key = focused?.dataset?.key;
  const caret = typeof focused?.selectionStart === 'number' ? [focused.selectionStart, focused.selectionEnd] : null;
  const panel = byId('panel-form');
  panel.replaceChildren(...queryForm(page.described, page.form, page.marks, page.values, actions));
  const again = key === undefined ? null : panel.querySelector(`[data-key="${CSS.escape(key)}"]`);
  again?.focus();
  if (caret !== null && typeof again?.selectionStart === 'number') again.setSelectionRange(...caret);
  showRequest();
}

function showRequest() {
  byId('request').textContent = JSON.stringify(model.buildRequest(page.described, page.form).body, null, 2);
}

async function run() {
  if (page.form === null || runButton.disabled) return;
  if (model.modes(page.described).length === 0) {
    status.textContent = 'This source has nothing to query.';
    return;
  }
  const found = model.problems(page.described, page.form);
  if (found.size > 0) {
    page.marks = found;
    select(tabs[0]);
    draw();
    status.textContent = 'Fix the marked fields, then run again.';
    return;
  }
  const { body, fields } = model.buildRequest(page.described, page.form);
  runButton.disabled = true;
  status.textContent = 'Running…';
  try {
    const answered = await reach(() => querySource(JSON.stringify(body)));
    if (answered === undefined) return;
    // A refusal is marked on the field that wrote the part of the request its path names.
    const refused = answered.status === 200 ? undefined : answered.json;
    const field = refused?.code === undefined ? null : model.fieldFor(fields, refused.path);
    page.marks = field === null ? new Map() : new Map([[field, `${refused.code}: ${refused.message}`]]);
    byId('answer-body').replaceChildren(...(answered.status === 200 ? view.rows(answered.json) : [failureOf(answered, body)]));
    byId('raw').textContent = answered.json === undefined ? answered.text : JSON.stringify(answered.json, null, 2);
    byId('answer').hidden = false;
    draw();
  } finally {
    runButton.disabled = false;
    status.textContent = ready;
  }
}

// Reads a column's values for a condition's values list; a list closed or replaced meanwhile stays as it is.
async function showValues(condition) {
  const request = model.valuesRequest(page.described, model.columnNamed(page.described, condition.col));
  const panel = { col: condition.col, status: 'reading', request };
  page.values.set(condition.id, panel);
  draw();
  try {
    const answered = await querySource(JSON.stringify(request));
    Object.assign(panel, answered.status === 200
      ? { status: 'done', answer: answered.json }
      : { status: 'failed', answer: answered.json, message: `DataGateway answered HTTP ${answered.status} without an error body.` });
  } catch {
    Object.assign(panel, { status: 'failed', message: 'DataGateway did not answer. Check that it is running.' });
  }
  if (page.values.get(condition.id) === panel) draw();
}

// DataGateway's own error body when there is one; the root path names the whole request, so it shows nothing more.
function failureOf(answered, request) {
  const error = answered.json;
  if (error?.code === undefined) return banner(`DataGateway answered HTTP ${answered.status} without an error body.`);
  const requested = request === undefined || error.path === '$' ? undefined : model.valueAt(request, error.path);
  return view.failure(error, requested);
}

// Makes one call, or says DataGateway cannot be reached and returns undefined.
async function reach(call) {
  try {
    const answered = await call();
    byId('notice').hidden = true;
    return answered;
  } catch {
    notice(banner('DataGateway did not answer. Check that it is running, then reload the page.'));
    return undefined;
  }
}

const banner = text => view.element('p', { class: 'banner failure' }, text);

function notice(content) {
  byId('notice').replaceChildren(content);
  byId('notice').hidden = false;
}

function select(tab) {
  for (const each of tabs) {
    const chosen = each === tab;
    each.setAttribute('aria-selected', String(chosen));
    each.tabIndex = chosen ? 0 : -1;
    byId(each.getAttribute('aria-controls')).hidden = !chosen;
  }
}

for (const tab of tabs) {
  tab.addEventListener('click', () => select(tab));
  tab.addEventListener('keydown', event => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    const other = tabs[1 - tabs.indexOf(tab)];
    select(other);
    other.focus();
  });
}
runButton.addEventListener('click', run);
byId('reset').addEventListener('click', () => {
  if (page.described === null) return;
  page.form = model.initialForm(page.described);
  page.values.clear();
  actions.changed();
});
byId('query').addEventListener('keydown', event => {
  if (event.key !== 'Enter' || !(event.ctrlKey || event.metaKey)) return;
  event.preventDefault();
  run();
});
start();
