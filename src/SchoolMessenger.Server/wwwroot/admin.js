let session, editing = null;
const $ = id => document.getElementById(id);
$('updates').onclick=()=>UpdateNotice.show('office',true);
const notice = text => { $('notice').textContent = text; };
async function request(path, method = 'GET', body) {
  const options = { method, credentials: 'same-origin', headers: {} };
  if (method !== 'GET') { options.headers['X-CSRF-TOKEN'] = session.csrfToken; options.headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(body ?? {}); }
  const response = await fetch('/api/' + path, options);
  const text = await response.text(); let data;
  try { data = text ? JSON.parse(text) : null; } catch { data = null; }
  if (!response.ok) {
    if (response.status === 401) { UpdateNotice.reset();$('updates').hidden=true;$('dashboard').hidden = true; $('login-panel').hidden = false; $('logout').hidden = true; }
    throw new Error(data?.error ?? (response.status === 403 ? '관리자 권한이 필요합니다.' : '서버 연결 또는 로그인 상태를 확인하세요.'));
  }
  return data;
}
async function run(action) { try { notice(''); await action(); } catch (error) { notice(error.message); } }
async function initialize() {
  session = await request('session');
  UpdateNotice.reset();$('updates').hidden=true;$('dashboard').hidden = true; $('login-panel').hidden = false; $('logout').hidden = true;
  if (!session.user) return;
  if (!session.user.isAdmin) { notice('관리자 계정으로 로그인하세요.'); return; }
  $('login-panel').hidden = true; $('dashboard').hidden = false; $('logout').hidden = false;
  await load();
  $('updates').hidden=false;void UpdateNotice.show('office');
}
async function load() {
  const [users, status, registrations] = await Promise.all([request('admin/users'), request('admin/status'), request('admin/registrations')]);
  $('registrations').replaceChildren(); $('pending-count').textContent = registrations.length + '건'; $('pending-empty').hidden = registrations.length > 0;
  for (const entry of registrations) {
    const row = document.createElement('tr');
    for (const value of [entry.name, entry.username, entry.department, new Date(entry.createdAt).toLocaleString('ko-KR')]) {
      const cell = document.createElement('td'); cell.textContent = value; row.append(cell);
    }
    const actions = document.createElement('td'); actions.className = 'approval-actions';
    for (const [action, label] of [['approve', '승인'], ['reject', '반려']]) {
      const button = document.createElement('button'); button.textContent = label;
      if (action === 'approve') button.className = 'primary';
      button.addEventListener('click', () => run(async () => {
        if (action === 'reject' && !confirm(entry.name + ' 선생님의 신청을 반려할까요?')) return;
        button.disabled = true;
        try { await request('admin/registrations/' + encodeURIComponent(entry.id) + '/' + action, 'POST'); await load(); notice(entry.name + (action === 'approve' ? ' 선생님의 계정을 승인했습니다.' : ' 선생님의 신청을 반려했습니다.')); }
        finally { button.disabled = false; }
      }));
      actions.append(button);
    }
    row.append(actions); $('registrations').append(row);
  }
  $('users').replaceChildren();
  for (const user of users) {
    const row = document.createElement('tr');
    for (const value of [user.name, user.username, user.department, user.isAdmin ? '관리자' : user.canBroadcast ? '전체 발송' : '교직원', user.active ? '사용 중' : '비활성']) {
      const cell = document.createElement('td'); cell.textContent = value; row.append(cell);
    }
    const cell = document.createElement('td'), edit = document.createElement('button'); edit.textContent = '수정'; edit.addEventListener('click', () => editUser(user)); cell.append(edit); row.append(cell); $('users').append(row);
  }
  $('status').replaceChildren();
  const date = value => value ? new Date(value).toLocaleString('ko-KR') : '아직 실행되지 않음';
  for (const [label, value] of [['현재 접속', `${status.online}명 / 등록 ${status.users}명`], ['첨부 사용량', `${(status.filesBytes / 1024 ** 3).toFixed(2)}GB`], ['디스크 여유', `${(status.freeBytes / 1024 ** 3).toFixed(1)}GB`], ['최근 파일 정리', date(status.lastCleanup)], ['최근 DB 백업', date(status.lastBackup)], ['운영 오류', status.lastError ?? '없음'], ['본문 보관', status.messageRetention]]) {
    const term = document.createElement('dt'), description = document.createElement('dd'); term.textContent = label; description.textContent = value; $('status').append(term, description);
  }
}
function editUser(user) {
  editing = user.id; const form = $('user-form');
  for (const key of ['username', 'name', 'department']) form.elements[key].value = user[key];
  for (const key of ['isAdmin', 'canBroadcast', 'active']) form.elements[key].checked = user[key];
  form.elements.password.value = ''; form.elements.username.disabled = true; $('form-title').textContent = `${user.name} 교직원 수정`; form.scrollIntoView({ behavior: 'smooth', block: 'center' });
}
function resetForm() { editing = null; $('user-form').reset(); $('user-form').elements.username.disabled = false; $('form-title').textContent = '교직원 등록'; }
$('login-form').addEventListener('submit', event => {
  event.preventDefault(); run(async () => {
    const form = event.currentTarget; await request('login', 'POST', { username: form.elements.username.value, password: form.elements.password.value });
    form.elements.password.value = ''; await initialize();
  });
});
$('user-form').addEventListener('submit', event => {
  event.preventDefault(); run(async () => {
    const form = event.currentTarget, data = {};
    for (const key of ['username', 'name', 'department', 'password']) data[key] = form.elements[key].value;
    for (const key of ['isAdmin', 'canBroadcast', 'active']) data[key] = form.elements[key].checked;
    if (!editing && data.password.length < 12) throw new Error('등록 비밀번호는 12자 이상입니다.');
    await request(editing ? 'admin/users/' + editing : 'admin/users', editing ? 'PATCH' : 'POST', data);
    resetForm(); await initialize(); notice('저장했습니다. 수정된 계정은 다시 로그인해야 합니다.');
  });
});
$('cancel-edit').addEventListener('click', resetForm);
$('refresh').addEventListener('click', () => run(load));
$('refresh-registrations').addEventListener('click', () => run(load));
$('maintenance').addEventListener('click', () => run(async () => { await request('admin/maintenance', 'POST'); await load(); notice('파일 정리와 DB 백업을 완료했습니다.'); }));
$('logout').addEventListener('click', () => run(async () => { await request('logout', 'POST'); location.reload(); }));
run(initialize);
