async function session() {
  const response = await fetch('/_wafish/auth/session');
  if (!response.ok) throw new Error('Session unavailable');
  return response.json();
}
async function submit(path) {
  const current = await session();
  return fetch(path, { method: 'POST', headers: { 'X-Wafish-CSRF': current.csrfToken } });
}
session().then(current => { document.querySelector('#visitor').textContent = current.name || 'Signed in'; });
document.querySelector('#send').onclick = async () => {
  try { const response = await submit('/api/support/tickets'); document.querySelector('#status').textContent = response.ok ? 'Ticket created' : `Request denied (${response.status})`; }
  catch { document.querySelector('#status').textContent = 'Service unavailable'; }
};
document.querySelector('#logout').onclick = async () => {
  const response = await submit('/_wafish/auth/logout');
  if (response.ok) location.assign('/');
};
