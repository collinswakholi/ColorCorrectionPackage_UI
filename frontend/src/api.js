/**
 * Centralized API utility for Color Correction Studio.
 * All backend fetch calls go through this module.
 */

const API_BASE = '';

/**
 * Fetch wrapper with JSON handling and error normalization.
 * @param {string} endpoint - API path (e.g. '/api/health')
 * @param {object} [options]
 * @param {string}  [options.method='GET']
 * @param {object}  [options.json]    - auto-serialized as JSON body
 * @param {*}       [options.body]    - raw body (FormData, etc.)
 * @param {AbortSignal} [options.signal]
 * @param {number}  [options.timeout] - ms, creates AbortSignal if no signal given
 * @returns {Promise<any>} parsed JSON response
 */
export async function apiFetch(endpoint, options = {}) {
  const { method = 'GET', body, json, signal, timeout } = options;

  const fetchOptions = { method, signal };

  if (json !== undefined) {
    fetchOptions.headers = { 'Content-Type': 'application/json' };
    fetchOptions.body = JSON.stringify(json);
  } else if (body) {
    fetchOptions.body = body;
  }

  if (timeout && !signal) {
    fetchOptions.signal = AbortSignal.timeout(timeout);
  }

  const resp = await fetch(`${API_BASE}${endpoint}`, fetchOptions);

  if (!resp.ok) {
    const text = await resp.text();
    let msg = `HTTP ${resp.status}`;
    try {
      const errJson = JSON.parse(text);
      msg = errJson.error || text;
    } catch {
      msg = text || msg;
    }
    throw new Error(msg);
  }

  return resp.json();
}

/**
 * Fire-and-forget POST (e.g. clear-session).
 */
export function apiPost(endpoint, json) {
  return apiFetch(endpoint, { method: 'POST', json });
}

/**
 * Upload files via FormData.
 */
export async function apiUpload(endpoint, fieldName, files) {
  const formData = new FormData();
  if (Array.isArray(files)) {
    files.forEach(f => formData.append(fieldName, f));
  } else {
    formData.append(fieldName, files);
  }
  return apiFetch(endpoint, { method: 'POST', body: formData });
}
