// Draws the query form from its model and turns each edit into a model change. Every choice it offers comes from the
// source's querySchema, through query.js; DataGateway judges the values typed in.
import * as model from './query.js';
import { element, failure } from './view.js';

const labels = {
  eq: 'equals', neq: 'does not equal', in: 'is one of', not_in: 'is none of', is_null: 'is null',
  is_not_null: 'is not null', lt: 'is less than', lte: 'is at most', gt: 'is greater than', gte: 'is at least',
  between: 'is between', contains: 'contains', like: 'is like'
};
const inputTypes = { number: 'number', date: 'date' };

/**
 * The form's fieldsets for a described source. marks holds a message per field key, and values each condition's open
 * values list by condition id. actions.changed redraws the form after an edit, actions.typed follows typing without a
 * redraw, and actions.showValues and actions.closeValues open and close a condition's values list.
 */
export function queryForm(described, form, marks, values, actions) {
  const edit = change => (...args) => { change(...args); actions.changed(); };
  const draw = { described, form, marks, values, actions, edit };
  if (model.modes(described).length === 0)
    return [element('p', { class: 'hint' }, 'This source has no column that can be selected, grouped or aggregated.')];
  const kind = form.mode === 'rows' ? [columns(draw)] : [groupBy(draw), aggregates(draw)];
  return [mode(draw), ...kind, filters(draw), sort(draw), limit(draw)].filter(part => part !== null);
}

function mode(draw) {
  const available = model.modes(draw.described);
  if (available.length < 2) return null;
  const names = { rows: 'Rows', summary: 'Summary' };
  const radios = available.map(value => {
    const radio = element('input', { type: 'radio', name: 'mode', value, 'data-key': `mode.${value}`, checked: draw.form.mode === value });
    radio.addEventListener('change', draw.edit(() => { draw.form.mode = value; }));
    return element('label', {}, radio, ` ${names[value]}`);
  });
  return fieldset('mode', 'Show', element('div', { class: 'choices' }, ...radios),
    element('p', { class: 'hint' }, 'Rows lists the rows that match. Summary groups them and aggregates columns.'));
}

function columns(draw) {
  const names = model.selectable(draw.described).map(column => column.name);
  const rows = draw.form.rows;
  return fieldset('select', 'Columns',
    checkboxes(draw, 'select', names, rows.select, (name, on) => model.toggle(rows.select, name, on)),
    element('p', { class: 'row-actions' },
      button('All', draw.edit(() => { rows.select = [...names]; }), { 'data-key': 'select.all' }),
      button('None', draw.edit(() => { rows.select = []; }), { 'data-key': 'select.none' })),
    ...notes(draw, key => key === 'select' || key.startsWith('select.')));
}

function groupBy(draw) {
  const names = model.groupable(draw.described).map(column => column.name);
  if (names.length === 0) return null;
  return fieldset('group_by', 'Group by',
    checkboxes(draw, 'group_by', names, draw.form.summary.groupBy,
      (name, on) => model.setGroupBy(draw.described, draw.form, name, on)),
    ...notes(draw, key => key === 'group_by' || key.startsWith('group_by.')));
}

function aggregates(draw) {
  const { described, form } = draw;
  const columns = model.aggregatable(described);
  if (columns.length === 0) return null;
  const lines = form.summary.aggregates.map(aggregate => {
    const key = `aggregates.${aggregate.id}`;
    const allowed = model.functions(described, model.columnNamed(described, aggregate.col));
    return line(draw, key, [
      choice(draw, `${key}.fn`, 'Function', allowed.map(rule => [rule.fn, rule.fn]), aggregate.fn,
        draw.edit(fn => model.setAggregate(described, form, aggregate, { fn }))),
      'of',
      choice(draw, `${key}.col`, 'Column', columns.map(column => [column.name, column.name]), aggregate.col,
        draw.edit(col => model.setAggregate(described, form, aggregate, { col }))),
      element('span', { class: 'output' }, `→ ${model.outputName(aggregate)}`),
      remove('Remove aggregate', draw.edit(() => model.removeAggregate(described, form, aggregate)))
    ], allowed.find(rule => rule.fn === aggregate.fn).rule ?? null);
  });
  return fieldset('aggregates', 'Aggregates', ...lines,
    element('p', { class: 'row-actions' },
      button('+ Aggregate', draw.edit(() => model.addAggregate(described, form)), { 'data-key': 'aggregates.add' })),
    ...notes(draw, key => key === 'aggregates'));
}

