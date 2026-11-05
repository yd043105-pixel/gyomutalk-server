import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';

export class RemoteChannel {
  pending = new Map(); events = []; waiters = []; next = 0; closed = false;
  constructor(client, route = '/remote') { this.client = client; this.route = route; }
  async connect() {
    const n = await this.client.request(this.route + '/negotiate?negotiateVersion=1', 'POST', {});
    this.path = this.route + '?id=' + encodeURIComponent(n.connectionToken);
    await this.client.request(this.path);
    await this.client.request(this.path, 'POST', '{"protocol":"json","version":1}\u001e');
    this.parse(await this.client.request(this.path));
    this.polling = this.poll();
    return this;
  }
  parse(text) {
    for (const part of text.split('\u001e').filter(Boolean)) {
      const message = JSON.parse(part);
      if (message.type === 3) {
        const completion = this.pending.get(message.invocationId); if (!completion) continue;
        this.pending.delete(message.invocationId); clearTimeout(completion.timer);
        message.error ? completion.reject(new Error(message.error)) : completion.resolve(message.result);
      }
      if (message.type === 1) { this.events.push(message); this.flush(); }
    }
  }
  flush() {
    for (const waiter of [...this.waiters]) {
      const index = this.events.findIndex(e => e.target === waiter.name && waiter.predicate(e.arguments));
      if (index < 0) continue;
      const [event] = this.events.splice(index, 1); this.waiters.splice(this.waiters.indexOf(waiter), 1); clearTimeout(waiter.timer); waiter.resolve(event.arguments);
    }
  }
  event(name, predicate = () => true) {
    return new Promise((resolve, reject) => {
      const waiter = { name, predicate, resolve, timer: setTimeout(() => { this.waiters.splice(this.waiters.indexOf(waiter), 1); reject(new Error('Remote event timeout: ' + name)); }, 7000) };
      this.waiters.push(waiter); this.flush();
    });
  }
  async poll() {
    while (!this.closed) {
      try { this.parse(await this.client.request(this.path)); }
      catch (error) { if (!this.closed) this.failure = error; break; }
    }
  }
  async invoke(target, ...args) {
    if (this.failure) throw this.failure;
    const id = String(++this.next);
    const result = new Promise((resolve, reject) => this.pending.set(id, { resolve, reject, timer: setTimeout(() => { this.pending.delete(id); reject(new Error('Remote invocation timeout: ' + target)); }, 7000) }));
    try {
      // Observe the completion immediately: polling can reject it before POST finishes.
      const [, value] = await Promise.all([this.client.request(this.path, 'POST', JSON.stringify({ type: 1, invocationId: id, target, arguments: args }) + '\u001e'), result]);
      return value;
    }
    catch (error) { const pending = this.pending.get(id); if (pending) { clearTimeout(pending.timer); pending.reject(error); this.pending.delete(id); } throw error; }
  }
  async close() { this.closed = true; try { await this.client.request(this.path, 'DELETE', undefined, 202); } catch (error) { if (!this.failure && this.client.user) this.cleanupError = error; } await this.polling; }
}

