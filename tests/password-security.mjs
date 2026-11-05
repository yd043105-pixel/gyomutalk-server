import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { randomUUID } from 'node:crypto';
import path from 'node:path';

export async function passwordSecurityChecks({ Client, admin, password, address, data, check }) {
  const replacement = 'Reset-' + randomUUID();
  const create = (username, secret = password) => admin.request('/api/admin/users', 'POST', {
    username, name: '비밀번호 보안 시험', department: '가상 자료', password: secret, isAdmin: false, canBroadcast: false
  });
  const account = await create('security.password-race');
  const reference = await create('security.reset-reference', replacement);
  const client = new Client(); await client.login('security.password-race', false, password);
  const db = new DatabaseSync(path.join(data, 'school.db'));
  let pending;
  try {
    const resetHash = db.prepare('SELECT PasswordHash FROM Users WHERE Id=?').get(reference.id).PasswordHash;
    // WAL allows the request's authentication/password reads while the external reset owns the write lock.
    db.exec('BEGIN IMMEDIATE');
    pending = fetch(address + '/api/password', {
      method: 'POST', headers: { Cookie: [...client.cookies].map(([k, v]) => k + '=' + v).join('; '),
        'Content-Type': 'application/json', 'X-CSRF-TOKEN': client.csrf },
      body: JSON.stringify({ currentPassword: password, newPassword: 'Obsolete-' + randomUUID() })
    });
    await new Promise(resolve => setTimeout(resolve, 1500));
    db.prepare('UPDATE Users SET PasswordHash=?,SessionVersion=SessionVersion+1 WHERE Id=?').run(resetHash, account.id);
    db.exec('COMMIT');
    const response = await pending;
    assert.ok([401, 409].includes(response.status), 'A stale password-change request must not overwrite a newer reset: ' + response.status);
    await response.arrayBuffer();
    assert.equal(db.prepare('SELECT PasswordHash FROM Users WHERE Id=?').get(account.id).PasswordHash, resetHash);
  } finally {
    if (db.isTransaction) db.exec('ROLLBACK');
    db.close();
    if (pending) await pending.catch(() => {});
  }
  await client.request('/api/users', 'GET', undefined, 401);
  await client.login('security.password-race', false, replacement);
  check('in-flight password changes cannot overwrite administrator resets; reset hash and revoked session remain effective');

  const limitedAccount = await create('security.password-limit');
  const limited = new Client(); await limited.login('security.password-limit', false, password);
  for (let i = 0; i < 10; i++) await limited.request('/api/password', 'POST', {
    currentPassword: 'Incorrect-' + randomUUID(), newPassword: 'Unused-' + randomUUID()
  }, 400);
  await limited.request('/api/password', 'POST', { currentPassword: password, newPassword: replacement }, 429);
  await client.request('/api/password', 'POST', { currentPassword: replacement, newPassword: password });
  await client.request('/api/users', 'GET', undefined, 401);
  check('credential changes have a separate per-account attempt limit; another teacher can still change a password and old sessions are revoked');
  for (const user of [account, reference, limitedAccount]) await admin.request('/api/admin/users/' + user.id, 'PATCH', {
    name: '비밀번호 보안 시험', department: '가상 자료', active: false, isAdmin: false, canBroadcast: false
  });
}
