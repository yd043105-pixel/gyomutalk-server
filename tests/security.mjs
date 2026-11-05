import assert from 'node:assert/strict';
import { readdirSync } from 'node:fs';
import path from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { RemoteChannel } from './features.mjs';
import { passwordSecurityChecks } from './password-security.mjs';
import {writeDeploymentReviewFixture} from './deployment-review-fixture.mjs';

export async function securityChecks({ Client, admin, password, address, data, check, start, stop, ready, dotnet }) {
  const reviewFile=writeDeploymentReviewFixture(path.join(data,'..','review-office'),'office',address);
  assert.ok(admin.loginCookieHeaders.some(c => c.includes('httponly') && c.includes('samesite=strict')));
  const headers = (await fetch(address + '/')).headers;
  assert.equal(headers.get('x-frame-options'), 'DENY');
  assert.equal(headers.get('x-content-type-options'), 'nosniff');
  assert.ok(headers.get('content-security-policy').includes("script-src 'self'"));
  check('browser session cookie and content, script and framing protections');
  const database = new DatabaseSync(path.join(data, 'school.db'), { readOnly: true });
  try {
    const hash = Buffer.from(database.prepare('SELECT PasswordHash FROM Users WHERE Username=?').get('admin').PasswordHash, 'base64');
    assert.equal(hash[0], 1); assert.equal(hash.readUInt32BE(1), 2); assert.ok(hash.readUInt32BE(5) >= 220000);
  } finally { database.close(); }
  check('stored administrator password uses salted PBKDF2-HMAC-SHA512 with at least 220000 iterations');
  await passwordSecurityChecks({ Client, admin, password, address, data, check });
  for (const route of ['/api/users', '/hub/negotiate?negotiateVersion=1', '/remote/negotiate?negotiateVersion=1', '/hub?id=unknown'])
    await admin.request(route, route.includes('negotiate') ? 'POST' : 'GET', undefined, 403, true, { Origin: 'https://other-school.invalid' });
  await admin.request('/api/users', 'GET', undefined, 200, true, { Origin: address });
  await admin.request('/api/users', 'GET', undefined, 403, true, { Origin: 'null' });
  check('foreign and null browser origins blocked on API and both realtime transports');
  const created = await admin.request('/api/admin/users', 'POST', { username: 'security.teacher', name: '보안검사', department: '검증', password, isAdmin: false, canBroadcast: false });
  const teacher = new Client(); await teacher.login('security.teacher', true, password);
  const stolen = new Client(); stolen.cookies = new Map(teacher.cookies); stolen.csrf = teacher.csrf;
  const notifications = await new RemoteChannel(teacher, '/hub').connect();
  const remote = await new RemoteChannel(teacher).connect();
  try {
    await teacher.request('/api/logout', 'POST', {});
    await stolen.request('/api/users', 'GET', undefined, 401);
    await stolen.request('/remote/negotiate?negotiateVersion=1', 'POST', {}, 401);
    for (let i = 0; i < 40 && (!notifications.failure || !remote.failure); i++) await new Promise(r => setTimeout(r, 100));
    assert.ok(notifications.failure && remote.failure, 'logout must close both existing realtime connections');
    check('logout revokes copied persistent cookie and closes message and remote connections');
  } finally { await Promise.allSettled([notifications.close(), remote.close()]); }
  await teacher.login('security.teacher', false, password);
  const channel = await new RemoteChannel(teacher, '/hub').connect();
  try {
    await admin.request('/api/admin/users/' + created.id, 'PATCH', { name: '보안검사', department: '검증', active: false, isAdmin: false, canBroadcast: false });
    for (let i = 0; i < 40 && !channel.failure; i++) await new Promise(r => setTimeout(r, 100));
    assert.ok(channel.failure, 'revoked teacher must stop receiving realtime notifications');
    check('account deactivation closes an already authenticated message connection');
  } finally { await channel.close(); }
  // Scanner outcomes, including the existing school-antivirus fallback, are covered by file-scan.mjs.
  await stop(); start(); await ready();
}