export async function featureChecks({ Client, admin, bob, carol, password, check, stop, start, ready }) {
  const room = await admin.request('/api/chats', 'POST', { memberIds: [bob.user.id], name: '' });
  assert.equal((await bob.request('/api/chats', 'POST', { memberIds: [admin.user.id], name: '' })).id, room.id);
  await carol.request('/api/chats/' + room.id + '/messages', 'GET', undefined, 404);
  await carol.request('/api/chats/' + room.id + '/messages', 'POST', { clientId: randomUUID(), body: 'intrusion', attachmentIds: [] }, 404);
  const confidential = await bob.request('/api/chats', 'POST', { memberIds: [carol.user.id], name: '부서 대화' });
  await admin.request('/api/chats/' + confidential.id + '/messages', 'GET', undefined, 404);
  check('private and group chat membership enforced even for administrators');
  const file = await admin.upload('채팅자료.txt', '채팅 첨부 검증');
  const send = { clientId: randomUUID(), body: '안녕하세요. 채팅 자료입니다.', attachmentIds: [file.id] };
  const posted = await admin.request(`/api/chats/${room.id}/messages`, 'POST', send);
  assert.equal((await admin.request(`/api/chats/${room.id}/messages`, 'POST', send)).id, posted.id);
  await admin.request(`/api/chats/${room.id}/messages`, 'POST', { ...send, body: 'changed' }, 409);
  const messages = await bob.request(`/api/chats/${room.id}/messages`);
  assert.equal(messages.length, 1); assert.equal(messages[0].attachments[0].id, file.id);
  assert.equal(await bob.request('/api/attachments/' + file.id + '/download'), '채팅 첨부 검증');
  await carol.request('/api/attachments/' + file.id + '/download', 'GET', undefined, 404);
  assert.ok(!(await bob.request('/api/messages')).some(m => m.id === posted.id));
  assert.equal((await bob.request('/api/chats')).find(r => r.id === room.id).unread, 1);
  await bob.request(`/api/chats/${room.id}/read`, 'POST', { sequence: messages[0].sequence });
  assert.equal((await bob.request('/api/chats')).find(r => r.id === room.id).unread, 0);
  assert.equal((await bob.request(`/api/chats/${room.id}/messages?before=${messages[0].sequence}`)).length, 0);
  check('chat attachments, idempotent retry, unread counts and history cursor');
  const qs = [{ text: '회의 시간', kind: 'choice', required: true, options: ['15시', '16시'] }, { text: '의견', kind: 'text', required: false, options: [] }];
  const definition = { title: '서버 설문 검증', description: '기명 조사', deadline: Date.now() + 60000, questions: qs, targetIds: [admin.user.id], all: false };
  await bob.request('/api/surveys', 'POST', { ...definition, all: true }, 403);
  await bob.request('/api/surveys', 'POST', { ...definition, questions: [{ ...qs[0], options: ['같음', '같음'] }] }, 400);
  const survey = await bob.request('/api/surveys', 'POST', definition);
  await carol.request(`/api/surveys/${survey.id}`, 'GET', undefined, 404);
  await carol.request(`/api/surveys/${survey.id}/answers`, 'POST', { answers: ['15시', ''] }, 404);
  await bob.request(`/api/surveys/${survey.id}/answers`, 'POST', { answers: ['15시', ''] }, 404);
  await admin.request(`/api/surveys/${survey.id}/answers`, 'POST', { answers: ['없는 선택지', ''] }, 400);
  await admin.request(`/api/surveys/${survey.id}/answers`, 'POST', { answers: ['', ''] }, 400);
  await admin.request(`/api/surveys/${survey.id}/answers`, 'POST', { answers: ['15시', '의견 1'] });
  await admin.request(`/api/surveys/${survey.id}/answers`, 'POST', { answers: ['16시', '의견 수정'] });
  assert.equal((await admin.request(`/api/surveys/${survey.id}`)).answers[0], '16시');
  await admin.request(`/api/surveys/${survey.id}/results`, 'GET', undefined, 404);
  const results = await bob.request(`/api/surveys/${survey.id}/results`);
  assert.equal(results.responses.length, 1); assert.deepEqual(results.responses[0].answers, ['16시', '의견 수정']);
  check('survey recipients, required answers, edit replacement and owner-only results');
  await admin.request(`/api/surveys/${survey.id}/close`, 'POST', {}, 404);
  await bob.request(`/api/surveys/${survey.id}/close`, 'POST', {});
  await admin.request(`/api/surveys/${survey.id}/answers`, 'POST', { answers: ['15시', ''] }, 409);
  const expires = await bob.request('/api/surveys', 'POST', { ...definition, title: '마감 검증', deadline: Date.now() + 1200 });
  await new Promise(resolve => setTimeout(resolve, 1400));
  await admin.request(`/api/surveys/${expires.id}/answers`, 'POST', { answers: ['15시', ''] }, 409);
  check('survey owner closing and deadline block new and edited responses');
  await stop(); start(); await ready();
  assert.equal((await bob.request(`/api/chats/${room.id}/messages`))[0].body, send.body);
  assert.equal((await bob.request(`/api/surveys/${survey.id}/results`)).responses.length, 1);
  check('chat and survey data survive server restart');
  const channels = [];
  const hostPin = 'A'.repeat(64), viewerPin = 'B'.repeat(64);
  try {
    const host = await new RemoteChannel(bob).connect(); channels.push(host);
    const viewer = await new RemoteChannel(admin).connect(); channels.push(viewer);
    const outsider = await new RemoteChannel(carol).connect(); channels.push(outsider);
    const offer = await viewer.invoke('Request', bob.user.id, false, true);
    assert.equal((await host.event('Offer'))[0].id, offer.id);
    await assert.rejects(host.invoke('RegisterPeer', offer.id, hostPin, 55000), /수락된/);
    await assert.rejects(viewer.invoke('Check', offer.id), /권한/);
    await assert.rejects(outsider.invoke('Accept', offer.id, true), /수락/);
    await assert.rejects(outsider.invoke('End', offer.id), /권한/);
    await host.invoke('Accept', offer.id, false);
    assert.equal((await viewer.event('Started'))[0].control, false);
    await assert.rejects(outsider.invoke('RegisterPeer', offer.id, hostPin, 55000), /수락된/);
    await assert.rejects(outsider.invoke('Check', offer.id), /권한/);
    check('remote consent and exact accepted connection identity protect peer information');
    await assert.rejects(host.invoke('Frame', offer.id, '/9j/2Q=='), /Method does not exist/);
    await assert.rejects(viewer.invoke('Input', offer.id, { kind: 'key', value: 65 }), /Method does not exist/);
    check('server exposes no screen or input relay methods');
    await assert.rejects(host.invoke('RegisterPeer', offer.id, 'Z'.repeat(64), 55000), /지문/);
    await assert.rejects(host.invoke('RegisterPeer', offer.id, hostPin, 443), /포트/);
    await assert.rejects(viewer.invoke('RegisterPeer', offer.id, viewerPin, 55000), /변경/);
    await host.invoke('RegisterPeer', offer.id, hostPin.toLowerCase(), 55000);
    await assert.rejects(host.invoke('RegisterPeer', offer.id, viewerPin, 55000), /인증서/);
    await assert.rejects(host.invoke('RegisterPeer', offer.id, hostPin, 55001), /포트/);
    await viewer.invoke('RegisterPeer', offer.id, viewerPin, 0);
    const peer = (await viewer.event('PeerReady'))[0];
    assert.equal(peer.hostAddress, '127.0.0.1'); assert.equal(peer.hostPort, 55000);
    assert.equal(peer.hostPin, hostPin); assert.equal(peer.viewerPin, viewerPin);
    assert.deepEqual((await host.event('PeerReady'))[0], peer);
    assert.ok(!outsider.events.some(e => e.target === 'PeerReady'));
    await assert.rejects(viewer.invoke('RegisterPeer', offer.id, hostPin, 0), /변경/);
    check('peer pins and ports validated, immutable and sent only to accepted PCs with server-observed address');
    await assert.rejects(viewer.invoke('SetControl', offer.id, true), /공유자/);
    await host.invoke('SetControl', offer.id, true);
    assert.equal((await viewer.invoke('Check', offer.id)).controlRevision, 1);
    await host.invoke('SetControl', offer.id, false);
    const restricted = await viewer.invoke('Check', offer.id);
    assert.equal(restricted.control, false); assert.equal(restricted.controlRevision, 2);
    await host.invoke('SetControl', offer.id, true);
    assert.equal((await viewer.invoke('Check', offer.id)).controlRevision, 3);
    await viewer.invoke('End', offer.id); await host.event('Ended');
    await assert.rejects(viewer.invoke('Check', offer.id), /종료/);
    check('host-only control changes increment permission revision and ended support cannot renew authority');
    const again = await viewer.invoke('Request', bob.user.id, false, true); await host.event('Offer', a => a[0].id === again.id); await host.invoke('Accept', again.id, true); await viewer.event('Started', a => a[0].id === again.id);
    await host.close(); channels.splice(channels.indexOf(host), 1); await viewer.event('Ended', a => a[0] === again.id);
    await assert.rejects(viewer.invoke('Check', again.id), /종료/);
    check('remote disconnection permanently ends the accepted session');
    let host2 = await new RemoteChannel(bob).connect(); channels.push(host2);
    const sharing = await viewer.invoke('Request', bob.user.id, true, true); await host2.event('Offer', a => a[0].id === sharing.id);
    await viewer.invoke('SetControl', sharing.id, false); await host2.invoke('Accept', sharing.id, true);
    assert.equal((await host2.event('Started', a => a[0].id === sharing.id))[0].control, false);
    await assert.rejects(host2.invoke('SetControl', sharing.id, true), /공유자/); await viewer.invoke('End', sharing.id);
    check('sharing initiator can restrict a pending request to view-only before acceptance');
    const loggedOut = await viewer.invoke('Request', bob.user.id, false, true); await host2.event('Offer', a => a[0].id === loggedOut.id); await host2.invoke('Accept', loggedOut.id, true); await viewer.event('Started', a => a[0].id === loggedOut.id);
    await bob.request('/api/logout', 'POST', {}); await viewer.event('Ended', a => a[0] === loggedOut.id);
    await assert.rejects(viewer.invoke('Check', loggedOut.id), /종료/);
    await bob.login('bob', false, password);
    await host2.close(); channels.splice(channels.indexOf(host2), 1);
    host2 = await new RemoteChannel(bob).connect(); channels.push(host2);
    check('logging out terminates remote support before authority can be renewed');
    const revoked = await viewer.invoke('Request', bob.user.id, false, true); await host2.event('Offer', a => a[0].id === revoked.id); await host2.invoke('Accept', revoked.id, true); await viewer.event('Started', a => a[0].id === revoked.id);
    await admin.request('/api/admin/users/' + bob.user.id, 'PATCH', { name: bob.user.name, department: bob.user.department, active: false, isAdmin: false, canBroadcast: false });
    await viewer.event('Ended', a => a[0] === revoked.id); await assert.rejects(viewer.invoke('Check', revoked.id), /종료/);
    await admin.request('/api/admin/users/' + bob.user.id, 'PATCH', { name: bob.user.name, department: bob.user.department, active: true, isAdmin: false, canBroadcast: false }); await bob.login('bob', false, password);
    check('administrator account revocation automatically terminates remote support');
  } finally { await Promise.allSettled(channels.map(c => c.close())); }
  const attached = (await bob.request(`/api/chats/${room.id}/messages`))[0].attachments[0];
  await new Promise(resolve => setTimeout(resolve, Math.max(0, attached.expiresAt - Date.now() + 50)));
  await bob.request('/api/attachments/' + file.id + '/download', 'GET', undefined, 410);
  check('chat attachments follow the same thirty-day expiry and cleanup policy');
}
