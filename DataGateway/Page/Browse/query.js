// The query form's model: its state, the choices the source's querySchema allows, and the request it builds.
// No page and no network, so tests run it under Node. DataGateway still checks every request the form sends.

const shapes = { is_null: 'none', is_not_null: 'none', in: 'list', not_in: 'list', between: 'range' };
const exactMatches = ['eq', 'neq', 'in', 'not_in'];

/** How an operator takes its value: none, one, a list, or a range [low, high]. */
export const shapeOf = op => shapes[op] ?? 'one';

/** The parsed text, or undefined when it is not JSON. */
export function parseJson(text) {
  try { return JSON.parse(text); } catch { return undefined; }
}

// What the source allows, read from its describe answer.

const withFlag = flag => described => described.querySchema.columns.filter(column => column[flag]);
export const selectable = withFlag('canSelect');
export const filterable = withFlag('canFilter');
export const sortable = withFlag('canSort');
export const groupable = withFlag('canGroup');
export const aggregatable = withFlag('canAggregate');
export const columnNamed = (described, name) => described.querySchema.columns.find(column => column.name === name);
const inCatalogOrder = (described, names) =>
  described.querySchema.columns.map(column => column.name).filter(name => names.includes(name));

/** The filter operators for a column's type, with their rules, as querySchema lists them. */
export const operators = (described, column) =>
  (described.querySchema.filters?.operators ?? []).filter(rule => rule.types.includes(column.dataType));

/** The aggregate functions for a column's type, with their rules, as querySchema lists them. */
export const functions = (described, column) =>
  (described.querySchema.aggregates?.functions ?? []).filter(rule => rule.types.includes(column.dataType));

/** The kinds of query the source allows: listing rows, summarizing them, or both. */
export function modes(described) {
  const { select, group_by: groupBy, aggregates } = described.querySchema;
  return [...(select ? ['rows'] : []), ...(groupBy || aggregates ? ['summary'] : [])];
}

export const outputName = aggregate => `${aggregate.fn}_${aggregate.col}`;

/** What the form can sort by: sortable columns for rows; sortable group columns and their aggregates for a summary. */
export function sortTargets(described, form) {
  if (form.mode === 'rows') return sortable(described).map(column => column.name);
  const sorts = name => columnNamed(described, name).canSort;
  return [...inCatalogOrder(described, form.summary.groupBy).filter(sorts),
    ...form.summary.aggregates.filter(aggregate => sorts(aggregate.col)).map(outputName)];
}

/** Whether a column's values list helps a condition: the column can be grouped and the operator matches exact values. */
export const offersValues = (column, op) => column.canGroup && exactMatches.includes(op);

const maxDepth = described => described.querySchema.filters?.maxGroupDepth ?? 0;

/** Whether the source allows and/or groups at all. */
export const groupsAllowed = described => maxDepth(described) > 0;

/** The number of conditions in a group, counting those in the groups inside it. */
export const conditionCount = group =>
  group.items.reduce((count, item) => count + (item.kind === 'group' ? conditionCount(item) : 1), 0);

// How deep each group's and/or object sits in the request; the root writes one only when it matches any.
function depths(form) {
  const found = new Map();
  const visit = (group, depth) => {
    found.set(group, depth);
    for (const item of group.items) if (item.kind === 'group') visit(item, depth + 1);
  };
  visit(form.filters, form.filters.match === 'any' ? 1 : 0);
  return found;
}

export const groupDepth = (form, group) => depths(form).get(group);

export const canAddCondition = (described, form) =>
  conditionCount(form.filters) < (described.querySchema.filters?.maxConditions ?? 0);

/** Whether a group can hold another group, which starts with a condition of its own. */
export const canAddGroup = (described, form, group) =>
  groupDepth(form, group) < maxDepth(described) && canAddCondition(described, form);

/** Whether the root may match any: that puts every condition one group deeper. */
export const canMatchAny = (described, form) =>
  form.filters.match === 'any' || Math.max(...depths(form).values()) < maxDepth(described);

