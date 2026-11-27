// The page's only network calls: its gateway's three POST routes, at the paths the server wrote into the page.
import { parseJson } from './query.js';

/** The gateway this page serves, as the server wrote it into the page: its name, and each route's path or null. */
export const gateway = JSON.parse(document.documentElement.dataset.gateway);

async function post(route, body) {
  const response = await fetch(gateway.routes[route], {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body
  });
  const text = await response.text();
  return { status: response.status, text, json: parseJson(text) };
}

export const listSources = () => post('list', '{}');
export const describeSource = source => post('describe', JSON.stringify({ source }));

/** Sends the form's request: DataGateway, not the page, decides whether it is valid. */
export const querySource = body => post('query', body);
