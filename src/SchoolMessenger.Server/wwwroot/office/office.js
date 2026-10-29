'use strict';
const $ = id => document.getElementById(id);
let session, view = 'received', offset = 0, selected, epoch = 0, listRevision = 0, loggingOut = false;
const pending = new Map();
$('updates').onclick=()=>UpdateNotice.show('office',true);
const labels = { received:'받은 메시지', sent:'보낸 메시지', chats:'교직원 채팅', surveys:'설문', todos:'나의 할 일', submissions:'파일 제출' };
const statusNames = { suggested:'추천 · 확인 필요', open:'진행 중', done:'완료', dismissed:'제외' };
const date = n => new Date(n).toLocaleString('ko-KR',{timeZone:'Asia/Seoul'});
function node(tag, text, cls) { const e = document.createElement(tag); if (text !== undefined) e.textContent = text; if(cls)e.className=cls; return e; }
function message(text){ $('feedback').textContent=text; }
function clearSession(){ TimetableUI.reset();UpdateNotice.reset();epoch++;session=null;selected=null;pending.clear();$('office').hidden=true;$('login').hidden=false;$('password').value='';$('list').replaceChildren();$('detail').replaceChildren();$('identity').textContent=''; }
async function api(path, method='GET', body){
  const token=epoch; const headers={}; const options={method,headers,credentials:'same-origin',cache:'no-store',signal:AbortSignal.timeout(90000)};
  if(method!=='GET'){headers['X-CSRF-TOKEN']=session?.csrfToken||'';if(body instanceof FormData)options.body=body;else{headers['Content-Type']='application/json';options.body=JSON.stringify(body||{});}}
  const r=await fetch('/api/'+path,options);
  if(token!==epoch)throw Error('로그인 상태가 바뀌었습니다.');
  if(r.status===401){clearSession();throw Error('로그인이 만료되었습니다. 다시 로그인하세요.');}
  if(!r.ok){let error;try{error=(await r.json()).error;}catch{}throw Error(error||'요청 실패 ('+r.status+')');}
  const text=await r.text();if(token!==epoch)throw Error('로그인 상태가 바뀌었습니다.');return text?JSON.parse(text):null;
}
function safe(action){return async()=>{try{await action();}catch(e){message(e.message);}};}
function button(text,action){const b=node('button',text);b.onclick=safe(async()=>{b.disabled=true;try{await action();}finally{b.disabled=false;}});return b;}
function link(text,path){const a=node('a',text);a.href='/api/'+path;return a;}
function files(list,container){const group=node('div',undefined,'files');for(const f of list)group.append(f.expired?node('span',f.name+' · 파일 만료','muted'):link(f.name+' · '+Math.ceil(f.size/1024)+'KB','attachments/'+encodeURIComponent(f.id)+'/download'));container.append(group);}
function activate(){ $('login').hidden=true;$('office').hidden=false;$('identity').textContent=session.user.name+' · '+session.user.department;$('school-name').textContent=session.schoolName;void UpdateNotice.show('office'); }
async function reload(keep=false){
 if(!session?.user)return;const revision=++listRevision,ticket=epoch,current=view,page=offset;
 try{
  const path=current==='received'||current==='sent'?'messages?box='+current+'&offset='+page:current==='todos'?'todos?offset='+page:current==='submissions'?'submission-requests?offset='+page:current;
  const rows=await api(path);if(ticket!==epoch||current!==view||page!==offset||revision!==listRevision)return;
  $('list').replaceChildren();$('more').hidden=!(rows.length===100&&['received','sent','todos','submissions'].includes(current));
  for(const item of rows){
   const row=node('button',undefined,'row');row.dataset.id=item.id;row.append(node('strong',item.title||item.name));
   let meta=item.senderName?item.senderName+' · '+date(item.createdAt):item.ownerName?item.ownerName+' · '+date(item.deadline):item.members?item.members.map(m=>m.name).join(', '):statusNames[item.status]||'';
   if(current==='submissions')meta+=' · '+(item.ownerId===session.user.id?item.submittedCount+'/'+item.targetCount+'명 제출':item.exempt?'면제':item.submitted?'제출 완료':'미제출');
   if(item.dueDate)meta+=' · '+item.dueDate;
   row.append(node('small',meta));if(item.preview)row.append(node('p',item.preview));row.onclick=safe(()=>show(item));$('list').append(row);
  }
  if(!rows.length)$('list').append(node('div','표시할 항목이 없습니다.','empty'));
  if(keep&&selected){const match=rows.find(x=>x.id===selected.id);if(match)await show(match);else{selected=null;$('detail').replaceChildren();}}
 }finally{ /* Requests from an older view are discarded by revision and session checks. */ }
}
async function changeView(next){view=next;offset=0;selected=null;$('heading').textContent=labels[next];$('detail').replaceChildren(node('div','목록에서 항목을 선택하세요.','empty'));$('add-todo').hidden=next!=='todos';$('hint').textContent=next==='submissions'?'제출 파일은 제출자와 요청한 교사만 열람합니다.':next==='todos'?'받은 메시지에서 찾은 후보를 확인하고 등록하세요.':'메시지 발송·대화·설문 작성은 Windows 앱에서 사용하세요.';for(const b of document.querySelectorAll('[data-view]'))b.classList.toggle('active',b.dataset.view===next);await reload();}
async function show(item){
 const ticket=epoch,current=view;selected=item;const panel=node('div');
 if(view==='todos'){todoForm(item,panel);}
 else if(view==='received'||view==='sent'){
  const d=await api('messages/'+encodeURIComponent(item.id));panel.append(node('h2',d.message.title),node('small',d.message.senderName+' · '+date(d.message.createdAt)),node('div',d.message.body,'body'));files(d.attachments,panel);
  if(view==='received'){await api('messages/'+encodeURIComponent(item.id)+'/read','POST',{});const own=d.recipients.find(r=>r.id===session.user.id);if(own)own.readAt=Date.now();}
  panel.append(node('p',d.recipients.filter(r=>r.readAt).length+'/'+d.recipients.length+'명 확인','muted small'));
  if(d.submissionRequestId)panel.append(button('파일 제출 · 현황 보기',async()=>{await changeView('submissions');await show({id:d.submissionRequestId});}));
 }else if(view==='chats'){
  panel.append(node('h2',item.name));const history=await api('chats/'+encodeURIComponent(item.id)+'/messages');
  const historyPanel=node('div');panel.append(historyPanel);
  function render(entries,prepend=false){for(const entry of (prepend?[...entries].reverse():entries)){const p=node('div',undefined,'chat-entry');p.append(node('small',entry.senderName+' · '+date(entry.createdAt)),node('div',entry.body,'body'));files(entry.attachments,p);prepend?historyPanel.prepend(p):historyPanel.append(p);}}
  render(history);let first=history[0]?.sequence;const older=button('이전 대화',async()=>{const entries=await api('chats/'+encodeURIComponent(item.id)+'/messages?before='+first);render(entries,true);first=entries[0]?.sequence;older.hidden=entries.length<100;});older.hidden=history.length<100;panel.prepend(older);
 }else if(view==='surveys'){
  const d=await api('surveys/'+encodeURIComponent(item.id));panel.append(node('h2',d.title),node('p',d.description,'body'),node('small',date(d.deadline)+(d.closed?' · 종료':'')));
  const form=node('form');const answers=[];
  d.questions.forEach((q,i)=>{const label=node('label',q.text+(q.required?' *':''));const input=node(q.kind==='choice'?'select':'textarea');if(q.kind==='choice'){input.append(new Option('선택하세요',''));for(const choice of q.options)input.append(new Option(choice,choice));}else input.maxLength=2000;input.value=d.answers?.[i]||'';input.required=q.required;input.disabled=d.closed||!d.canAnswer;answers.push(input);label.append(input);form.append(label);});
  if(d.canAnswer&&!d.closed){const submit=node('button','응답 저장','primary');form.append(submit);form.onsubmit=async e=>{e.preventDefault();submit.disabled=true;try{await api('surveys/'+item.id+'/answers','POST',{answers:answers.map(a=>a.value)});message('응답을 저장했습니다.');await show(item);}catch(error){message(error.message);}finally{submit.disabled=false;}};}panel.append(form);
  if(d.ownerId===session.user.id){const result=await api('surveys/'+item.id+'/results');panel.append(node('h2','응답 결과 · '+result.responses.length+'명'));for(const r of result.responses)panel.append(node('p',r.name+': '+r.answers.join(' / '),'evidence'));}
 }else if(view==='submissions'){
  const d=await api('submission-requests/'+encodeURIComponent(item.id));const r=d.request;panel.append(node('h2',r.title),node('div',r.body,'body'),node('p','마감 '+date(r.deadline)+(r.closed?' · 종료':''),'muted small'));
  const mine=r.ownerId===session.user.id;
  if(mine){const actions=node('div',undefined,'actions');actions.append(link('파일 일괄 다운로드 (ZIP)','submission-requests/'+r.id+'/zip'));if(!r.closed)actions.append(button('요청 종료',async()=>{if(!await confirmAction('새 제출과 자동 알림을 종료할까요?'))return;await api('submission-requests/'+r.id+'/close','POST');await reload(true);}));panel.append(actions);}
  for(const target of d.targets){const p=node('div',undefined,'target');p.append(node('strong',target.name+' · '+(target.exempt?'면제':target.submittedAt?target.late?'지각 제출':'제출 완료':'미제출')));if(target.submittedAt)p.append(node('small',date(target.submittedAt)));files(target.files,p);if(mine&&!r.closed)p.append(button(target.exempt?'면제 해제':'제출 면제',async()=>{await api('submission-requests/'+r.id+'/targets/'+target.id,'PATCH',{exempt:!target.exempt});await reload(true);}));panel.append(p);}
  if(!mine&&!r.closed&&!d.targets[0]?.exempt){const form=node('form'),label=node('label','제출 파일 · 최대 10개, 합계 200MB'),input=node('input');input.type='file';input.multiple=true;input.required=!pending.has(r.id);input.disabled=pending.has(r.id);label.append(input);form.append(label,node('p','제출 확정 후 30일간 보관됩니다. 마감 이후에는 지각 제출로 표시합니다.','muted small'));const submit=node('button',pending.has(r.id)?'같은 제출 다시 시도':'파일 제출','primary');form.append(submit);form.onsubmit=async e=>{e.preventDefault();submit.disabled=true;try{let packet=pending.get(r.id);if(!packet){const chosen=[...input.files];if(chosen.length<1||chosen.length>10||chosen.some(f=>f.size>104857600)||chosen.reduce((sum,f)=>sum+f.size,0)>209715200)throw Error('파일 수와 크기를 확인하세요.');const ids=[];for(const f of chosen){const fd=new FormData();fd.append('file',f);ids.push((await api('attachments','POST',fd)).id);}packet={clientId:crypto.randomUUID(),attachmentIds:ids};pending.set(r.id,packet);input.disabled=true;}await api('submission-requests/'+r.id+'/submit','POST',packet);pending.delete(r.id);message('파일을 제출했습니다.');await reload(true);}catch(error){message(error.message);submit.textContent=pending.has(r.id)?'같은 제출 다시 시도':'파일 제출';}finally{submit.disabled=false;}};panel.append(form);}
 }
 if(ticket!==epoch||current!==view||selected?.id!==item.id)return;
 $('detail').replaceChildren(panel);for(const row of $('list').querySelectorAll('.row'))row.classList.toggle('selected',row.dataset.id===item.id);
}
function todoForm(item,panel){
 panel.append(node('h2',item?'할 일 확인 · 수정':'할 일 직접 추가'));if(item?.evidence)panel.append(node('p',item.evidence,'evidence'));if(item?.sourceId)panel.append(button('원본 메시지',async()=>{await changeView('received');await show({id:item.sourceId});}));
 const form=node('form'),title=node('input'),due=node('input'),status=node('select');title.maxLength=200;title.required=true;title.value=item?.title||'';due.type='date';due.value=item?.dueDate||'';for(const [value,text]of Object.entries(statusNames))status.append(new Option(text,value));status.value=item?.status==='suggested'?'open':item?.status||'open';if(!item){status.value='open';status.disabled=true;}
 for(const [text,input]of [['제목',title],['기한 · 선택',due],['상태',status]]){const label=node('label',text);label.append(input);form.append(label);}const submit=node('button',item?.status==='suggested'?'확인 후 등록':'저장','primary');form.append(submit);form.onsubmit=async e=>{e.preventDefault();submit.disabled=true;try{await api('todos'+(item?'/'+item.id:''),item?'PATCH':'POST',{title:title.value,dueDate:due.value||null,status:status.value,revision:item?.revision||0});message('할 일을 저장했습니다.');selected=null;await reload();$('detail').replaceChildren(node('div','저장했습니다.','empty'));}catch(error){message(error.message);}finally{submit.disabled=false;}};panel.append(form);
}
document.querySelectorAll('[data-view]').forEach(b=>b.onclick=safe(()=>changeView(b.dataset.view)));
$('refresh').onclick=safe(()=>reload(true));$('more').onclick=safe(async()=>{offset+=100;selected=null;await reload();});$('add-todo').onclick=()=>{selected=null;$('detail').replaceChildren();todoForm(null,$('detail'));};
$('logout').onclick=safe(async()=>{loggingOut=true;$('office').hidden=true;$('login').hidden=false;$('list').replaceChildren();$('detail').replaceChildren();let failure;try{await api('logout','POST');}catch(error){failure=error;}finally{clearSession();loggingOut=false;}message(failure?'화면은 닫았지만 서버 로그아웃을 확인하지 못했습니다. 연결 상태를 확인하세요.':'로그아웃했습니다. 같은 계정의 다른 로그인도 만료됩니다.');try{const fresh=await api('session');session={...fresh,user:null};}catch{}});
$('login-form').onsubmit=async e=>{e.preventDefault();const b=e.target.querySelector('button');b.disabled=true;try{session=await api('session');await api('login','POST',{username:$('username').value,password:$('password').value,rememberLogin:false});$('password').value='';session=await api('session');activate();message('');await changeView('received');}catch(error){message(error.message);}finally{b.disabled=false;}};
safe(async()=>{session=await api('session');if(session.user){activate();await changeView('received');}})();
async function refreshSession(){if(!session?.user||loggingOut)return;const live=await api('session');if(!live.user){clearSession();message('로그인이 만료되었습니다.');return;}session=live;if(!document.hidden)await reload(false);}
setInterval(safe(refreshSession),30000);
document.addEventListener('visibilitychange',safe(async()=>{if(!document.hidden)await refreshSession();}));

async function confirmAction(text){const dialog=document.createElement('dialog'),form=document.createElement('form');form.method='dialog';form.append(node('h2','제출 요청 종료'),node('p',text));for(const [label,value] of [['취소','cancel'],['종료하기','ok']]){const b=node('button',label);b.value=value;form.append(b);}dialog.append(form);document.body.append(dialog);dialog.showModal();return new Promise(resolve=>dialog.addEventListener('close',()=>{const accepted=dialog.returnValue==='ok';dialog.remove();resolve(accepted);},{once:true}));}

$("timetable").onclick=safe(()=>TimetableUI.open(api,true));
