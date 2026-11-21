// 교무톡 수강신청 스크립트입니다. 교무톡이 이 학교를 위해 만든 코드이니 고치지 말고 그대로 붙여넣으세요.
// 학생 이름은 학생 링크 안에만 있는 열쇠로 잠겨 있어 이 계정에서도 읽을 수 없습니다.
var SECRET = '__SECRET__';
var VERSION = __VERSION__;

function doGet() {
  return HtmlService.createHtmlOutput(PAGE).setTitle('수강신청').addMetaTag('viewport', 'width=device-width, initial-scale=1');
}

// Only the school server calls this, with the secret above.
function doPost(e) {
  var body;
  try { body = JSON.parse(e.postData.contents); } catch (x) { return reply_({ error: '요청을 읽지 못했습니다.' }); }
  if (!body || body.secret !== SECRET) return reply_({ error: '연결 열쇠가 맞지 않습니다. 교무톡에서 코드를 다시 복사해 붙여넣고 새 버전으로 배포하세요.' });
  var lock = LockService.getScriptLock();
  lock.waitLock(30000);
  try {
    if (body.action === 'ping') { book_(); return reply_({ ok: true, version: VERSION }); }
    if (body.action === 'publish') {
      var round = body.round;
      keep_('rounds', function (r) { return r[0] !== round.id; });
      append_('rounds', [[round.id, JSON.stringify(round)]]);
      keep_('students', function (r) { return r[1] !== round.id; });
      if (body.students.length) append_('students', body.students.map(function (s) { return [s[0], round.id, s[1]]; }));
      return reply_({ ok: true, count: body.students.length });
    }
    if (body.action === 'results') {
      return reply_({ ok: true, answers: rows_('answers').filter(function (r) { return r[1] === body.round; }).map(function (r) { return [r[0], r[2], Number(r[3])]; }) });
    }
    if (body.action === 'remove') {
      ['rounds', 'students', 'answers'].forEach(function (name) { keep_(name, function (r) { return (name === 'rounds' ? r[0] : r[1]) !== body.round; }); });
      return reply_({ ok: true });
    }
    return reply_({ error: '알 수 없는 요청입니다.' });
  } finally {
    lock.releaseLock();
  }
}

// Called from the student page.
function load(token) {
  var me = find_('students', token);
  var round = me && round_(me[1]);
  if (!round) return { error: 'gone' };
  var answer = find_('answers', token);
  return { title: round.title, closes: round.closes, groups: round.groups, enc: me[2], picks: answer ? JSON.parse(answer[2]) : null, at: answer ? Number(answer[3]) : null, now: Date.now() };
}

function submit(token, picks) {
  var lock = LockService.getScriptLock();
  if (!lock.tryLock(20000)) return { error: 'busy' };
  try {
    var me = find_('students', token);
    var round = me && round_(me[1]);
    if (!round) return { error: 'gone' };
    if (Date.now() >= round.closes) return { error: 'closed' };
    if (!valid_(round.groups, picks)) return { error: 'invalid' };
    var now = Date.now(), row = [[token, me[1], JSON.stringify(picks), String(now)]];
    var hit = finder_('answers', token);
    if (hit) { var range = sheet_('answers').getRange(hit.getRow(), 1, 1, 4); range.setNumberFormat('@'); range.setValues(row); }
    else append_('answers', row);
    return { ok: true, at: now };
  } finally {
    lock.releaseLock();
  }
}

function valid_(groups, picks) {
  if (!Array.isArray(picks) || picks.length !== groups.length) return false;
  return groups.every(function (g, i) {
    var p = picks[i], seen = {};
    if (!Array.isArray(p) || p.length !== g.pick) return false;
    return p.every(function (c) { if (typeof c !== 'string' || g.courses.indexOf(c) < 0 || seen[c]) return false; seen[c] = true; return true; });
  });
}

