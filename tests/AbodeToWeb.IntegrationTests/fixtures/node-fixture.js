'use strict';

const fs = require('node:fs');
const http = require('node:http');
const { spawn } = require('node:child_process');

const port = Number.parseInt(process.env.PORT ?? '0', 10);
const mode = process.env.ABODE_TO_WEB_FIXTURE_MODE ?? 'healthy';
const marker = process.env.ABODE_TO_WEB_CRASH_MARKER;

if (mode === 'orphan-parent' && marker) {
  const child = spawn(process.execPath, [__filename], {
    env: { ...process.env, ABODE_TO_WEB_FIXTURE_MODE: 'orphan-child' },
    stdio: 'ignore',
    windowsHide: true
  });
  fs.writeFileSync(marker, String(child.pid), { encoding: 'utf8', flag: 'wx' });
  console.log(`CHILD ${child.pid}`);
  setTimeout(() => process.exit(17), 1000);
} else if (mode === 'crash-once' && marker && !fs.existsSync(marker)) {
  fs.writeFileSync(marker, String(process.pid), { encoding: 'utf8', flag: 'wx' });
  console.error('fixture simulated crash');
  setTimeout(() => process.exit(23), 150);
} else {
  const server = http.createServer((request, response) => {
    if (request.url === '/health') {
      response.writeHead(200, { 'content-type': 'text/plain; charset=utf-8' });
      response.end('healthy');
      return;
    }

    response.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' });
    response.end('not found');
  });

  let stopping = false;
  const stop = () => {
    if (stopping) {
      return;
    }

    stopping = true;
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(2), 2000).unref();
  };

  if (mode !== 'orphan-child') {
    process.stdin.resume();
    process.stdin.on('end', stop);
  }
  process.on('SIGINT', stop);
  process.on('SIGTERM', stop);
  server.listen(port, '127.0.0.1', () => {
    console.log(`READY ${server.address().port}`);
    console.error('fixture stderr ready');
  });
}
