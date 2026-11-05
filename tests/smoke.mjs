import {acceptRequiredPoliciesForFixture} from './registration-fixture.mjs';
import assert from 'node:assert/strict';
import {DatabaseSync} from 'node:sqlite';
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync, unlinkSync } from 'node:fs';
import path from 'node:path';
import net from 'node:net';
import { featureChecks } from './features.mjs';
import { securityChecks } from './security.mjs';
import { presenceChecks } from './presence.mjs';
import { workChecks } from './work.mjs';
import { officeChecks } from './office.mjs';

const root = process.cwd();
const localDotnet = path.join(root, '.tools/dotnet/dotnet.exe');
const dotnet = existsSync(localDotnet) ? localDotnet : process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, 'dotnet.exe') : 'dotnet';
const serverDll = path.join(root, 'src/SchoolMessenger.Server/bin/Debug/net10.0/SchoolMessenger.Server.dll');
const packaged = process.argv.includes('--package');
const skipNativeInput = process.argv.includes('--skip-native-input');
const packageRoot = process.env.YY_PACKAGE_ROOT ?? path.join(root, 'artifacts/여양고-교무메신저');
const data = path.join(process.env.YY_TEST_DATA_ROOT??path.join(root,'.test-data'), randomUUID());
const password = 'Test-' + randomUUID();
mkdirSync(path.join(root, 'artifacts'), { recursive: true });
const listener = net.createServer();
await new Promise(resolve => listener.listen(0, '127.0.0.1', resolve));
const port = listener.address().port; await new Promise(resolve => listener.close(resolve));
const address = `http://127.0.0.1:${port}`;
let server, output = '', passed = [];
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
function start(overrides = {}) {
  output = '';
  server = spawn(packaged ? path.join(packageRoot, 'server/SchoolMessenger.Server.exe') : dotnet, packaged ? ['--urls', address] : [serverDll, '--urls', address], {
    cwd: packaged ? path.join(packageRoot, 'server') : path.join(root, 'src/SchoolMessenger.Server'), windowsHide: true,
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', School__DataDirectory: data,
      School__AdminPassword: password, School__RetentionSeconds: '8', School__CleanupSeconds: '1', ...overrides }
  });
  server.stdout.on('data', text => { output += text; }); server.stderr.on('data', text => { output += text; });
}
async function ready() {
  const deadline=Date.now()+60000;
  while(Date.now()<deadline){
    if(server.exitCode!==null)throw new Error('Server stopped before readiness: '+output);
    try{if((await fetch(address+'/health',{signal:AbortSignal.timeout(1000)})).ok)return;}catch{}
    await delay(100);
  }
  throw new Error('Server failed to start: ' + output);
}
async function stop() { if (server && server.exitCode === null) { const closed = new Promise(resolve => server.once('exit', resolve)); server.kill(); await closed; } }
class Client {
  cookies = new Map(); csrf = '';
  async request(route, method = 'GET', body, expected = 200, withCsrf = true, extraHeaders = {}) {
    if(route==='/api/register'&&method==='POST'&&body?.agreement===undefined)body={...body,agreement:await acceptRequiredPoliciesForFixture(address)};
    const headers = { Cookie: [...this.cookies].map(([key, value]) => `${key}=${value}`).join('; '), ...extraHeaders };
    if (method !== 'GET' && withCsrf) headers['X-CSRF-TOKEN'] = this.csrf;
    const options = { method, headers };
    if (body instanceof FormData) options.body = body;
    else if (typeof body === 'string') { headers['Content-Type'] = 'text/plain'; options.body = body; }
    else if (body !== undefined) { headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(body); }
    const response = await fetch(address + route, options);
    this.lastSetCookies = response.headers.getSetCookie();
    for (const cookie of response.headers.getSetCookie()) { const pair = cookie.split(';')[0], index = pair.indexOf('='); this.cookies.set(pair.slice(0, index), pair.slice(index + 1)); }
    const text = await response.text();
    assert.equal(response.status, expected, `${method} ${route}: ${response.status} ${text}`);
    if (response.headers.get('content-type')?.includes('application/json') && text) return JSON.parse(text);
    return text;
  }
  async login(username, rememberLogin = false, loginPassword = password) { let session = await this.request('/api/session'); this.csrf = session.csrfToken; await this.request('/api/login', 'POST', { username, password: loginPassword, rememberLogin }); this.loginCookieHeaders = this.lastSetCookies; session = await this.request('/api/session'); this.csrf = session.csrfToken; this.user = session.user; }
  async upload(name, content, expected = 200) { const form = new FormData(); form.append('file', new Blob([content]), name); return this.request('/api/attachments', 'POST', form, expected); }
}
function check(label) { passed.push(label); console.log('PASS ' + label); }
try {
  start(); await ready();
  for (const asset of ['updates.json','update-notice.js','update-notice.css']) assert.equal((await fetch(address+'/'+asset)).status,200);
  assert.equal((await (await fetch(address+'/updates.json')).json()).version,JSON.parse(readFileSync('src/SchoolMessenger.Shared/updates.json','utf8')).version);
  check('school web serves bundled update notes and popup assets in development and packaged builds');
  const anonymous = new Client(), admin = new Client(), alice = new Client(), bob = new Client(), carol = new Client();
  await anonymous.request('/api/messages', 'GET', undefined, 401); check('unauthenticated mailbox blocked');
  await admin.login('admin');
  await securityChecks({ Client, admin, password, address, data, check, start, stop, ready, dotnet });
  await admin.request('/api/admin/users', 'POST', { username: 'no-csrf' }, 400, false); check('CSRF required');
  for (const [username, name, department] of [['alice', '김담임', '1학년부'], ['bob', '이교무', '교무부'], ['carol', '박행정', '행정실']])
    await admin.request('/api/admin/users', 'POST', { username, name, department, password, isAdmin: false, canBroadcast: false });
  await alice.login('alice'); await bob.login('bob'); await carol.login('carol');
  await presenceChecks({Client,admin,bob,carol,check});
  await anonymous.request('/hub/negotiate?negotiateVersion=1', 'POST', {}, 401);
  const negotiation = await bob.request('/hub/negotiate?negotiateVersion=1', 'POST', {});
  const hubPath = '/hub?id=' + encodeURIComponent(negotiation.connectionToken);
  await bob.request(hubPath);
  await bob.request(hubPath, 'POST', '{"protocol":"json","version":1}\u001e');
  assert.ok((await bob.request(hubPath)).includes('{}'));
  const notification = bob.request(hubPath);
  await alice.request('/api/admin/users', 'GET', undefined, 403); check('teacher cannot administer accounts');
  let form = new FormData(); form.append('file', new Blob(['payload']), 'danger.exe');
  await alice.request('/api/attachments', 'POST', form, 400);
  form = new FormData(); form.append('file', new Blob(['not a PDF']), 'fake.pdf');
  await alice.request('/api/attachments', 'POST', form, 400); check('extension and file signature validation');
  const attachment = await alice.upload('회의자료.txt', '교무회의 자료입니다.');
  const send = { clientId: randomUUID(), title: '교무회의 자료 확인', body: '내일 15시 교무회의입니다. 첨부 자료를 확인해 주세요.', recipientIds: [bob.user.id], attachmentIds: [attachment.id], all: false };
  const message = await alice.request('/api/messages', 'POST', send);
  let signal = await notification;
  for (let i = 0; i < 4 && !signal.includes('NewMessage'); i++) signal += await bob.request(hubPath);
  assert.ok(signal.includes('NewMessage') && signal.includes(message.id));
  await bob.request(hubPath, 'DELETE', undefined, 202);
  check('authenticated SignalR notification after database commit');
  assert.equal((await alice.request('/api/messages?box=conversation&person=' + bob.user.id)).length, 1);
  assert.equal((await bob.request('/api/messages?box=conversation&person=' + alice.user.id)).length, 1);
  assert.equal((await carol.request('/api/messages?box=conversation&person=' + alice.user.id)).length, 0);
  check('person message history includes sent and received with authorization');
  const duplicate = await alice.request('/api/messages', 'POST', send);
  assert.equal(duplicate.id, message.id); assert.equal(duplicate.duplicate, true);
  await alice.request('/api/messages', 'POST', { ...send, body: '다른 내용' }, 409);
  assert.equal((await alice.request('/api/messages?box=sent')).length, 1); check('durable send and duplicate retry protection');
  assert.equal((await bob.request('/api/attachments/' + attachment.id + '/download')), '교무회의 자료입니다.');
  await carol.request('/api/messages/' + message.id, 'GET', undefined, 404);
  await carol.request('/api/attachments/' + attachment.id + '/download', 'GET', undefined, 404); check('message and attachment recipient authorization');
  await bob.request('/api/messages/' + message.id + '/read', 'POST', {});
  const detail = await alice.request('/api/messages/' + message.id);
  assert.ok(detail.recipients[0].readAt); assert.equal((await bob.request('/api/messages?box=unread')).length, 0); check('per-recipient read receipt');
  await alice.request('/api/messages', 'POST', { ...send, clientId: randomUUID(), all: true, attachmentIds: [] }, 403); check('broadcast authorization');
  const group = await admin.request('/api/messages', 'POST', { clientId: randomUUID(), title: '학교 공지', body: '전체 교직원 안내입니다.', recipientIds: [], attachmentIds: [], all: true });
  assert.equal((await admin.request('/api/messages/' + group.id)).recipients.length, 4); check('broadcast recipient snapshot');
  const sent = await Promise.all(Array.from({ length: 12 }, (_, i) => admin.request('/api/messages', 'POST', { clientId: randomUUID(), title: '동시 전송 ' + i, body: '동시 쓰기 검증', recipientIds: [bob.user.id], attachmentIds: [], all: false })));
  assert.equal(new Set(sent.map(m => m.id)).size, 12); check('12 concurrent sends without loss');
  await admin.request('/api/admin/maintenance', 'POST', {});
  const backups = readdirSync(path.join(data, 'backups')); assert.ok(backups.some(name => name.endsWith('.db')));
  assert.ok(!readFileSync(path.join(data, 'backups', backups[0])).includes(Buffer.from('교무회의 자료입니다.'))); check('SQLite backup excludes attachment contents');
  await admin.request('/api/admin/users/' + carol.user.id, 'PATCH', { name: '박행정', department: '행정실', active: false, isAdmin: false, canBroadcast: false });
  await carol.request('/api/users', 'GET', undefined, 401); check('account deactivation invalidates existing session');
  await stop(); start(); await ready();
  assert.equal((await bob.request('/api/messages?box=received')).length, 14); check('messages survive server restart and offline delivery');
  await delay(8500);
  await bob.request('/api/attachments/' + attachment.id + '/download', 'GET', undefined, 410);
  assert.ok(!existsSync(path.join(data, 'files', attachment.id))); check('expired attachment blocked and physically removed');
  await admin.login('admin');
  if (process.argv.includes('--ui') || process.argv.includes('--features')) {
    const applicant = new Client(); applicant.csrf = (await applicant.request('/api/session')).csrfToken;
    const registration = { username: 'new.teacher', name: '신청교사', department: '교무기획부', password, isAdmin: true, canBroadcast: true };
    await applicant.request('/api/register', 'POST', registration, 400, false);
    assert.equal((await applicant.request('/api/register', 'POST', registration)).status, 'pending');
    await applicant.request('/api/register', 'POST', registration, 409);
    assert.ok((await applicant.request('/api/login', 'POST', { username: registration.username, password }, 403)).error.includes('승인 대기'));
    await applicant.request('/api/login', 'POST', { username: registration.username, password: 'incorrect-password' }, 401);
    await applicant.request('/api/users', 'GET', undefined, 401);
    await applicant.request('/api/messages', 'GET', undefined, 401);
    assert.ok(!(await admin.request('/api/users')).some(u => u.name === registration.name));
    const pending = (await admin.request('/api/admin/registrations')).find(r => r.username === registration.username);
    assert.ok(pending && !('passwordHash' in pending));
    await alice.request('/api/admin/registrations', 'GET', undefined, 403);
    await alice.request('/api/admin/registrations/' + pending.id + '/approve', 'POST', {}, 403);
    check('registration requires CSRF and pending users cannot log in or access messages');
    await admin.request('/api/admin/registrations/' + pending.id + '/approve', 'POST', {});
    await admin.request('/api/admin/registrations/' + pending.id + '/approve', 'POST', {}, 409);
    await applicant.login(registration.username, true);
    assert.equal(applicant.user.isAdmin, false); assert.equal(applicant.user.canBroadcast, false);
    assert.ok(!(await admin.request('/api/admin/registrations')).some(r => r.id === pending.id));
    await applicant.request('/api/register', 'POST', registration, 409);
    check('only administrators approve and registration cannot grant privileged roles');
    const persistent = applicant.loginCookieHeaders.find(c => c.startsWith('SchoolMessenger.Session='));
    assert.ok(new Date(persistent.match(/expires=([^;]+)/i)[1]).getTime() > Date.now() + 29 * 86400000);
    const restored = new Client(); restored.cookies = new Map(applicant.cookies);
    assert.equal((await restored.request('/api/session')).user.id, applicant.user.id);
    const nextPassword = 'Changed-' + randomUUID();
    await admin.request('/api/admin/users/' + applicant.user.id, 'PATCH', { name: registration.name, department: registration.department, active: true, isAdmin: false, canBroadcast: false, password: nextPassword });
    await restored.request('/api/users', 'GET', undefined, 401);
    await applicant.login(registration.username, true, nextPassword);
    await admin.request('/api/admin/users/' + applicant.user.id, 'PATCH', { name: registration.name, department: registration.department, active: false, isAdmin: false, canBroadcast: false });
    await applicant.request('/api/users', 'GET', undefined, 401);
    check('persistent login cookie restores without password and account changes revoke it');
    const rejected = new Client(); rejected.csrf = (await rejected.request('/api/session')).csrfToken;
    await rejected.request('/api/register', 'POST', { ...registration, username: 'rejected.teacher' });
    const firstRequest = (await admin.request('/api/admin/registrations')).find(r => r.username === 'rejected.teacher');
    await admin.request('/api/admin/registrations/' + firstRequest.id + '/reject', 'POST', {});
    assert.ok((await rejected.request('/api/login', 'POST', { username: 'rejected.teacher', password }, 403)).error.includes('반려'));
    await rejected.request('/api/register', 'POST', { ...registration, username: 'rejected.teacher', name: '수정신청교사' });
    const resubmitted = (await admin.request('/api/admin/registrations')).find(r => r.username === 'rejected.teacher');
    assert.notEqual(resubmitted.id, firstRequest.id); assert.equal(resubmitted.name, '수정신청교사');
    check('rejected registration cannot log in and can be corrected and resubmitted');
    await featureChecks({ Client, admin, bob, carol: alice, password, check, stop, start, ready });
    if (process.argv.includes('--ui')) {
    const sampleDeadline=Date.now()+7*86400000;
    const sample=await admin.request('/api/messages','POST',{clientId:randomUUID(),title:'부서별 운영 계획 자료 제출',body:'부서별 운영 계획 자료를 제출해 주세요.',recipientIds:[alice.user.id,bob.user.id],attachmentIds:[],all:false,submissionDeadline:sampleDeadline});
    const sampleSender=new Client();await sampleSender.login('bob');const sampleFile=await sampleSender.upload('학년부_운영계획.txt','가상 시운행 제출 자료입니다.');
    await sampleSender.request('/api/submission-requests/'+sample.id+'/submit','POST',{clientId:randomUUID(),attachmentIds:[sampleFile.id]});
    const sampleDb=new DatabaseSync(path.join(data,'school.db'));sampleDb.prepare('UPDATE Attachments SET ExpiresAt=? WHERE Id=?').run(sampleDeadline,sampleFile.id);sampleDb.close();
    process.env.YY_SUBMISSION_PREVIEW_ID=sample.id;
    await admin.request('/api/messages', 'POST', { clientId: randomUUID(), title: '교무회의 안내 · 자료 확인', body: '선생님들께 안내드립니다.\n\n내일 오후 3시, 본관 회의실에서 교무회의가 있습니다.\n부서별 전달 사항을 준비해 주세요.\n\n자료는 첨부파일 보관 기한 내에 저장해 주세요.', recipientIds: [admin.user.id], attachmentIds: [], all: false });
    if (existsSync('artifacts/ui-error.txt')) unlinkSync('artifacts/ui-error.txt');
    const installedClient=process.env.YY_INSTALLED_CLIENT;
    const ui = spawn(installedClient ?? (packaged ? path.join(packageRoot, 'client/SchoolMessenger.exe') : dotnet), installedClient || packaged ? ['--verify-ui'] : [path.join(root, 'src/SchoolMessenger.Desktop/bin/Debug/net10.0-windows/SchoolMessenger.dll'), '--verify-ui'], {
      cwd: root, windowsHide: true, env: { ...process.env, SCHOOL_UI_SERVER: address, SCHOOL_UI_USER: 'admin', SCHOOL_UI_PASSWORD: password, SCHOOL_UI_OUTPUT: path.join(root, 'artifacts/client-preview.png'), SCHOOL_UI_LOCALDIR: path.join(data, 'client'), SCHOOL_UI_SKIP_INPUT: skipNativeInput ? '1' : '0' }
    });
    let uiDeadline;
    const code = await Promise.race([new Promise(resolve => ui.once('exit', resolve)), new Promise(resolve => { uiDeadline = setTimeout(() => { ui.kill(); resolve(-1); }, 90000); })]);
    clearTimeout(uiDeadline);
    assert.equal(code, 0, existsSync('artifacts/ui-error.txt') ? readFileSync('artifacts/ui-error.txt', 'utf8') : 'UI failed');
    // Native logout revokes every session of this account, including this test driver.
    await admin.login('admin', false, password);
    assert.ok(existsSync('artifacts/client-preview.png')); assert.ok(existsSync('artifacts/compose-preview.png'));
    check('native WPF login, mailbox, detail, compose send and screenshots');
    assert.ok((await admin.request('/api/admin/registrations')).some(r => r.username === 'native.teacher'));
    assert.ok(existsSync('artifacts/registration-preview.png') && existsSync('artifacts/login-preview.png'));
    assert.equal(existsSync(path.join(data, 'client/login.dat')), false);
    assert.equal(JSON.parse(readFileSync(path.join(data, 'client/preferences.json'), 'utf8')).AutoLogin, false);
    check('native registration, DPAPI automatic login, logout cleanup and startup settings');
    assert.equal(JSON.parse(readFileSync('artifacts/update-notice-check.json', 'utf8')).checks, 6);
    assert.ok(existsSync('artifacts/update-notice-preview.png'));
    check('native update popup confirms once, preserves deferred notice, handles older/newer versions and manual history');
    assert.ok(existsSync('artifacts/tasks-preview.png') && existsSync('artifacts/submissions-preview.png'));
    assert.ok((await admin.request('/api/todos')).some(t => t.title === 'Windows 업무 창에서 등록한 할 일'));
    assert.ok((await admin.request('/api/submission-requests')).some(r => r.title === 'Windows 제출 요청 검증'));
    check('native WPF task registration and compose submission request with collector status screen');
    for (const name of ['chat-preview.png', 'survey-preview.png', 'survey-results-preview.png', 'remote-preview.png']) assert.ok(existsSync('artifacts/' + name));
    assert.ok((await admin.request('/api/surveys')).some(s => s.title === '교무회의 시간 조사' && s.responseCount === 1));
    assert.equal(JSON.parse(readFileSync('artifacts/native-input-check.json', 'utf8')).physicalInput, !skipNativeInput);
    check('native chat and survey UI, CSV safety, direct TLS screen and input packets, pin and size checks, permission revocation and direct connection termination');
    if (!skipNativeInput) check('actual Windows Korean keyboard input reaches the verification window and is blocked after permission removal');
    else console.log('SKIP actual Windows keyboard injection: explicit --skip-native-input; transport and permission checks still ran.');
    }
  }
  await workChecks({ Client, admin, password, address, data, check, start, stop, ready });
  await officeChecks({Client,admin,password,address,data,check,start,stop,ready});
  writeFileSync('artifacts/test-results.json', JSON.stringify({ verifiedAt: new Date().toISOString(), mode: packaged ? 'self-contained Windows executables' : 'development build', passed, skipped: skipNativeInput ? ['actual Windows keyboard injection: another foreground Windows dialog prevents verification-window focus'] : [], testData: data }, null, 2));
  console.log(`${passed.length} checks passed.`);
} catch(error){writeFileSync(path.join(data,'failure-server.log'),output);throw error;} finally { await stop(); }
