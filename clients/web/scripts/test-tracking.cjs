const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const web = path.resolve(__dirname, '..');
const output = path.resolve(web, '../../.artifacts/validation/tracking-tests');
fs.mkdirSync(output, { recursive: true });
execFileSync(process.execPath, [
  require.resolve('typescript/bin/tsc'),
  path.join(web, 'src/app/tracking-session.ts'),
  path.join(web, 'src/app/admin-sections.ts'),
  '--target',
  'ES2022',
  '--module',
  'commonjs',
  '--strict',
  '--skipLibCheck',
  '--outDir',
  output,
]);
const { TrackingSession, TrackingRefresh } = require(path.join(output, 'tracking-session.js'));
class Connection {
  events = {};
  calls = [];
  stopped = 0;
  failStart = false;
  failSubscribe = false;
  on(name, callback) {
    this.events[name] = callback;
  }
  onreconnecting(callback) {
    this.reconnecting = callback;
  }
  onreconnected(callback) {
    this.reconnected = callback;
  }
  onclose(callback) {
    this.closed = callback;
  }
  async start() {
    if (this.failStart) throw Error('offline');
  }
  async stop() {
    this.stopped++;
    this.closed?.();
  }
  async invoke(method, order) {
    this.calls.push([method, order]);
    if (this.failSubscribe) throw Error('forbidden');
  }
}
const tick = () => new Promise((resolve) => setImmediate(resolve));
test('reconnect subscribes again and reads changes missed while disconnected', async () => {
  const connection = new Connection();
  const states = [];
  let reads = 0;
  const tracking = new TrackingSession(
    async () => connection,
    (value) => states.push(value),
  );
  await tracking.watch('a', () => reads++);
  connection.reconnecting();
  assert.equal(states.at(-1), 'reconnecting');
  connection.reconnected();
  await tick();
  assert.equal(states.at(-1), 'live');
  assert.equal(reads, 2);
  assert.deepEqual(connection.calls, [
    ['Subscribe', 'a'],
    ['Subscribe', 'a'],
  ]);
});
test('closed connection can be replaced manually without a page reload', async () => {
  const first = new Connection(),
    second = new Connection();
  let created = 0;
  const states = [];
  const tracking = new TrackingSession(
    async () => (created++ ? second : first),
    (value) => states.push(value),
  );
  await tracking.watch('a', () => {});
  first.closed();
  assert.equal(states.at(-1), 'offline');
  await tracking.watch('a', () => {});
  assert.equal(states.at(-1), 'live');
  assert.equal(first.stopped, 1);
});
test('failed initial connection is retired and the next watch succeeds', async () => {
  const first = new Connection();
  first.failStart = true;
  const second = new Connection();
  let created = 0;
  const states = [];
  const tracking = new TrackingSession(
    async () => (created++ ? second : first),
    (value) => states.push(value),
  );
  await assert.rejects(tracking.watch('a', () => {}));
  assert.equal(states.at(-1), 'offline');
  await tracking.watch('a', () => {});
  assert.equal(states.at(-1), 'live');
});
test('events and callbacks from a former subscription cannot update another order', async () => {
  const first = new Connection(),
    second = new Connection();
  let created = 0,
    reads = 0;
  const tracking = new TrackingSession(
    async () => (created++ ? second : first),
    () => {},
  );
  await tracking.watch('a', () => {});
  await tracking.watch('b', () => reads++);
  first.events.orderChanged({ orderId: 'a' });
  first.reconnected();
  second.events.orderChanged({ orderId: 'a' });
  await tick();
  assert.equal(reads, 1);
  second.events.orderChanged({ orderId: 'b' });
  assert.equal(reads, 2);
});
test('rejected resubscription remains offline and never announces stale data as live', async () => {
  const connection = new Connection(),
    states = [];
  let reads = 0;
  const tracking = new TrackingSession(
    async () => connection,
    (value) => states.push(value),
  );
  await tracking.watch('a', () => reads++);
  connection.failSubscribe = true;
  connection.reconnecting();
  connection.reconnected();
  await tick();
  assert.equal(states.at(-1), 'offline');
  assert.equal(reads, 1);
  assert.equal(connection.stopped, 1);
});
test('logout during connection creation cannot revive tracking', async () => {
  let ready;
  const pending = new Promise((resolve) => (ready = resolve));
  const connection = new Connection(),
    states = [];
  const tracking = new TrackingSession(
    () => pending,
    (value) => states.push(value),
  );
  const starting = tracking.watch('a', () => assert.fail('retired callback'));
  await tick();
  await tracking.stop();
  ready(connection);
  await starting;
  assert.equal(states.at(-1), 'idle');
  assert.deepEqual(connection.calls, []);
  assert.equal(connection.stopped, 1);
});
test('an event received during a read forces one trailing refresh', async () => {
  let release;
  let reads = 0;
  const pending = new Promise((resolve) => (release = resolve));
  const refresh = new TrackingRefresh(async () => {
    if (++reads === 1) await pending;
  });
  const first = refresh.request();
  await refresh.request();
  await refresh.request();
  release();
  await first;
  assert.equal(reads, 2);
});
test('a failed read does not block later refreshes', async () => {
  let reads = 0;
  const refresh = new TrackingRefresh(async () => {
    if (++reads === 1) throw Error('offline');
  });
  await assert.rejects(refresh.request());
  await refresh.request();
  assert.equal(reads, 2);
});

const { visibleAdminSections } = require(path.join(output, 'admin-sections.js'));
test('administration only exposes sections authorized by permissions', () => {
  assert.deepEqual(visibleAdminSections([]), []);
  assert.deepEqual(
    visibleAdminSections(['support.manage']).map((x) => x.id),
    ['support'],
  );
  assert.deepEqual(
    visibleAdminSections(['finance.manage', 'payments.refund']).map((x) => x.id),
    ['finance'],
  );
  assert.deepEqual(
    visibleAdminSections(['identity.roles.manage']).map((x) => x.id),
    ['participants'],
  );
  assert.deepEqual(
    visibleAdminSections(['promotions.manage', 'configuration.manage']).map((x) => x.id),
    ['configuration'],
  );
});
