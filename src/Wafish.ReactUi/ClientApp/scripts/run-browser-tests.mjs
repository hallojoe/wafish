import { spawn } from 'node:child_process';
import { mkdtemp, chmod, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

// The parent owns cleanup even when browser setup or a host process fails.
export async function runOwnedProcess(command, args, { env = process.env, stdio = 'inherit', onStart = () => {} } = {}) {
  const root = await mkdtemp(join(tmpdir(), 'wafish-browser-'));
  await chmod(root, 0o700);
  let child;
  let interrupted;
  let timer;
  const signalGroup = signal => {
    if (!child?.pid) return;
    try {
      if (process.platform === 'win32') child.kill(signal);
      else process.kill(-child.pid, signal);
    } catch (error) { if (error.code !== 'ESRCH') throw error; }
  };
  const interrupt = signal => {
    interrupted = signal;
    signalGroup(signal);
    timer ??= setTimeout(() => signalGroup('SIGKILL'), 5000);
  };
  const onInt = () => interrupt('SIGINT');
  const onTerm = () => interrupt('SIGTERM');
  process.on('SIGINT', onInt);
  process.on('SIGTERM', onTerm);
  try {
    child = spawn(command, args, { env: { ...env, UI_TEST_ROOT: root }, stdio, detached: process.platform !== 'win32' });
    const completion = new Promise((resolve, reject) => {
      child.once('error', reject);
      child.once('close', (code, signal) => resolve({ code: code ?? (signal === 'SIGINT' ? 130 : 143), signal }));
    });
    await onStart(root, child.pid);
    return await completion;
  } finally {
    signalGroup('SIGTERM');
    if (child?.pid && process.platform !== 'win32') {
      for (let attempt = 0; attempt < 100; attempt++) {
        try { process.kill(-child.pid, 0); } catch { break; }
        await delay(20);
      }
      signalGroup('SIGKILL');
    }
    clearTimeout(timer);
    process.off('SIGINT', onInt);
    process.off('SIGTERM', onTerm);
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
    if (interrupted) process.exitCode = interrupted === 'SIGINT' ? 130 : 143;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const require = createRequire(import.meta.url);
  const result = await runOwnedProcess(process.execPath, [require.resolve('@playwright/test/cli'), 'test', ...process.argv.slice(2)]);
  process.exitCode = result.code;
}