function reply_(value) { return ContentService.createTextOutput(JSON.stringify(value)).setMimeType(ContentService.MimeType.JSON); }
function book_() {
  var props = PropertiesService.getScriptProperties(), id = props.getProperty('book'), book = null;
  if (id) { try { book = SpreadsheetApp.openById(id); } catch (x) { book = null; } }
  if (!book) { book = SpreadsheetApp.create('교무톡 수강신청 자료 (지우지 마세요)'); props.setProperty('book', book.getId()); }
  ['rounds', 'students', 'answers'].forEach(function (n) { if (!book.getSheetByName(n)) book.insertSheet(n); });
  return book;
}
function sheet_(name) { return book_().getSheetByName(name); }
function rows_(name) { var s = sheet_(name), n = s.getLastRow(); return n ? s.getRange(1, 1, n, s.getLastColumn()).getValues() : []; }
// Text format keeps tokens and numbers exactly as sent.
function append_(name, values) { var s = sheet_(name), range = s.getRange(s.getLastRow() + 1, 1, values.length, values[0].length); range.setNumberFormat('@'); range.setValues(values); }
function keep_(name, test) {
  var all = rows_(name), kept = all.filter(test);
  if (kept.length === all.length) return;
  sheet_(name).clearContents();
  if (kept.length) append_(name, kept);
}
function finder_(name, token) {
  if (typeof token !== 'string' || !/^[A-Za-z0-9_-]{22}$/.test(token)) return null;
  return sheet_(name).getRange('A:A').createTextFinder(token).matchCase(true).matchEntireCell(true).findNext();
}
function find_(name, token) { var hit = finder_(name, token); return hit ? sheet_(name).getRange(hit.getRow(), 1, 1, name === 'answers' ? 4 : 3).getValues()[0] : null; }
function round_(id) { var hit = rows_('rounds').filter(function (r) { return r[0] === id; })[0]; return hit ? JSON.parse(hit[1]) : null; }