// The form's state and its edits. Conditions and groups form a tree under form.filters; every entry has an id that
// names its fields. Values stay as typed, as text, until the request is built.

/** The starting state: every selectable column, up to 10 rows, no filters and no sort. */
export function initialForm(described) {
  return {
    mode: modes(described)[0] ?? 'rows',
    rows: { select: selectable(described).map(column => column.name), sort: [] },
    summary: { groupBy: [], aggregates: [], sort: [] },
    filters: { id: 1, kind: 'group', match: 'all', items: [] },
    limit: String(Math.min(10, described.querySchema.limit.maximum)),
    next: 2
  };
}

/** Adds a condition on the first filterable column, with its first operator. */
export function addCondition(described, form, group) {
  const column = filterable(described)[0];
  const op = operators(described, column)[0].op;
  group.items.push({ id: form.next++, kind: 'condition', col: column.name, op, value: emptyValue(column, op), draft: '' });
}

/** Adds a group holding one condition. It matches the other way from its parent, the usual reason to group. */
export function addGroup(described, form, group) {
  const added = { id: form.next++, kind: 'group', match: group.match === 'all' ? 'any' : 'all', items: [] };
  addCondition(described, form, added);
  group.items.push(added);
}

/** Removes a condition or group; a group left empty goes too, as and/or groups need at least one item. */
export function removeFilter(form, node) {
  const prune = group => {
    group.items = group.items.filter(item => item !== node);
    for (const item of group.items) if (item.kind === 'group') prune(item);
    group.items = group.items.filter(item => item.kind === 'condition' || item.items.length > 0);
  };
  prune(form.filters);
}

/** Moves a condition to another column, keeping its operator where that column's type allows it. */
export function setColumn(described, condition, name) {
  const column = columnNamed(described, name);
  const allowed = operators(described, column).map(rule => rule.op);
  condition.col = name;
  if (!allowed.includes(condition.op)) condition.op = allowed[0];
  condition.value = emptyValue(column, condition.op);
  condition.draft = '';
}

/** Changes a condition's operator, carrying its values across: one value becomes a list of one, and so on. */
export function setOperator(described, condition, op) {
  const kept = { none: [], one: [condition.value], list: condition.value, range: condition.value }[shapeOf(condition.op)]
    .filter(text => text !== '');
  const column = columnNamed(described, condition.col);
  condition.op = op;
  switch (shapeOf(op)) {
    case 'none': condition.value = null; break;
    case 'list': condition.value = kept; break;
    case 'range': condition.value = [kept[0] ?? '', kept[1] ?? '']; break;
    default: condition.value = kept[0] ?? emptyValue(column, op);
  }
}

function emptyValue(column, op) {
  switch (shapeOf(op)) {
    case 'none': return null;
    case 'list': return [];
    case 'range': return ['', ''];
    default: return column.dataType === 'bool' ? 'true' : '';
  }
}

/** Puts a value from the column's values list into a condition: as its one value, or added to its list. */
export function useValue(condition, value) {
  const text = String(value);
  if (shapeOf(condition.op) !== 'list') condition.value = text;
  else if (!condition.value.includes(text)) condition.value.push(text);
}

/** Adds a name to a list, or takes it out. */
export function toggle(names, name, on) {
  const at = names.indexOf(name);
  if (on && at < 0) names.push(name);
  if (!on && at >= 0) names.splice(at, 1);
}

/** Groups by a column, or stops; a sort on that column goes when it stops. */
export function setGroupBy(described, form, name, on) {
  toggle(form.summary.groupBy, name, on);
  dropStaleSorts(described, form);
}

/** Adds an aggregate of the first aggregatable column, with its first function. */
export function addAggregate(described, form) {
  const column = aggregatable(described)[0];
  form.summary.aggregates.push({ id: form.next++, fn: functions(described, column)[0].fn, col: column.name });
}