function filters(draw) {
  if (model.filterable(draw.described).length === 0) return null;
  return fieldset('filters', 'Filters', group(draw, draw.form.filters), ...notes(draw, key => key === 'filters'));
}

function group(draw, node) {
  const { described, form } = draw;
  const root = node === form.filters;
  const key = `filters.${node.id}`;
  const conditionsLeft = model.canAddCondition(described, form);
  const adds = element('p', { class: 'row-actions' },
    button('+ Condition', draw.edit(() => model.addCondition(described, form, node)),
      { 'data-key': `${key}.add-condition`, disabled: !conditionsLeft }),
    model.groupsAllowed(described)
      ? button('+ Group', draw.edit(() => model.addGroup(described, form, node)),
        { 'data-key': `${key}.add-group`, disabled: !model.canAddGroup(described, form, node) })
      : null,
    conditionsLeft ? null : element('span', { class: 'hint' },
      `The source allows ${described.querySchema.filters.maxConditions} conditions.`));
  if (root && node.items.length === 0)
    return element('div', { class: 'group root', 'data-key': key },
      element('p', { class: 'hint' }, 'No conditions: every row is included.'), adds);
  const match = model.groupsAllowed(described)
    ? choice(draw, `${key}.match`, 'Match', [['all', 'all'], ['any', 'any', root && !model.canMatchAny(described, form)]],
      node.match, draw.edit(value => { node.match = value; }))
    : 'all';
  const head = element('div', { class: 'row' }, root ? 'Rows must match' : 'Match', match, 'of these:',
    root ? null : remove('Remove group', draw.edit(() => model.removeFilter(form, node))));
  const items = node.items.map(item => element('li', {}, item.kind === 'group' ? group(draw, item) : condition(draw, item)));
  return element('div', { class: root ? 'group root' : 'group', 'data-key': key }, head,
    ...notes(draw, mark => mark === key || mark === `${key}.match`),
    element('ul', { class: 'items' }, ...items), adds);
}

function condition(draw, node) {
  const { described, form } = draw;
  const key = `filters.${node.id}`;
  const column = model.columnNamed(described, node.col);
  const rules = model.operators(described, column);
  const offers = model.offersValues(column, node.op);
  const panel = draw.values.get(node.id);
  const open = offers && panel?.col === node.col;
  return line(draw, key, [
    choice(draw, `${key}.col`, 'Column', model.filterable(described).map(each => [each.name, each.name]), node.col,
      draw.edit(name => model.setColumn(described, node, name))),
    choice(draw, `${key}.op`, 'Operator', rules.map(rule => [rule.op, labels[rule.op] ?? rule.op]), node.op,
      draw.edit(op => model.setOperator(described, node, op))),
    valueEditor(draw, node, column),
    offers
      ? button('Values', () => (open ? draw.actions.closeValues(node) : draw.actions.showValues(node)),
        { 'data-key': `${key}.values`, 'aria-expanded': String(open) })
      : null,
    remove('Remove condition', draw.edit(() => model.removeFilter(form, node)))
  ], `${node.op}: ${rules.find(rule => rule.op === node.op).rule}`, open ? valuesList(draw, node, column, panel) : null);
}

