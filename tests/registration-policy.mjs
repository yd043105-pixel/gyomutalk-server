import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {readFileSync,writeFileSync,mkdirSync,existsSync} from 'node:fs';
import {DatabaseSync} from 'node:sqlite';
import path from 'node:path';
import net from 'node:net';
import {writeDeploymentReviewFixture} from './deployment-review-fixture.mjs';

const root=process.cwd(),sdk=existsSync('.tools/dotnet/dotnet.exe')?path.join(root,'.tools/dotnet/dotnet.exe'):path.join(process.env.DOTNET_ROOT,process.platform==='win32'?'dotnet.exe':'dotnet');
const password='Test-'+randomUUID(),key='Bridge-'+randomUUID(),passed=[];
const embeddedDraft=JSON.parse(readFileSync('config/registration-policy.draft.json','utf8'));
const check=name=>{passed.push(name);console.log('PASS '+name);};
const delay=ms=>new Promise(r=>setTimeout(r,ms));
for(const project of ['AnnouncementServer','Server']){
 const portal=project==='AnnouncementServer',setting=portal?'Portal':'School',dir=path.join(process.env.YY_TEST_DATA_ROOT??path.join(root,'.test-data'),'policy-'+randomUUID());mkdirSync(dir,{recursive:true});
 const file=path.join(dir,'policy.json'),data=path.join(dir,'data');
 let document=structuredClone(embeddedDraft);
 document.documents.push({id:'optional',title:'가상 선택 항목',version:'test.1',body:'선택 여부 시험용 문구',required:false,action:'consent'});
 const save=()=>writeFileSync(file,JSON.stringify(document));save();
 const listener=net.createServer();await new Promise(r=>listener.listen(0,'127.0.0.1',r));const base='http://127.0.0.1:'+listener.address().port;await new Promise(r=>listener.close(r));
 const securityReviewFile=writeDeploymentReviewFixture(path.join(dir,'review'),portal?'announcements':'office',base);
 let server,logs='';
 async function start(mode='Development',policyFile=file){logs='';server=spawn(sdk,[path.join(root,`src/SchoolMessenger.${project}/bin/Debug/net10.0/SchoolMessenger.${project}.dll`),'--urls',base],{cwd:path.join(root,'src','SchoolMessenger.'+project),windowsHide:true,env:{...process.env,ASPNETCORE_ENVIRONMENT:mode,Operations__SchoolId:'YY-SYNTHETIC-ONLY',Operations__SecurityReviewFile:securityReviewFile,[setting+'__DataDirectory']:data,[setting+'__AdminPassword']:password,Portal__BridgeKey:key,Registration__PolicyFile:policyFile}});server.stdout.on('data',b=>logs+=b);server.stderr.on('data',b=>logs+=b);for(let i=0;i<100;i++){try{if((await fetch(base+'/health')).ok)return;}catch{}if(server.exitCode!==null)throw Error(logs);await delay(100);}throw Error(logs);}
 async function stop(){if(server?.exitCode===null){server.kill();await new Promise(r=>server.once('exit',r));}}
 class Client{
  cookies=new Map();csrf='';
  async req(route,method='GET',body,expected=200,csrf=true){const headers={Cookie:[...this.cookies].map(([k,v])=>k+'='+v).join('; ')};if(method!=='GET'){headers['Content-Type']='application/json';if(csrf)headers['X-CSRF-TOKEN']=this.csrf;}const response=await fetch(base+'/api/'+route,{method,headers,body:method==='GET'?undefined:JSON.stringify(body??{})});for(const cookie of response.headers.getSetCookie()){const p=cookie.split(';')[0],i=p.indexOf('=');this.cookies.set(p.slice(0,i),p.slice(i+1));}const text=await response.text();assert.equal(response.status,expected,project+' '+route+' '+text);return text?JSON.parse(text):null;}
  async refresh(){this.csrf=(await this.req('session')).csrfToken;return this;}
  async login(){await this.refresh();await this.req('login','POST',{username:'admin',password});return this.refresh();}
 }
 const signup=(username,agreement)=>portal?{name:'가상 보호자',username,password,role:'parent',agreement}:{name:'가상 교사',username,password,department:'가상 부서',agreement};
 const dbFile=path.join(data,portal?'announcements.db':'school.db');
 const accounts=username=>{const db=new DatabaseSync(dbFile);try{return db.prepare(`SELECT COUNT(*) n FROM ${portal?'People':'Registrations'} WHERE Username=?`).get(username).n;}finally{db.close();}};
 try{
  await start();const guest=await new Client().refresh();let policy=await guest.req('registration/policy');assert.equal(policy.draft,true);assert.equal(policy.canRegister,true);assert.match(policy.revision,/^[A-F0-9]{64}$/);assert.equal(policy.documents.length,3);assert.ok(policy.documents.every(d=>d.body&&d.version));check(project+': unauthenticated policy supplies full versioned documents and explicit draft status');
  const agreement={revision:policy.revision,acceptedIds:['terms','privacy']};
  for(const invalid of [undefined,null,{...agreement,acceptedIds:[]},{...agreement,acceptedIds:['terms']},{...agreement,acceptedIds:['terms','privacy','forged']},{...agreement,acceptedIds:['terms','privacy','terms']},{...agreement,revision:'forged'}])await guest.req('register','POST',signup('invalid',invalid),400);
  assert.equal(accounts('invalid'),0);check(project+': missing, unchecked, duplicate, invented and stale acceptance is rejected without creating an account');
  await guest.req('register','POST',signup('no.csrf',agreement),400,false);assert.equal(accounts('no.csrf'),0);check(project+': acceptance cannot bypass CSRF');
  const before=Date.now();await guest.req('register','POST',signup('required.only',{...agreement,acceptedAt:1,subjectId:'forged'}));
  let db=new DatabaseSync(dbFile);let row=db.prepare('SELECT * FROM RegistrationAgreements').get();const originalSubject=row.SubjectId;assert.deepEqual(JSON.parse(row.AcceptedIds),['terms','privacy']);assert.ok(row.AcceptedAt>=before&&row.AcceptedAt<=Date.now());assert.notEqual(row.SubjectId,'forged');let archive=JSON.parse(db.prepare('SELECT PolicyJson FROM RegistrationPolicies WHERE Revision=?').get(row.Revision).PolicyJson);assert.equal(archive.documents[1].body,policy.documents[1].body);assert.equal(archive.documents[1].action,'consent');db.close();check(project+': required-only acceptance succeeds; authoritative identity, server timestamp, document text and action are archived');
  await guest.req('register','POST',signup('all.selected',{...agreement,acceptedIds:['terms','privacy','optional']}));db=new DatabaseSync(dbFile);assert.equal(JSON.parse(db.prepare('SELECT AcceptedIds FROM RegistrationAgreements WHERE SubjectId<>?').get(originalSubject).AcceptedIds).length,3);const count=db.prepare('SELECT COUNT(*) n FROM RegistrationAgreements').get().n;db.close();await guest.req('register','POST',signup('required.only',agreement),409);db=new DatabaseSync(dbFile);assert.equal(db.prepare('SELECT COUNT(*) n FROM RegistrationAgreements').get().n,count);db.close();check(project+': optional unchecked or checked both work; duplicate signup adds no agreement');
  db=new DatabaseSync(dbFile);db.exec("CREATE TRIGGER FailAgreement BEFORE INSERT ON RegistrationAgreements BEGIN SELECT RAISE(ABORT,'synthetic agreement failure'); END");db.close();await guest.req('register','POST',signup('atomic.rollback',agreement),500);assert.equal(accounts('atomic.rollback'),0);db=new DatabaseSync(dbFile);db.exec('DROP TRIGGER FailAgreement');db.close();check(project+': failed agreement storage rolls back account creation');
  if(portal){
   const admin=await new Client().login(),person=(await admin.req('admin/people','POST',{name:'가상 초대 보호자',role:'parent',classId:null,internalUserId:null,classIds:[],canBroadcast:false}));
   const invite=(await admin.req('admin/people/'+person.id+'/invite','POST')).code;
   await guest.req('register','POST',{code:invite,username:'invited',password},400);
   db=new DatabaseSync(dbFile);assert.equal(db.prepare('SELECT UsedAt FROM Invites WHERE PersonId=?').get(person.id).UsedAt,null);db.close();
   await guest.req('register','POST',{code:invite,username:'invited',password,agreement});db=new DatabaseSync(dbFile);assert.ok(db.prepare('SELECT AcceptedAt FROM RegistrationAgreements WHERE SubjectId=?').get(person.id));db.close();check('AnnouncementServer: invite signup also requires acceptance and rejected agreement does not consume the invitation');
  }
  await stop();document.documents[1].body+='\n새로운 가상 내용';document.documents[1].version='test.2';document.documents[1].action='acknowledgment';save();await start();const changed=await new Client().refresh();policy=await changed.req('registration/policy');assert.notEqual(policy.revision,agreement.revision);await changed.req('register','POST',signup('stale.window',agreement),400);await changed.req('register','POST',signup('new.version',{revision:policy.revision,acceptedIds:['terms','privacy']}));db=new DatabaseSync(dbFile);archive=JSON.parse(db.prepare('SELECT PolicyJson FROM RegistrationPolicies WHERE Revision=?').get(agreement.revision).PolicyJson);assert.equal(archive.documents[1].action,'consent');assert.ok(db.prepare('SELECT 1 FROM RegistrationAgreements WHERE SubjectId=?').get(originalSubject));db.close();check(project+': restart preserves old evidence; changed text/version/action gets a new revision and old signup windows are rejected');
  await stop();await start('Production','');const blocked=await new Client().refresh();policy=await blocked.req('registration/policy');assert.equal(policy.canRegister,false);assert.deepEqual(policy.documents,embeddedDraft.documents);assert.equal(policy.operator,embeddedDraft.operator);assert.equal(policy.contact,embeddedDraft.contact);check(project+': packaged policy exposes the current school documents verbatim, including version and contact');await blocked.req('register','POST',signup('production.draft',{revision:policy.revision,acceptedIds:['terms','privacy']}),503);assert.equal(accounts('production.draft'),0);await new Client().login();check(project+': default production draft blocks only new registration and preserves administrator login');
  await stop();document.approvedForProduction=true;document.operator='가상 학교';document.contact='가상 시험 담당자';save();await start('Production');const approved=await new Client().refresh();policy=await approved.req('registration/policy');assert.equal(policy.canRegister,true);assert.equal(policy.draft,false);await approved.req('register','POST',signup('production.approved',{revision:policy.revision,acceptedIds:['terms','privacy']}));check(project+': explicitly approved local production configuration enables versioned signup');
 }finally{await stop();}
}
mkdirSync('artifacts',{recursive:true});writeFileSync('artifacts/registration-policy-checks.json',JSON.stringify({version:'1.0.0',verifiedAt:new Date().toISOString(),passed},null,2));console.log(passed.length+' registration policy checks passed.');