/** Changes an aggregate's function or column. A sort on its output follows the new name. */
export function setAggregate(described, form, aggregate, change) {
  const before = outputName(aggregate);
  Object.assign(aggregate, change);
  const allowed = functions(described, columnNamed(described, aggregate.col)).map(rule => rule.fn);
  if (!allowed.includes(aggregate.fn)) aggregate.fn = allowed[0];
  for (const sort of form.summary.sort) if (sort.col === before) sort.col = outputName(aggregate);
  dropStaleSorts(described, form);
}

export function removeAggregate(described, form, aggregate) {
  form.summary.aggregates = form.summary.aggregates.filter(item => item !== aggregate);
  dropStaleSorts(described, form);
}

function dropStaleSorts(described, form) {
  const targets = sortTargets(described, { ...form, mode: 'summary' });
  form.summary.sort = form.summary.sort.filter(sort => targets.includes(sort.col));
}

/** Adds a sort on the first target not already sorted by. */
export function addSort(described, form) {
  const targets = sortTargets(described, form);
  const sorts = form[form.mode].sort;
  const col = targets.find(target => !sorts.some(sort => sort.col === target)) ?? targets[0];
  sorts.push({ id: form.next++, col, dir: 'asc' });
}

export function removeSort(form, sort) {
  form[form.mode].sort = form[form.mode].sort.filter(item => item !== sort);
}

// The request.

/** The body the form sends to POST /query, and the form field that wrote each JSON path in it. */
export function buildRequest(described, form) {
  const fields = new Map();
  const query = {};
  if (form.mode === 'rows') {
    query.select = inCatalogOrder(described, form.rows.select);
    record(fields, '$.query.select', 'select', query.select.map(name => `select.${name}`));
  } else {
    const { groupBy, aggregates } = form.summary;
    if (groupBy.length > 0) {
      query.group_by = inCatalogOrder(described, groupBy);
      record(fields, '$.query.group_by', 'group_by', query.group_by.map(name => `group_by.${name}`));
    }
    if (aggregates.length > 0) {
      query.aggregates = aggregates.map(({ fn, col }) => ({ fn, col }));
      record(fields, '$.query.aggregates', 'aggregates', aggregates.map(aggregate => `aggregates.${aggregate.id}`), ['fn', 'col']);
    }
  }
  const root = form.filters;
  if (root.items.length > 0) {
    fields.set('$.query.filters', 'filters');
    query.filters = root.match === 'all'
      ? root.items.map((item, index) => filter(described, item, `$.query.filters[${index}]`, fields))
      : [filter(described, root, '$.query.filters[0]', fields)];
  }
  const sorts = form[form.mode].sort;
  if (sorts.length > 0) {
    query.order_by = sorts.map(({ col, dir }) => ({ col, dir }));
    record(fields, '$.query.order_by', 'sort', sorts.map(sort => `sort.${sort.id}`), ['col', 'dir']);
  }
  if (form.limit.trim() !== '') {
    query.limit = Number(form.limit);
    fields.set('$.query.limit', 'limit');
  }
  return { body: { source: described.source.name, query }, fields };
}

// Records the path of a list clause, of each entry in it, and of each entry's named parts.
function record(fields, path, key, keys, parts = []) {
  fields.set(path, key);
  keys.forEach((entry, index) => {
    fields.set(`${path}[${index}]`, entry);
    for (const part of parts) fields.set(`${path}[${index}].${part}`, `${entry}.${part}`);
  });
}

function filter(described, node, path, fields) {
  fields.set(path, `filters.${node.id}`);
  if (node.kind === 'group') {
    const member = node.match === 'all' ? 'and' : 'or';
    return { [member]: node.items.map((item, index) => filter(described, item, `${path}.${member}[${index}]`, fields)) };
  }
  for (const part of ['col', 'op', 'value']) fields.set(`${path}.${part}`, `filters.${node.id}.${part}`);
  const shape = shapeOf(node.op);
  if (shape === 'none') return { col: node.col, op: node.op };
  const type = columnNamed(described, node.col).dataType;
  const value = shape === 'one' ? typed(type, node.value) : node.value.map(text => typed(type, text));
  return { col: node.col, op: node.op, value };
}