function valueEditor(draw, node, column) {
  const key = `filters.${node.id}.value`;
  switch (model.shapeOf(node.op)) {
    case 'none': return null;
    case 'one': return entry(draw, key, column, node.value, 'Value', text => { node.value = text; });
    case 'range': return element('span', { class: 'row' },
      entry(draw, `${key}.0`, column, node.value[0], 'From', text => { node.value[0] = text; }), 'and',
      entry(draw, `${key}.1`, column, node.value[1], 'To', text => { node.value[1] = text; }));
    default: return valueList(draw, node, column, key);
  }
}

// A list value: a removable chip per value, and an entry that adds the next one on Enter or Add.
function valueList(draw, node, column, key) {
  const pending = () => (column.dataType === 'bool' && node.draft === '' ? 'true' : node.draft);
  const add = draw.edit(() => {
    const value = pending();
    if (value !== '' && !node.value.includes(value)) node.value.push(value);
    node.draft = '';
  });
  const next = entry(draw, `${key}.next`, column, pending(), 'Next value', text => { node.draft = text; });
  next.addEventListener('keydown', event => {
    if (event.key !== 'Enter' || event.ctrlKey || event.metaKey) return;
    event.preventDefault();
    add();
  });
  const chips = node.value.map((text, index) => element('li', { class: 'chip' }, text,
    remove(`Remove ${text}`, draw.edit(() => { node.value.splice(index, 1); }))));
  return element('div', { class: 'list' }, chips.length > 0 ? element('ul', { class: 'chips' }, ...chips) : null,
    next, button('Add', add, { 'data-key': `${key}.add` }));
}

function valuesList(draw, node, column, panel) {
  const key = `filters.${node.id}.values`;
  const close = button('Close', () => draw.actions.closeValues(node), { 'data-key': `${key}.close` });
  return element('div', { class: 'values', 'aria-live': 'polite' }, ...valuesBody(draw, node, column, panel, key), close);
}

function valuesBody(draw, node, column, panel, key) {
  if (panel.status === 'reading') return [element('p', { class: 'hint' }, `Reading the values of ${column.name}…`)];
  if (panel.status === 'failed')
    return [panel.answer?.code === undefined ? element('p', { class: 'flag' }, panel.message) : failure(panel.answer)];
  const { answer, request } = panel;
  const count = `count_${column.name}`;
  const counted = answer.columns.some(each => each.name === count);
  const order = request.query.order_by?.[0].col;
  const known = answer.rows.filter(row => row[column.name] !== null);
  const picks = known.map((row, index) => {
    const value = row[column.name];
    const pick = element('button', { type: 'button', 'data-key': `${key}.${index}` }, String(value),
      counted ? element('span', { class: 'count' }, String(row[count])) : null);
    pick.addEventListener('click', draw.edit(() => model.useValue(node, value)));
    return element('li', {}, pick);
  });
  const ordered = order === count ? 'most common first' : order === column.name ? 'in order' : 'in no particular order';
  return [
    element('p', { class: 'hint' }, `${known.length} ${known.length === 1 ? 'value' : 'values'}, ${ordered}. ${answer.hint}`),
    element('ul', { class: 'value-list' }, ...picks),
    known.length < answer.rows.length ? element('p', { class: 'hint' }, 'Some rows have no value here: is null finds them.') : null
  ];
}

function sort(draw) {
  const { described, form } = draw;
  if (model.sortable(described).length === 0) return null;
  const targets = model.sortTargets(described, form);
  const sorts = form[form.mode].sort;
  const lines = sorts.map(entry => {
    const key = `sort.${entry.id}`;
    return line(draw, key, [
      choice(draw, `${key}.col`, 'Sort by', targets.map(target => [target, target]), entry.col, draw.edit(col => { entry.col = col; })),
      choice(draw, `${key}.dir`, 'Direction', [['asc', 'ascending'], ['desc', 'descending']], entry.dir,
        draw.edit(dir => { entry.dir = dir; })),
      remove('Remove sort', draw.edit(() => model.removeSort(form, entry)))
    ], null);
  });
  const hint = targets.length === 0 ? 'Group by a sortable column, or aggregate one, to sort the summary.'
    : sorts.length === 0 ? 'Without a sort, which rows come back is not defined.' : null;
  return fieldset('sort', 'Sort', ...lines, hint === null ? null : element('p', { class: 'hint' }, hint),
    element('p', { class: 'row-actions' },
      button('+ Sort', draw.edit(() => model.addSort(described, form)), { 'data-key': 'sort.add', disabled: targets.length === 0 })),
    ...notes(draw, key => key === 'sort'));
}

