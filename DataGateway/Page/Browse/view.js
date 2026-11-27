// Builds page content from DataGateway's answers. Text enters only as text nodes, never as markup:
// catalog text and rows are data and must never become HTML.

const abilities = ['Select', 'Filter', 'Sort', 'Group', 'Aggregate'];

/**
 * A new element. An attribute set to true is written bare and one set to false or null is left out, as for checked
 * or disabled. String children become text nodes, and null children are left out.
 */
export function element(tag, attributes = {}, ...children) {
  const node = document.createElement(tag);
  for (const [name, value] of Object.entries(attributes))
    if (value !== false && value !== null) node.setAttribute(name, value === true ? '' : value);
  node.append(...children.filter(child => child !== null));
  return node;
}

export function sourceItems(sources, choose) {
  return sources.map(source => {
    const button = element('button', { type: 'button', 'data-source': source.name },
      source.title, element('span', { class: 'name' }, source.name));
    button.addEventListener('click', () => choose(source.name));
    return element('li', {}, button);
  });
}

export const sourceFacts = source => `Grain: ${source.grain}. At most ${source.rowCap} rows per query.`;

export function columnRows(columns) {
  const head = element('tr', {}, ...['Column', 'Meaning', 'Type', 'Can'].map(text => element('th', { scope: 'col' }, text)));
  const body = columns.map(column => element('tr', {},
    element('td', { class: 'name' }, column.name),
    element('td', {}, element('strong', {}, column.title), ' ', column.description),
    element('td', {}, typeOf(column)),
    element('td', {}, can(column))));
  return [element('thead', {}, head), element('tbody', {}, ...body)];
}

// Unit and magnitude belong with every number read from the column: "millions" means 523.06 is 523.06 million.
function typeOf(column) {
  const magnitude = column.magnitude === 'none' ? null : column.magnitude;
  return [column.dataType, column.unit, magnitude].filter(part => part !== null).join(' · ');
}

function can(column) {
  const allowed = abilities.filter(name => column['can' + name]).map(name => name.toLowerCase());
  return allowed.length > 0 ? allowed.join(', ') : 'nothing: described only';
}

/** A successful query answer: its count and hint, then its rows in the answer's column order. */
export function rows(answer) {
  const count = `${answer.rowCount} ${answer.rowCount === 1 ? 'row' : 'rows'}. ${answer.hint}`;
  const summary = element('p', { class: answer.more ? 'banner more' : 'muted' }, count);
  const head = element('tr', {}, ...answer.columns.map(column => element('th', { scope: 'col' }, column.name)));
  const body = answer.rows.map(row => element('tr', {}, ...answer.columns.map(column => cell(row[column.name]))));
  const table = element('table', {}, element('thead', {}, head), element('tbody', {}, ...body));
  return [summary, element('div', { class: 'scroll' }, table)];
}

function cell(value) {
  if (value === null) return element('td', {}, element('span', { class: 'null' }, 'null'));
  return element('td', typeof value === 'number' ? { class: 'number' } : {}, String(value));
}

/** A DataGateway error answer; requested is what the request holds at the error's path, when that can be shown. */
export function failure(answer, requested) {
  return element('div', { class: 'banner failure' },
    element('strong', {}, answer.code), ' at ', element('code', {}, answer.path),
    element('p', {}, answer.message),
    requested === undefined ? null : element('p', {}, 'The request has ', element('code', {}, JSON.stringify(requested)), ' there.'),
    answer.incidentId === undefined ? null : element('p', {}, 'Incident ', element('code', {}, answer.incidentId)));
}
