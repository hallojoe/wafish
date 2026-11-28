// Run after Aspire reports visitor-gateway healthy. This script never starts or stops resources.
import { chromium } from '../src/Wafish.ReactUi/ClientApp/node_modules/@playwright/test/index.mjs';
import { readFile } from 'node:fs/promises';
import { homedir } from 'node:os';
import { join } from 'node:path';
import assert from 'node:assert/strict';

const origin = process.env.WAFISH_VISITOR_ORIGIN || 'https://localhost:7443';
const topology = process.env.WAFISH_TOPOLOGY_ID || 'default';
const secretsFile = process.env.WAFISH_APPHOST_SECRETS_FILE || join(homedir(), '.microsoft/usersecrets/cb3f795d-3f7b-45b3-ad5b-f4cba4413556/secrets.json');
const secrets = JSON.parse((await readFile(secretsFile, 'utf8')).replace(/^\uFEFF/, ''));
const browser = await chromium.launch({ headless: true });
try {
  for (const user of ['writer', 'reader']) {
    const password = secrets[`Parameters:${topology}-visitor-${user}-password`];
    assert(password, `Missing ${user} development credential`);
    const context = await browser.newContext();
    try {
      const page = await context.newPage();
      page.setDefaultTimeout(15000);
      page.setDefaultNavigationTimeout(15000);
      await page.goto(origin);
      assert.equal(await page.locator('h1').textContent(), 'Public page');
      const anonymous = await context.request.post(origin + '/api/support/tickets');
      assert.equal(anonymous.status(), 401);
      await page.getByRole('link', { name: 'Open the protected support page' }).click();
      await page.locator('#username').fill(user);
      await page.locator('#password').fill(password);
      await page.locator('#kc-login').click();
      await page.waitForURL(origin + '/logged-in/index.html');
      assert.equal(await page.locator('h1').textContent(), 'Protected support page');
      const status = await context.request.get(origin + '/_wafish/auth/session');
      assert.equal(status.status(), 200);
      const session = await status.json();
      assert.equal(session.authenticated, true);
      if (process.env.WAFISH_VISITOR_REPLICA_ORIGIN) {
        const replica = await context.request.get(process.env.WAFISH_VISITOR_REPLICA_ORIGIN + '/_wafish/auth/session', { headers: { Host: new URL(origin).host } });
        assert.equal(replica.status(), 200);
        assert.equal((await replica.json()).authenticated, true);
      }
      assert.equal(Object.hasOwn(session, 'access_token'), false);
      assert.equal((await context.request.post(origin + '/api/support/tickets')).status(), 403);
      const forged = await context.request.post(origin + '/api/support/tickets', { headers: { Origin: 'https://other.example', 'X-Wafish-CSRF': session.csrfToken } });
      assert.equal(forged.status(), 403);
      const response = page.waitForResponse(r => r.url() === origin + '/api/support/tickets' && r.request().method() === 'POST');
      await page.getByRole('button', { name: 'Send support ticket' }).click();
      assert.equal((await response).status(), user === 'writer' ? 201 : 403);
      if (user === 'writer' && process.env.WAFISH_VISITOR_VERIFY_REFRESH === 'true') {
        // The development realm issues 120-second tokens. Enter the gateway's 60-second refresh window.
        await new Promise(resolve => setTimeout(resolve, 65000));
        const renewed = await context.request.post(origin + '/api/support/tickets', {
          headers: { Origin: origin, 'X-Wafish-CSRF': session.csrfToken }, data: { subject: 'Refresh verification' }
        });
        assert.equal(renewed.status(), 201);
        console.log('PASS: real-provider access token refresh');
      }
      assert((await context.cookies()).filter(c => c.name.startsWith('__Host-wafish')).every(c => c.httpOnly && c.secure));
      await page.getByRole('button', { name: 'Sign out' }).click();
      await page.waitForURL(origin + '/');
      assert.equal((await context.request.post(origin + '/api/support/tickets')).status(), 401);
      console.log(`PASS: ${user} login, protected page, API permission, CSRF, and logout`);
    } finally { await context.close(); }
  }
} finally { await browser.close(); }