// A typed-in value as JSON: a number as a number, a bool as true or false, text and dates (yyyy-MM-dd) as written.
function typed(type, text) {
  if (type === 'bool') return text === 'true';
  if (type !== 'number') return text;
  const number = text.trim() === '' ? NaN : Number(text);
  return Number.isFinite(number) ? number : null;
}

/**
 * What must change before the form's request is worth sending, as a message per field: a choice left empty, a number
 * or date not given, and a limit outside the source's range. Everything else is DataGateway's to judge.
 */
export function problems(described, form) {
  const found = new Map();
  if (form.mode === 'rows' && form.rows.select.length === 0) found.set('select', 'Choose at least one column.');
  if (form.mode === 'summary' && form.summary.groupBy.length === 0 && form.summary.aggregates.length === 0)
    found.set(groupable(described).length > 0 ? 'group_by' : 'aggregates', 'Group by a column or add an aggregate.');
  const maxIn = described.querySchema.filters?.maxInValues;
  const visit = group => {
    for (const item of group.items) {
      if (item.kind === 'group') visit(item);
      else {
        const message = valueProblem(columnNamed(described, item.col).dataType, item, maxIn);
        if (message !== null) found.set(`filters.${item.id}.value`, message);
      }
    }
  };
  visit(form.filters);
  const { minimum, maximum } = described.querySchema.limit;
  const limit = form.limit.trim();
  if (limit !== '' && !(/^\d+$/.test(limit) && Number(limit) >= minimum && Number(limit) <= maximum))
    found.set('limit', `Enter a whole number from ${minimum} to ${maximum}, or leave it empty for up to ${maximum} rows.`);
  return found;
}

function valueProblem(type, condition, maxIn) {
  const missing = text => (type === 'number' || type === 'date') && text.trim() === '';
  const named = type === 'number' ? 'a number' : 'a date';
  switch (shapeOf(condition.op)) {
    case 'one': return missing(condition.value) ? `Enter ${named}.` : null;
    case 'range': return condition.value.some(missing) ? `Enter ${named} at both ends.` : null;
    case 'list':
      if (condition.value.length === 0) return 'Add at least one value.';
      return condition.value.length > maxIn ? `Use at most ${maxIn} values.` : null;
    default: return null;
  }
}

/** The form field a refusal's path names: the nearest field at or above the path, or null for the whole request. */
export function fieldFor(fields, path) {
  let at = path;
  while (!fields.has(at)) {
    const parent = at.replace(/(?:\.[A-Za-z_]\w*|\[\d+\])$/, '');
    if (parent === at) return null;
    at = parent;
  }
  return fields.get(at);
}

/** The value an error path such as $.query.filters[0].value names in a request, or undefined when it names nothing. */
export function valueAt(request, path) {
  const steps = /^\$((?:\.[A-Za-z_]\w*|\[\d+\])*)$/.exec(path);
  if (!steps) return undefined;
  let value = request;
  for (const [, member, index] of steps[1].matchAll(/\.([A-Za-z_]\w*)|\[(\d+)\]/g)) {
    const key = member ?? Number(index);
    if (value === null || typeof value !== 'object' || Array.isArray(value) !== (member === undefined) ||
        !Object.hasOwn(value, key)) return undefined;
    value = value[key];
  }
  return value;
}

/**
 * The query behind a column's values list: one row per value, most common first where the column can be counted and
 * sorted, otherwise in value order where it can be sorted, up to 50 values within the source's cap.
 */
export function valuesRequest(described, column) {
  const query = { group_by: [column.name] };
  if (column.canAggregate) {
    query.aggregates = [{ fn: 'count', col: column.name }];
    if (column.canSort) query.order_by = [{ col: `count_${column.name}`, dir: 'desc' }];
  } else if (column.canSort) query.order_by = [{ col: column.name, dir: 'asc' }];
  query.limit = Math.min(50, described.querySchema.limit.maximum);
  return { source: described.source.name, query };
}
