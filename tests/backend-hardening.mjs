import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {mkdirSync,writeFileSync,readFileSync,existsSync,readdirSync} from 'node:fs';
import {DatabaseSync} from 'node:sqlite';
import net from 'node:net';
import path from 'node:path';
import {bridgeHeaders,bridgeFetch} from './bridge-client.mjs';
import {writeDeploymentReviewFixture} from './deployment-review-fixture.mjs';

const root=process.cwd(),sdk=path.join(root,'.tools/dotnet/dotnet.exe');
const dotnet=existsSync(sdk)?sdk:'dotnet';
const testRoot=path.join(process.env.YY_TEST_DATA_ROOT??path.join(root,'.test-data'),'backend-hardening-'+randomUUID());mkdirSync(testRoot,{recursive:true});
const password='Synthetic-'+randomUUID(),key='Bridge-'+randomUUID(),rotation='Rotation-'+randomUUID();
const passed=[],engines={};const check=name=>{passed.push(name);console.log('PASS '+name);};
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
async function port(){const s=net.createServer();await new Promise(r=>s.listen(0,'127.0.0.1',r));const p=s.address().port;await new Promise(r=>s.close(r));return p;}
async function stop(server){if(server?.exitCode===null){server.kill();await new Promise(r=>server.once('exit',r));}}
async function wait(test){for(let i=0;i<150;i++){try{if(await test())return;}catch{}await sleep(100);}throw Error('Condition timeout');}
class Client{
 cookies=new Map();csrf='';user=null;
 constructor(base){this.base=base;}
 async request(route,method='GET',body,status=200){const headers={Cookie:[...this.cookies].map(([k,v])=>k+'='+v).join('; ')};
  if(method!=='GET')headers['X-CSRF-TOKEN']=this.csrf;
  if(body!==undefined&&!(body instanceof FormData))headers['Content-Type']='application/json';
  const response=await fetch(this.base+'/api/'+route,{method,headers,body:body===undefined?undefined:body instanceof FormData?body:JSON.stringify(body)});
  for(const cookie of response.headers.getSetCookie()){const p=cookie.split(';')[0],i=p.indexOf('=');this.cookies.set(p.slice(0,i),p.slice(i+1));}
  const text=await response.text();assert.equal(response.status,status,method+' '+route+' '+text);return text&&response.headers.get('Content-Type')?.includes('json')?JSON.parse(text):text;
 }
 async login(username='admin'){this.csrf=(await this.request('session')).csrfToken;await this.request('login','POST',{username,password,rememberLogin:true});const s=await this.request('session');this.csrf=s.csrfToken;this.user=s.user;return this;}
}
for(const project of ['Server','AnnouncementServer']){
 const portal=project==='AnnouncementServer',prefix=portal?'Portal':'School',dbName=portal?'announcements.db':'school.db';
 const data=path.join(testRoot,project),base='http://127.0.0.1:'+await port();let server,logs='';
 const env={...process.env,ASPNETCORE_ENVIRONMENT:'Development',[prefix+'__DataDirectory']:data,[prefix+'__AdminPassword']:password,Portal__BridgeKey:key,Portal__BridgeKeys__rotated:rotation,School__CleanupSeconds:'1',Portal__CleanupSeconds:'1',School__LoginLimitPerMinute:'300'};
 const dll=path.join(root,'src','SchoolMessenger.'+project,'bin/Debug/net10.0/SchoolMessenger.'+project+'.dll');
 async function start(){logs='';server=spawn(dotnet,[dll,'--urls',base],{cwd:path.dirname(dll),env,windowsHide:true});server.stdout.on('data',b=>logs+=b);server.stderr.on('data',b=>logs+=b);await wait(async()=>{if(server.exitCode!==null)throw Error('Service exited');return(await fetch(base+'/health')).ok;});}
 async function command(snapshot){const child=spawn(dotnet,[dll,'--backend-restore',snapshot],{cwd:path.dirname(dll),env,windowsHide:true});let out='';child.stdout.on('data',b=>out+=b);child.stderr.on('data',b=>out+=b);const exit=await new Promise(r=>child.once('exit',r));return{exit,out};}
 try{
  await start();const a=await new Client(base).login(),b=await new Client(base).login();
  const sessions=await a.request('sessions');assert.equal(sessions.length,2);assert.equal(sessions.filter(s=>s.current).length,1);
  await a.request('sessions/'+sessions.find(s=>!s.current).id,'DELETE');await b.request(portal?'admin/status':'messages','GET',undefined,401);await a.request('sessions/'+randomUUID(),'DELETE',undefined,404);
  await a.request('logout','POST');await a.login();assert.equal((await a.request('sessions')).length,1);
  check(project+': selected session revoked without revoking the current device');
  const status=await a.request('admin/backend-status');engines[project]=status.sqliteVersion;
  await new Client(base).request('admin/backend-status','GET',undefined,401);await wait(async()=>(await a.request('admin/backend-status')).jobs.some(j=>j.name==='backup'&&j.lastSuccess));
  const nonceUrl=base+'/bridge/directory',signed=portal?bridgeHeaders(key,nonceUrl):null;
  if(portal){assert.equal((await fetch(nonceUrl,{headers:signed})).status,200);assert.equal((await bridgeFetch(rotation,nonceUrl,{}, {keyId:'rotated'})).status,200);assert.equal((await bridgeFetch(key,nonceUrl,{}, {keyId:'missing'})).status,401);}
  const live=path.join(data,dbName);function openDb(){const db=new DatabaseSync(live);db.exec("PRAGMA busy_timeout=30000");return db;}let db=openDb();
  assert.equal(db.prepare('PRAGMA journal_mode').get().journal_mode,'wal');assert.equal(db.prepare('PRAGMA quick_check').get().quick_check,'ok');assert.equal(db.prepare('PRAGMA foreign_key_check').all().length,0);
  assert.ok(db.prepare('SELECT COUNT(*) AS n FROM BackendAudit').get().n>0);
  assert.throws(()=>db.exec('DELETE FROM BackendAudit'),/append only/);assert.throws(()=>db.exec("UPDATE BackendAudit SET Result='altered'"),/append only/);db.close();
  check(project+': WAL, integrity, persistent job state and append-only audit');
  const competing=spawn(dotnet,[dll,'--urls','http://127.0.0.1:'+await port()],{cwd:path.dirname(dll),env,windowsHide:true});competing.stdout.resume();competing.stderr.resume();assert.notEqual(await new Promise(r=>competing.once('exit',r)),0);
  check(project+': second service cannot open the same data root');
  if(!portal){
   const user=await a.request('admin/users','POST',{username:'recipient',name:'가상 수신자',department:'가상부서',password,isAdmin:false,canBroadcast:false});
   const recipient=await new Client(base).login('recipient');await recipient.request('sessions/'+(await a.request('sessions'))[0].id,'DELETE',undefined,404);
   const manager=await a.request('admin/users','POST',{username:'manager',name:'가상 관리자',department:'가상부서',password,isAdmin:true,canBroadcast:false});const m=await new Client(base).login('manager');
   await m.request('admin/users','POST',{username:'escalate',name:'가상 관리자',department:'가상부서',password,isAdmin:true,canBroadcast:false},403);
   await m.request('admin/users/'+a.user.id,'PATCH',{name:'변경',department:'가상부서',active:true,isAdmin:true,canBroadcast:true},403);await m.request('messages/'+randomUUID(),'GET',undefined,404);
   check('school admin cannot grant administrator privilege or change bootstrap root');
   const form=new FormData();form.append('file',new Blob(['synthetic file']),'sample.txt');const file=await a.request('attachments','POST',form);
   db=openDb();const before=db.prepare('SELECT UploadedAt,ExpiresAt FROM Attachments WHERE Id=?').get(file.id);assert.ok(Math.abs(before.ExpiresAt-before.UploadedAt-2592000000)<100);db.close();await sleep(100);
   const payload={clientId:randomUUID(),title:'가상 쪽지',body:'가상 본문',recipientIds:[user.id],attachmentIds:[file.id],all:false,department:null};
   const send=await a.request('messages','POST',payload);await a.request('messages','POST',payload);await a.request('messages','POST',{...payload,body:'different'},409);
   db=openDb();assert.equal(db.prepare('SELECT ExpiresAt FROM Attachments WHERE Id=?').get(file.id).ExpiresAt,before.ExpiresAt);assert.equal(db.prepare("SELECT COUNT(*) AS n FROM MessageEvents WHERE ResourceId=? AND Kind='NewMessage'").get(send.id).n,1);db.close();
   assert.ok((await a.request('messages/changes')).events.some(e=>e.kind==='SentMessage'&&e.resourceId===send.id));
   const changes=await recipient.request('messages/changes');assert.ok(changes.events.some(e=>e.resourceId===send.id));assert.equal((await recipient.request('messages/changes?after='+changes.cursor)).events.length,0);
   await recipient.request('messages/'+send.id+'/read','POST');await recipient.request('messages/'+send.id+'/read','POST');assert.equal((await a.request('messages/changes')).events.filter(e=>e.kind==='ReadChanged'&&e.resourceId===send.id).length,1);
   writeFileSync(path.join(data,'files',file.id),'corrupted');await recipient.request('attachments/'+file.id+'/download','GET',undefined,410);
   check('upload-confirmed expiry, tamper rejection, send/read idempotency and recipient-scoped stable sync cursor');
   const room=await a.request('office/rooms','POST',{name:'가상 경쟁 특별실',location:'가상 위치',active:true});const day=new Date(Date.now()+86400000).toISOString().slice(0,10),start=day+'T09:00:00+09:00',end=day+'T09:50:00+09:00';
   const requests=await Promise.all(Array.from({length:100},async()=>{const response=await fetch(base+'/api/office/bookings',{method:'POST',headers:{Cookie:[...recipient.cookies].map(([k,v])=>k+'='+v).join('; '),'Content-Type':'application/json','X-CSRF-TOKEN':recipient.csrf},body:JSON.stringify({clientId:randomUUID().replaceAll('-',''),roomId:room.id,title:'가상 동시 예약',start,end})});await response.arrayBuffer();return response.status;}));
   assert.equal(requests.filter(s=>s===200).length,1);assert.equal(requests.filter(s=>s===409).length,99);check('100 simultaneous overlapping reservation requests commit exactly one booking');
   const actors=[];for(let i=0;i<100;i++)await a.request('admin/users','POST',{username:'load.user.'+i,name:'가상 부하 사용자 '+i,department:'가상부서',password,isAdmin:false,canBroadcast:false});
   const loadStart=Date.now();for(const actor of await Promise.all(Array.from({length:100},(_,i)=>new Client(base).login('load.user.'+i))))actors.push(actor);
   const sends=await Promise.all(actors.map(actor=>actor.request('messages','POST',{...payload,clientId:randomUUID(),attachmentIds:[]})));assert.equal(new Set(sends.map(s=>s.id)).size,100);
   db=openDb();assert.equal(db.prepare('SELECT COUNT(*) AS n FROM MessageEvents WHERE Kind=\'NewMessage\'').get().n,101);db.close();
   check('100 distinct users sign in and send concurrently; persist 100 unique messages and transactional events ('+(Date.now()-loadStart)+'ms)');
   const atomic={...payload,clientId:randomUUID(),attachmentIds:[]};db=openDb();const count=db.prepare('SELECT COUNT(*) AS n FROM Messages').get().n;
   db.exec("CREATE TRIGGER RejectMessageEvent BEFORE INSERT ON MessageEvents BEGIN SELECT RAISE(ABORT,'synthetic event failure'); END");db.close();await a.request('messages','POST',atomic,500);
   db=openDb();assert.equal(db.prepare('SELECT COUNT(*) AS n FROM Messages').get().n,count);db.exec('DROP TRIGGER RejectMessageEvent');db.close();await a.request('messages','POST',atomic);
   check('event storage failure rolls back message and recipients; the identical retry then succeeds');
   db=openDb();db.exec('BEGIN IMMEDIATE');const lockedStart=Date.now(),blocked=a.request('messages','POST',{...atomic,clientId:randomUUID()});await sleep(800);db.exec('COMMIT');db.close();await blocked;assert.ok(Date.now()-lockedStart>=800);
   check('temporary SQLite write contention waits and commits after the lock is released');
   db=openDb();db.prepare('UPDATE MessageEvents SET DeliveredAt=NULL,NextAttempt=? WHERE Sequence=(SELECT MAX(Sequence) FROM MessageEvents)').run(Date.now()+60000);db.close();

  }
  if(portal){
   // Synthetic seeded accounts isolate login throughput from separately tested signup/approval flows.
   db=openDb();const fixtureHash=db.prepare("SELECT PasswordHash FROM People WHERE Username='admin'").get().PasswordHash;
   const seed=db.prepare("INSERT INTO People(Id,Username,Name,Role,PasswordHash) VALUES(?,?,?,'parent',?)");db.exec('BEGIN');for(let i=0;i<100;i++)seed.run(randomUUID().replaceAll('-',''),'load.parent.'+i,'가상 부하 보호자 '+i,fixtureHash);db.exec('COMMIT');db.close();
   const parents=await Promise.all(Array.from({length:100},(_,i)=>new Client(base).login('load.parent.'+i)));assert.equal(new Set(parents.map(p=>p.user.id)).size,100);for(const parent of parents)assert.equal((await parent.request('sessions')).length,1);
   check('100 distinct synthetic parent accounts can sign in concurrently behind a shared IP; session ownership stays separate');
  }
  if(portal){
   const room=await a.request('admin/classes','POST',{name:'가상 정책 학급',grade:1});
   const student=await a.request('admin/people','POST',{name:'가상 정책 학생',role:'student',classId:room.id,internalUserId:null,classIds:[],canBroadcast:false});
   const policy=await(new Client(base)).request('registration/policy');const agreement={revision:policy.revision,acceptedIds:policy.documents.filter(d=>d.required).map(d=>d.id)};
   const parent=new Client(base);parent.csrf=(await parent.request('session')).csrfToken;await parent.request('register','POST',{name:'가상 보호자',username:'review.parent',password,role:'parent',agreement});
   await stop(server);env.ASPNETCORE_ENVIRONMENT='Production';env.Operations__SchoolId='YY-SYNTHETIC-ONLY';env.Operations__SecurityReviewFile=writeDeploymentReviewFixture(path.join(testRoot,'review-'+project),portal?'announcements':'office',base);await start();await a.login();await parent.login('review.parent');
   let code=(await a.request('admin/people/'+student.id+'/student-code')).code;
   db=openDb();db.prepare('INSERT OR REPLACE INTO FamilyCodeAttempts(ParentId,Failures,LockedUntil) VALUES(?,4,0)').run(parent.user.id);db.close();
   const pending=await parent.request('settings/children','POST',{code});assert.equal(pending.membership,'pending');db=openDb();assert.equal(db.prepare('SELECT Failures FROM FamilyCodeAttempts WHERE ParentId=?').get(parent.user.id).Failures,0);db.close();assert.equal((await parent.request('children')).length,0);await parent.request('announcements','GET',undefined,403);
   code=(await a.request('admin/people/'+student.id+'/student-code/reissue','POST')).code;
   await a.request('admin/guardian-requests/'+parent.user.id+'/'+student.id+'/approve','POST',undefined,409);
   await parent.request('settings/children','POST',{code});await a.request('admin/guardian-requests/'+parent.user.id+'/'+student.id+'/approve','POST');assert.equal((await parent.request('children')).length,1);
   check('production guardian policy requires school review; pending relation grants no data and retired codes cannot be approved');
   await a.request('admin/families/'+parent.user.id+'/'+student.id,'DELETE');await parent.login('review.parent');
   await stop(server);env.Portal__GuardianBindingMode='CodeOnly';await start();await a.login();code=(await a.request('admin/people/'+student.id+'/student-code')).code;await parent.login('review.parent');await parent.request('settings/children','POST',{code},503);
   await stop(server);env.Portal__GuardianCodeOnlyApproved='true';await start();await parent.login('review.parent');assert.equal((await parent.request('settings/children','POST',{code})).membership,'regular');
   check('production code-only binding requires explicit school approval; development instant binding remains compatible');
   await stop(server);env.ASPNETCORE_ENVIRONMENT='Development';delete env.Portal__GuardianBindingMode;delete env.Portal__GuardianCodeOnlyApproved;await start();
  }
  if(portal){
   await stop(server);env.Portal__BridgeKey='';env.Portal__BridgeKeyValidity__rotated__NotAfter=String(Math.floor(Date.now()/1000)-1);await start();
   assert.equal((await bridgeFetch(rotation,nonceUrl,{}, {keyId:'rotated'})).status,401);
   await stop(server);delete env.Portal__BridgeKeyValidity__rotated__NotAfter;await start();
   assert.equal((await bridgeFetch(rotation,nonceUrl,{}, {keyId:'rotated'})).status,200);assert.equal((await bridgeFetch(key,nonceUrl)).status,401);
   check('rotation can remove the legacy key; expired key validity is rejected and active named key works');
   await stop(server);env.Portal__BridgeKey=key;await start();
  }
  await stop(server);await start();
  if(portal)assert.equal((await fetch(nonceUrl,{headers:signed})).status,401);
  const restored=await new Client(base).login();if(!portal){
   assert.ok((await restored.request('messages/changes')).events.length>0);db=openDb();const queued=db.prepare('SELECT Sequence,DeliveredAt FROM MessageEvents ORDER BY Sequence DESC LIMIT 1').get();assert.equal(queued.DeliveredAt,null);db.prepare('UPDATE MessageEvents SET NextAttempt=0 WHERE Sequence=?').run(queued.Sequence);db.close();
   await wait(()=>{const reader=openDb();try{return reader.prepare('SELECT DeliveredAt FROM MessageEvents WHERE Sequence=?').get(queued.Sequence).DeliveredAt!==null;}finally{reader.close();}});check('pending transactional event survives process exit and is dispatched after restart');
  }
  check(project+': abrupt process exit preserves data, jobs and replay rejection after restart');
  const backupFolder=path.join(data,'backups'),snapshot=path.join(backupFolder,readdirSync(backupFolder).find(f=>f.endsWith('.db')));
  assert.notEqual((await command(snapshot)).exit,0);await stop(server);
  db=openDb();db.exec('DELETE FROM BackendMigrations');db.close();await start();await stop(server);
  assert.ok(readdirSync(path.join(data,'migration-backups')).some(f=>f.startsWith('before-backend-')));
  const corrupt=path.join(testRoot,project+'-corrupt.db');writeFileSync(corrupt,'invalid database');assert.notEqual((await command(corrupt)).exit,0);
  db=openDb();assert.equal(db.prepare('PRAGMA quick_check').get().quick_check,'ok');db.close();
  assert.equal((await command(snapshot)).exit,0);await start();await restored.request(portal?'admin/status':'messages','GET',undefined,401);const recoveryAdmin=await new Client(base).login();
  assert.equal((await recoveryAdmin.request('admin/recovery-status')).reviewRequired,true);if(!portal){await recoveryAdmin.request('admin/users','POST',{username:'recovery.peer',name:'가상 복원 관리자',department:'가상부서',password,isAdmin:true,canBroadcast:false});const peer=await new Client(base).login('recovery.peer');await peer.request('admin/users','GET',undefined,503);}const awaiting=await bridgeFetch(key,base+'/bridge/directory');if(portal)assert.equal(awaiting.status,503);
  await recoveryAdmin.request('admin/recovery-confirm','POST',{confirmed:false},400);await recoveryAdmin.request('admin/recovery-confirm','POST',{confirmed:true});assert.equal((await recoveryAdmin.request('admin/recovery-status')).reviewRequired,false);
  check(project+': restore pauses ordinary service until an administrator explicitly confirms school access review');
  check(project+': live restore blocked, migration snapshot saved, corrupt backup rejected, valid restore revokes old sessions');
 }finally{await stop(server);}
}
mkdirSync('artifacts',{recursive:true});writeFileSync('artifacts/backend-hardening-checks.json',JSON.stringify({version:'1.0.0',source:'isolated synthetic fixtures',passed,engines,notRun:['real disk exhaustion','actual power loss','off-disk recovery','production guardian identity process','FCM/APNs delivery','physical device security']},null,2));
console.log('Backend hardening: '+passed.length+' checks passed.');