function limit(draw) {
  const { maximum } = draw.described.querySchema.limit;
  const input = element('input', { type: 'text', inputmode: 'numeric', value: draw.form.limit, 'data-key': 'limit',
    'aria-label': 'Limit', 'aria-invalid': invalid(draw, 'limit') });
  input.addEventListener('input', () => { draw.form.limit = input.value; draw.actions.typed(); });
  return fieldset('limit', 'Limit',
    element('div', { class: 'row' }, input, element('span', { class: 'hint' }, `rows at most. Leave it empty for up to ${maximum}.`)),
    ...notes(draw, key => key === 'limit'));
}

// Building blocks.

const fieldset = (key, legend, ...children) => element('fieldset', { 'data-key': key }, element('legend', {}, legend), ...children);

// One editable entry, such as a condition or a sort: its controls, a hint, and any message about it.
function line(draw, key, controls, hint, extra = null) {
  const messages = notes(draw, mark => mark === key || mark.startsWith(`${key}.`));
  return element('div', { class: messages.length > 0 ? 'line flagged' : 'line', 'data-key': key },
    element('div', { class: 'row' }, ...controls),
    hint === null ? null : element('p', { class: 'hint' }, hint),
    ...messages, extra);
}

function checkboxes(draw, key, names, chosen, onToggle) {
  return element('div', { class: 'choices' }, ...names.map(name => {
    const box = element('input', { type: 'checkbox', 'data-key': `${key}.${name}`, checked: chosen.includes(name),
      'aria-invalid': invalid(draw, `${key}.${name}`) });
    box.addEventListener('change', draw.edit(() => onToggle(name, box.checked)));
    return element('label', {}, box, ` ${name}`);
  }));
}

// A select of [value, text, disabled] options.
function choice(draw, key, label, options, current, onChange) {
  const node = element('select', { 'data-key': key, 'aria-label': label, 'aria-invalid': invalid(draw, key) },
    ...options.map(([value, text, disabled = false]) => element('option', { value, selected: value === current, disabled }, text)));
  node.addEventListener('change', () => onChange(node.value));
  return node;
}

// A value input that fits the column's type: a number box, a date picker, true or false, or text.
function entry(draw, key, column, text, label, onText) {
  if (column.dataType === 'bool')
    return choice(draw, key, label, [['true', 'true'], ['false', 'false']], text, value => { onText(value); draw.actions.typed(); });
  const node = element('input', { type: inputTypes[column.dataType] ?? 'text', step: column.dataType === 'number' ? 'any' : false,
    value: text, 'data-key': key, 'aria-label': label, 'aria-invalid': invalid(draw, key) });
  node.addEventListener('input', () => { onText(node.value); draw.actions.typed(); });
  return node;
}

function button(text, onClick, attributes = {}) {
  const node = element('button', { type: 'button', ...attributes }, text);
  node.addEventListener('click', onClick);
  return node;
}

const remove = (label, onClick) => button('✕', onClick, { class: 'remove', 'aria-label': label, title: label });

// A control is marked when a mark names it or a field that contains it.
const invalid = (draw, key) =>
  [...draw.marks.keys()].some(mark => key === mark || key.startsWith(`${mark}.`)) ? 'true' : false;

const notes = (draw, belongs) =>
  [...draw.marks].filter(([key]) => belongs(key)).map(([, message]) => element('p', { class: 'flag' }, message));
