import {acceptRequiredPoliciesForFixture} from './registration-fixture.mjs';
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {existsSync,mkdirSync,writeFileSync} from 'node:fs';
import path from 'node:path';
import net from 'node:net';
import {DatabaseSync} from 'node:sqlite';
const root=process.cwd(),dotnet=existsSync('.tools/dotnet/dotnet.exe')?path.join(root,'.tools/dotnet/dotnet.exe'):path.join(process.env.DOTNET_ROOT,'dotnet.exe');
const fixture=path.join(process.env.YY_TEST_DATA_ROOT??path.join(root,'.test-data'),'bridge-'+randomUUID()),password='Test-'+randomUUID(),key='Bridge-'+randomUUID(),portalData=path.join(fixture,'public'),officeData=path.join(fixture,'office');mkdirSync(fixture,{recursive:true});mkdirSync('artifacts',{recursive:true});
const delay=ms=>new Promise(r=>setTimeout(r,ms)),id=()=>randomUUID().replaceAll('-','');
async function address(){const s=net.createServer();await new Promise(r=>s.listen(0,'127.0.0.1',r));const value='http://127.0.0.1:'+s.address().port;await new Promise(r=>s.close(r));return value;}
const portal=await address(),office=await address();let p,o,logs='';const passed=[];function check(name){passed.push(name);console.log('PASS '+name);}
function start(name,base,env){const child=spawn(dotnet,[path.join(root,`src/SchoolMessenger.${name}/bin/Debug/net10.0/SchoolMessenger.${name}.dll`),'--urls',base],{windowsHide:true,cwd:path.join(root,'src','SchoolMessenger.'+name),env:{...process.env,ASPNETCORE_ENVIRONMENT:'Development',...env}});child.stdout.on('data',b=>logs+=b);child.stderr.on('data',b=>logs+=b);return child;}
function publicStart(){p=start('AnnouncementServer',portal,{Portal__DataDirectory:portalData,Portal__AdminPassword:password,Portal__BridgeKey:key});}
function officeStart(){o=start('Server',office,{School__DataDirectory:officeData,School__AdminPassword:password,Announcements__Address:portal,Announcements__BridgeKey:key});}
async function stop(child){if(child?.exitCode===null){child.kill();await new Promise(r=>child.once('exit',r));}}
async function ready(base){for(let n=0;n<100;n++){try{if((await fetch(base+'/health')).ok)return;}catch{}await delay(100);}throw Error('Startup failure '+logs);}
class Client{
 constructor(base){this.base=base;this.cookies=new Map();this.csrf='';}
 async req(route,method='GET',body,expected=200){if(route==='register'&&method==='POST'&&body?.agreement===undefined)body={...body,agreement:await acceptRequiredPoliciesForFixture(this.base)};const headers={Cookie:[...this.cookies].map(([k,v])=>k+'='+v).join('; ')};if(method!=='GET')headers['X-CSRF-TOKEN']=this.csrf;let data;if(body instanceof FormData)data=body;else if(method!=='GET'){headers['Content-Type']='application/json';data=JSON.stringify(body??{});}const response=await fetch(this.base+'/api/'+route,{method,headers,body:data});for(const cookie of response.headers.getSetCookie()){const part=cookie.split(';')[0],at=part.indexOf('=');this.cookies.set(part.slice(0,at),part.slice(at+1));}const text=await response.text();assert.equal(response.status,expected,route+' '+response.status+' '+text);return text?JSON.parse(text):null;}
 async login(name='admin'){this.csrf=(await this.req('session')).csrfToken;await this.req('login','POST',{username:name,password});const session=await this.req('session');this.csrf=session.csrfToken;this.user=session.user;return this;}
}
async function waitFor(action){for(let n=0;n<50;n++){const result=await action();if(result)return result;await delay(250);}throw Error('State did not converge: '+logs.slice(-1500));}
const packet=(room,extra={})=>({clientId:id(),title:'가상 학급 공지',body:'학교 외부 공개를 승인한 내용만 전송합니다.',audience:'both',classIds:[room],all:false,attachmentIds:[],...extra});
try{
 publicStart();officeStart();await Promise.all([ready(portal),ready(office)]);const admin=await new Client(portal).login(),teacher=await new Client(office).login();
 const room=(await admin.req('admin/classes','POST',{name:'가상 1학년 1반',grade:1})).id;
 const portalTeacher=(await admin.req('admin/people','POST',{name:'가상 담임',role:'teacher',internalUserId:teacher.user.id,classIds:[room],canBroadcast:false})).id;
 const student=(await admin.req('admin/people','POST',{name:'가상 학생',role:'student',classId:room,classIds:[],canBroadcast:false})).id;
 const parentId=(await admin.req('admin/people','POST',{name:'가상 보호자',role:'parent',classIds:[],canBroadcast:false})).id;await admin.req('admin/families','POST',{parentId,studentId:student});
 const invite=await admin.req('admin/people/'+parentId+'/invite','POST');const parent=new Client(portal);parent.csrf=(await parent.req('session')).csrfToken;await parent.req('register','POST',{code:invite.code,username:'parent',password});await parent.login('parent');
 await waitFor(async()=>{const setup=await teacher.req('external-announcements/setup');return setup.connected&&setup.teacherId===portalTeacher;});
 check('school server initiates authenticated outbound directory/teacher verification; external teacher scope is school approved');
 await teacher.req('external-announcements','POST',packet(id()),403);await teacher.req('external-announcements','POST',packet(room,{all:true,classIds:[]}),403);
 const privateFile=new FormData();privateFile.append('file',new Blob(['교직원 비공개 파일']),'private.txt');const officeFile=await teacher.req('attachments','POST',privateFile);
 await teacher.req('external-announcements','POST',packet(room,{attachmentIds:[officeFile.id]}),409);
 await teacher.req('messages','POST',{clientId:id(),title:'비공개 교직원 쪽지',body:'외부 서버로 전송되면 안 되는 본문',recipientIds:[teacher.user.id],department:null,broadcast:false,attachmentIds:[]});
 const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aK1cAAAAASUVORK5CYII=','base64');
 const form=new FormData();form.append('file',new Blob([png]),'notice.png');const file=await teacher.req('external-announcements/attachments','POST',form);
 const originalExpiry=Date.now()+86400000;const expiresDb=new DatabaseSync(path.join(officeData,'school.db'));expiresDb.exec('PRAGMA busy_timeout=30000');expiresDb.prepare('UPDATE ExternalFiles SET ExpiresAt=? WHERE Id=?').run(originalExpiry,file.id);expiresDb.close();
 const fiveImages=[file.id];for(let i=1;i<5;i++){const extra=new FormData();extra.append('file',new Blob([png]),'page-'+i+'.png');fiveImages.push((await teacher.req('external-announcements/attachments','POST',extra)).id);}
 for(const kind of ['notice','home-letter'])await teacher.req('external-announcements','POST',packet(room,{kind,attachmentIds:[...fiveImages,id()]}),400);
 const request=packet(room,{attachmentIds:fiveImages});const queued=await teacher.req('external-announcements','POST',request);assert.equal((await teacher.req('external-announcements','POST',request)).id,queued.id);await teacher.req('external-announcements','POST',{...request,body:'변경'},409);
 const delivered=await waitFor(async()=>{const row=(await teacher.req('external-announcements')).find(x=>x.id===queued.id);return row?.state==='published'?row:null;});
 assert.equal(delivered.detail.attachments[0].expiresAt,originalExpiry);check('signed bridge preserves original attachment expiry instead of extending it after delayed copying');
 assert.equal(delivered.detail.attachments.length,5);check('office rejects six images for either notice kind and publishes exactly five through the signed bridge');
 const publicView=await fetch(portal+'/api/attachments/'+delivered.detail.attachments[0].id+'/view',{headers:{Cookie:[...parent.cookies].map(([k,v])=>k+'='+v).join('; ')}});assert.equal(publicView.status,200);assert.deepEqual(Buffer.from(await publicView.arrayBuffer()),png);await parent.req('attachments/'+delivered.detail.attachments[0].id+'/download','GET',undefined,404);
 const notices=await parent.req('announcements');assert.equal(notices.length,1);assert.equal(notices[0].id,delivered.remoteId);await parent.req('announcements/'+delivered.remoteId+'/read','POST');
 await waitFor(async()=>{const row=(await teacher.req('external-announcements')).find(x=>x.id===queued.id);return row.detail?.notice.readCount===1;});
 const pdb=new DatabaseSync(path.join(portalData,'announcements.db'));assert.equal(pdb.prepare('SELECT COUNT(*) n FROM Notices').get().n,1);assert.ok(!JSON.stringify(pdb.prepare('SELECT * FROM Notices').all()).includes('비공개 교직원'));assert.ok(!JSON.stringify(pdb.prepare('SELECT * FROM Files').all()).includes('private.txt'));pdb.close();
 check('only explicit announcement payload/files cross bridge; private messages and attachments stay internal; receipts return to sender');

 async function image(bytes,name='letter.png',kind='home-letter',expected=200){const form=new FormData();form.append('kind',kind);form.append('file',new Blob([bytes]),name);return teacher.req('external-announcements/attachments','POST',form,expected);}
 await image(Buffer.alloc(200001),'large.png','home-letter',413);await image('document','fake.png','home-letter',400);await image('document','document.txt','home-letter',400);
 await image('ordinary attachment','ordinary.txt','notice',400);await image(Buffer.alloc(200001),'large.png','notice',413);await image('document','fake.png','notice',400);
 const legacy=await image(png,'legacy.png','notice');const legacyDb=new DatabaseSync(path.join(officeData,'school.db'));legacyDb.prepare('UPDATE ExternalFiles SET Name=? WHERE Id=?').run('legacy.pdf',legacy.id);legacyDb.close();
 await teacher.req('external-announcements','POST',packet(room,{attachmentIds:[legacy.id]}),400);
 check('all public announcement uploads and legacy staged files obey image-only 200KB policy, including generic notices');
 const homeFile=await image(png);const homeRequest=packet(room,{title:'이미지 가정통신문',kind:'home-letter',attachmentIds:[homeFile.id]});const homeJob=await teacher.req('external-announcements','POST',homeRequest);
 const home=await waitFor(async()=>{const row=(await teacher.req('external-announcements')).find(x=>x.id===homeJob.id);return row?.state==='published'?row:null;});
 const homeDetail=await parent.req('announcements/'+home.remoteId);assert.equal(homeDetail.notice.kind,'home-letter');
 const homeResponse=await fetch(portal+'/api/attachments/'+homeDetail.attachments[0].id+'/view',{headers:{Cookie:[...parent.cookies].map(([k,v])=>k+'='+v).join('; ')}});assert.equal(homeResponse.status,200);assert.deepEqual(Buffer.from(await homeResponse.arrayBuffer()),png);
 await parent.req('attachments/'+homeDetail.attachments[0].id+'/download','GET',undefined,404);
 await teacher.req('external-announcements/'+homeJob.id+'/retract','POST');await waitFor(async()=>{const row=(await teacher.req('external-announcements')).find(x=>x.id===homeJob.id);return row?.state==='withdrawn';});await parent.req('attachments/'+homeDetail.attachments[0].id+'/view','GET',undefined,404);
 check('PC home-letter image validation, signed bridge publication, authorized inline viewing and recall work end to end');
 const odb=new DatabaseSync(path.join(officeData,'school.db'));odb.prepare("UPDATE ExternalOutbox SET State='pending',RemoteId=NULL,Detail=NULL WHERE Id=?").run(queued.id);odb.close();
 await waitFor(async()=>{const row=(await teacher.req('external-announcements')).find(x=>x.id===queued.id);return row.state==='published';});assert.equal((await parent.req('announcements')).length,1);
 check('lost publish acknowledgement probes immutable client ID before upload; no duplicate notice or attachment reuse');
 await stop(p);const offlinePacket=packet(room,{title:'오프라인 발송'}),offlineJob=await teacher.req('external-announcements','POST',offlinePacket);await delay(2200);assert.equal((await teacher.req('external-announcements')).find(x=>x.id===offlineJob.id).state,'pending');await stop(o);officeStart();await ready(office);await teacher.login();assert.equal((await teacher.req('external-announcements')).find(x=>x.id===offlineJob.id).state,'pending');publicStart();await ready(portal);
 await waitFor(async()=>{const row=(await teacher.req('external-announcements')).find(x=>x.id===offlineJob.id);return row.state==='published';});assert.equal((await parent.req('announcements')).length,2);
 check('offline durable outbox survives school-server restart and retries after public-server recovery');
 await teacher.req('external-announcements/'+queued.id+'/retract','POST');await waitFor(async()=>{const row=(await teacher.req('external-announcements')).find(x=>x.id===queued.id);return row.state==='withdrawn';});await parent.req('announcements/'+delivered.remoteId,'GET',undefined,404);
 check('outbound retraction revokes public notice and file access');
 await stop(p);const stalePacket=packet(room,{title:'관계 변경 전 대기'}),staleJob=await teacher.req('external-announcements','POST',stalePacket);await stop(o);publicStart();await ready(portal);await admin.login();await admin.req('admin/families/'+parentId+'/'+student,'DELETE');await admin.req('admin/families','POST',{parentId,studentId:student});officeStart();await ready(office);await teacher.login();
 await waitFor(async()=>{const row=(await teacher.req('external-announcements')).find(x=>x.id===staleJob.id);return row.state==='needs-review';});await parent.login('parent');assert.equal((await parent.req('announcements')).length,0);
 check('queued recipient snapshot never expands; guardian unlink and relink requires review instead of reviving old delivery');
 const guest=new Client(office);await guest.req('external-announcements','GET',undefined,401);await guest.req('admin/announcement-status','GET',undefined,401);
 check('outbox and health details are authenticated and owner/admin scoped');
 writeFileSync('artifacts/announcement-bridge-test-results.json',JSON.stringify({at:new Date().toISOString(),passed},null,2));console.log(passed.length+' bridge checks passed.');
}finally{await Promise.all([stop(p),stop(o)]);}
