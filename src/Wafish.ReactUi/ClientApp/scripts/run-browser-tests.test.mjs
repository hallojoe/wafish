import { test } from 'node:test';
import assert from 'node:assert/strict';
import { access, stat } from 'node:fs/promises';
import { runOwnedProcess } from './run-browser-tests.mjs';

for (const code of [0, 1, 23]) {
  test(`fixture parent cleans state after exit ${code}`, async () => {
    let root;
    const result = await runOwnedProcess(process.execPath, ['-e', `require('fs').writeFileSync(process.env.UI_TEST_ROOT + '/twofactor.json', 'test-only'); process.exit(${code})`], {
      stdio: 'ignore', onStart: async directory => {
        root = directory;
        if (process.platform !== 'win32') assert.equal((await stat(root)).mode & 0o777, 0o700);
      },
    });
    assert.equal(result.code, code);
    await assert.rejects(access(root));
  });
}
test('fixture parent cleans state after child launch failure', async () => {
  let root;
  await assert.rejects(runOwnedProcess('/nonexistent-wafish-browser', [], { stdio: 'ignore', onStart: directory => { root = directory; } }));
  await assert.rejects(access(root));
});
test('fixture parent cleans state after an interrupted host', async () => {
  let root;
  const result = await runOwnedProcess(process.execPath, ['-e', "require('fs').writeFileSync(process.env.UI_TEST_ROOT + '/gateway.db', 'test-only'); process.kill(process.pid, 'SIGTERM')"], {
    stdio: 'ignore', onStart: directory => { root = directory; },
  });
  assert.equal(result.signal, 'SIGTERM');
  await assert.rejects(access(root));
});