var PAGE = String.raw`<!doctype html>
<html lang="ko"><head><meta charset="utf-8">
<style>
:root{--blue:#2463EB;--blue-soft:#EAF1FE;--ink:#172033;--muted:#5B6475;--faint:#8A93A3;--line:#E3E8F0;--bg:#F5F7FB;--card:#fff;--ok:#16794C;--ok-soft:#E7F6EE;--warn:#B45309;--warn-soft:#FEF3E2;--bad:#C2410C}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.5 "Pretendard","Apple SD Gothic Neo","Malgun Gothic",system-ui,sans-serif;-webkit-text-size-adjust:100%}
main{max-width:560px;margin:0 auto;padding:16px 16px 120px}
.brand{display:flex;align-items:center;gap:8px;color:var(--muted);font-size:14px;font-weight:600;margin:4px 0 14px}
.brand svg{width:24px;height:24px;display:block}
.card{background:var(--card);border:1px solid var(--line);border-radius:16px;padding:18px;margin-bottom:12px}
h1{font-size:21px;line-height:1.35;margin:0 0 6px}
.who{font-size:15px;font-weight:600}
.meta{color:var(--muted);font-size:14px;margin-top:4px}
.note{border-radius:12px;padding:12px 14px;font-size:14px;margin-bottom:12px}
.note.ok{background:var(--ok-soft);color:var(--ok)}
.note.warn{background:var(--warn-soft);color:var(--warn)}
.group h2{font-size:17px;margin:0;display:flex;justify-content:space-between;align-items:baseline;gap:8px}
.group h2 span{font-size:14px;font-weight:600;color:var(--blue);white-space:nowrap}
.group h2 span.done{color:var(--ok)}
.group p{margin:2px 0 12px;color:var(--muted);font-size:14px}
label.opt{display:flex;align-items:center;gap:12px;min-height:52px;padding:10px 14px;border:1px solid var(--line);border-radius:12px;margin-top:8px;cursor:pointer}
label.opt input{width:22px;height:22px;accent-color:var(--blue);flex:none;margin:0}
label.opt.on{border-color:var(--blue);background:var(--blue-soft);font-weight:600}
label.opt.off{opacity:.45}
.bar{position:fixed;left:0;right:0;bottom:0;background:rgba(255,255,255,.96);border-top:1px solid var(--line);padding:12px 16px calc(12px + env(safe-area-inset-bottom))}
.bar div{max-width:560px;margin:0 auto}
.bar small{display:block;color:var(--muted);font-size:13px;margin-bottom:8px;text-align:center}
.bar small.ok{color:var(--ok);font-weight:700}
button{width:100%;height:52px;border:0;border-radius:12px;background:var(--blue);color:#fff;font:inherit;font-weight:700;font-size:17px}
button:disabled{background:#C9D3E3}
.center{text-align:center;padding:48px 8px;color:var(--muted)}
.spin{width:28px;height:28px;border:3px solid var(--line);border-top-color:var(--blue);border-radius:50%;margin:0 auto 14px;animation:s 0.8s linear infinite}
@keyframes s{to{transform:rotate(360deg)}}
</style></head>
<body><main id="app"><div class="center"><div class="spin"></div>불러오는 중…</div></main>
<script>
var MARK = '<svg viewBox="0 0 128 128" aria-hidden="true"><rect width="128" height="128" rx="29" fill="#2463EB"/><path d="M64 36V14" fill="none" stroke="#fff" stroke-width="4" stroke-linecap="round"/><path d="M66 13H85L79.5 19L85 25H66Z" fill="#FFC94A"/><path d="M32 64H96V88Q96 98 86 98H60L42 111V98H42Q32 98 32 88Z" fill="#fff" stroke="#fff" stroke-width="8" stroke-linejoin="round"/><path d="M22 64L64 37L106 64Z" fill="#A9D9EC" stroke="#A9D9EC" stroke-width="6" stroke-linejoin="round"/><circle cx="49" cy="82" r="5.5" fill="#2463EB"/><circle cx="64" cy="82" r="5.5" fill="#2463EB"/><circle cx="79" cy="82" r="5.5" fill="#2463EB"/></svg>';
var app = document.getElementById('app'), token = '', key = '', data = null, picks = [], saved = '';
function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
// A round closes at midnight Korean time, so its deadline is the day before that moment.
function until(ms) { return new Date(ms - 1).toLocaleDateString('ko-KR', { timeZone: 'Asia/Seoul', month: 'long', day: 'numeric', weekday: 'short' }) + ' 밤 12시까지'; }
function when(ms) { return new Date(ms).toLocaleString('ko-KR', { timeZone: 'Asia/Seoul', month: 'long', day: 'numeric', weekday: 'short', hour: '2-digit', minute: '2-digit' }); }
function stop(title, text) { app.innerHTML = ''; var c = el('div', 'center'); c.appendChild(el('h1', null, title)); c.appendChild(el('div', null, text)); app.appendChild(c); }
function bytes(s) { s = s.replace(/-/g, '+').replace(/_/g, '/'); while (s.length % 4) s += '='; var b = atob(s), out = new Uint8Array(b.length); for (var i = 0; i < b.length; i++) out[i] = b.charCodeAt(i); return out; }
// The server's slots are limited; a busy moment is retried quietly a few times before asking the student to try again.
function call(name, args, done, fail, tries) {
  tries = tries || 0;
  var run = google.script.run.withSuccessHandler(function (r) { if (r && r.error === 'busy' && tries < 5) setTimeout(function () { call(name, args, done, fail, tries + 1); }, 1200 * Math.pow(1.6, tries)); else done(r); })
    .withFailureHandler(function () { if (tries < 5) setTimeout(function () { call(name, args, done, fail, tries + 1); }, 1200 * Math.pow(1.6, tries)); else fail(); });
  run[name].apply(run, args);
}
google.script.url.getLocation(function (loc) {
  token = (loc.parameter && loc.parameter.t) || ''; key = (loc.hash || '').replace(/^#/, '');
  if (!/^[A-Za-z0-9_-]{22}$/.test(token) || !/^[A-Za-z0-9_-]{22}$/.test(key)) { stop('링크가 잘렸어요', '받은 링크 전체를 그대로 눌러 다시 열어 주세요.'); return; }
  call('load', [token], start, function () { stop('연결이 원활하지 않아요', '잠시 후 이 화면을 새로 고쳐 주세요.'); });
});
function start(r) {
  if (!r || r.error) { stop('열 수 없는 링크예요', '신청이 끝났거나 링크가 바뀌었습니다. 담당 선생님께 새 링크를 받아 주세요.'); return; }
  data = r; var raw = bytes(r.enc);
  crypto.subtle.importKey('raw', bytes(key), 'AES-GCM', false, ['decrypt'])
    .then(function (k) { return crypto.subtle.decrypt({ name: 'AES-GCM', iv: raw.slice(0, 12) }, k, raw.slice(12)); })
    .then(function (plain) { draw(JSON.parse(new TextDecoder().decode(plain))); })
    .catch(function () { stop('링크가 잘렸어요', '받은 링크 전체를 그대로 눌러 다시 열어 주세요.'); });
}
function draw(me) {
  picks = data.groups.map(function (g, i) { return data.picks && data.picks[i] ? data.picks[i].slice() : []; });
  saved = data.picks ? JSON.stringify(data.picks) : '';
  var closed = data.now >= data.closes;
  app.innerHTML = '';
  var brand = el('div', 'brand'); brand.innerHTML = MARK; brand.appendChild(document.createTextNode('수강신청')); app.appendChild(brand);
  var head = el('div', 'card'); head.appendChild(el('h1', null, data.title));
  head.appendChild(el('div', 'who', me.g + '학년 ' + me.c + '반 ' + me.no + '번 ' + me.n));
  head.appendChild(el('div', 'meta', until(data.closes) + (closed ? ' · 마감되었습니다' : ' · 마감 전까지 바꿀 수 있어요')));
  app.appendChild(head);
  var status = el('div', 'note'); status.id = 'status'; app.appendChild(status);
  data.groups.forEach(function (g, gi) {
    var card = el('div', 'card group'), h = el('h2', null, g.name), count = el('span'); h.appendChild(count); card.appendChild(h);
    card.appendChild(el('p', null, g.courses.length + '과목 중 ' + g.pick + '과목을 고르세요'));
    var boxes = [];
    g.courses.forEach(function (name) {
      var row = el('label', 'opt'), box = el('input'); box.type = 'checkbox'; box.checked = picks[gi].indexOf(name) >= 0; box.disabled = closed;
      row.appendChild(box); row.appendChild(el('span', null, name)); card.appendChild(row); boxes.push({ box: box, row: row, name: name });
      box.addEventListener('change', function () {
        var list = picks[gi], at = list.indexOf(name);
        if (box.checked && at < 0) list.push(name); else if (!box.checked && at >= 0) list.splice(at, 1);
        if (g.pick === 1 && box.checked) { picks[gi] = [name]; }
        paint();
      });
    });
    function paintGroup() {
      var full = picks[gi].length >= g.pick;
      boxes.forEach(function (b) { var on = picks[gi].indexOf(b.name) >= 0; b.box.checked = on; b.row.className = 'opt' + (on ? ' on' : full && g.pick > 1 && !closed ? ' off' : ''); b.box.disabled = closed || (full && !on && g.pick > 1); });
      count.textContent = picks[gi].length + ' / ' + g.pick; count.className = picks[gi].length === g.pick ? 'done' : '';
    }
    card.paint = paintGroup; app.appendChild(card);
  });
  var bar = el('div', 'bar'), inner = el('div'), hint = el('small'), send = el('button', null, '제출하기'); send.id = 'send';
  inner.appendChild(hint); inner.appendChild(send); bar.appendChild(inner); if (!closed) document.body.appendChild(bar);
  send.addEventListener('click', function () {
    send.disabled = true; send.textContent = '제출하는 중…';
    call('submit', [token, picks], function (r) {
      if (r && r.ok) { saved = JSON.stringify(picks); data.at = r.at; paint(); return; }
      var why = r && r.error === 'closed' ? '신청이 마감되었습니다.' : r && r.error === 'gone' ? '링크가 바뀌었습니다. 담당 선생님께 새 링크를 받아 주세요.' : '선택이 바뀌었습니다. 화면을 새로 고친 뒤 다시 골라 주세요.';
      note('warn', why); send.textContent = '제출하기'; send.disabled = false;
    }, function () { note('warn', '제출하지 못했어요. 잠시 후 다시 눌러 주세요.'); send.textContent = '제출하기'; send.disabled = false; });
  });
  function paint() {
    Array.prototype.forEach.call(document.querySelectorAll('.group'), function (c) { c.paint(); });
    var missing = data.groups.map(function (g, i) { return g.pick - picks[i].length; });
    var first = missing.findIndex(function (m) { return m !== 0; });
    var changed = JSON.stringify(picks) !== saved;
    if (saved && !changed) note('ok', '제출했습니다 · ' + when(data.at)); else if (saved) note('warn', '바뀐 선택은 아직 제출하지 않았어요.'); else note('', '');
    var done = saved && !changed;
    send.disabled = first >= 0 || !changed; send.textContent = done ? '제출 완료' : saved ? '바뀐 내용 제출하기' : '제출하기';
    hint.className = done ? 'ok' : '';
    hint.textContent = done ? '✓ 제출했습니다 · ' + when(data.at) : first >= 0 ? (missing[first] > 0 ? data.groups[first].name + '에서 ' + missing[first] + '과목 더 골라 주세요' : data.groups[first].name + '에서 ' + (-missing[first]) + '과목을 빼 주세요') : '모두 골랐어요';
  }
  paint();
  if (closed) note(saved ? 'ok' : 'warn', saved ? '마감되었습니다 · 제출한 내용입니다' : '마감되었습니다 · 제출하지 않았어요');
}
function note(kind, text) { var s = document.getElementById('status'); s.className = 'note ' + kind; s.textContent = text; s.style.display = text ? '' : 'none'; }
</script></body></html>`;
