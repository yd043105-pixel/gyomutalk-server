import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomUUID,createHash} from 'node:crypto';
import {mkdirSync,writeFileSync,readFileSync,existsSync,unlinkSync,renameSync} from 'node:fs';
import {DatabaseSync} from 'node:sqlite';
import net from 'node:net';
import path from 'node:path';

const root=process.cwd(),sdk=path.join(root,'.tools/dotnet/dotnet.exe'),dotnet=existsSync(sdk)?sdk:'dotnet';
const testRoot=path.resolve(process.env.YY_TEST_DATA_ROOT??'.test-data','server-operations-'+randomUUID());mkdirSync(testRoot,{recursive:true});
const checks=[];const check=name=>{checks.push(name);console.log('PASS '+name);};
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
async function wait(test){for(let i=0;i<150;i++){if(await test())return;await sleep(100);}throw Error('Condition timeout');}
async function port(){const s=net.createServer();await new Promise(r=>s.listen(0,'127.0.0.1',r));const p=s.address().port;await new Promise(r=>s.close(r));return p;}
async function run(command,args,env=process.env){const p=spawn(command,args,{cwd:root,env,windowsHide:true});let output='';p.stdout.on('data',b=>output+=b);p.stderr.on('data',b=>output+=b);return{exit:await new Promise((r,j)=>{p.once('exit',r);p.once('error',j);}),output};}
function removeFixture(file){assert.ok(path.resolve(file).startsWith(testRoot+path.sep));unlinkSync(file);}
const powershell=path.join(process.env.SystemRoot??'C:/Windows','System32/WindowsPowerShell/v1.0/powershell.exe');
const psFixture=path.join(testRoot,'powershell');
// A PowerShell 7 parent may otherwise make Windows PowerShell load incompatible modules.
const psEnv={...process.env,PSModulePath:path.join(process.env.SystemRoot??'C:/Windows','System32/WindowsPowerShell/v1.0/Modules')+';'+path.join(process.env.ProgramFiles??'C:/Program Files','WindowsPowerShell/Modules')};
const ps=await run(powershell,['-NoProfile','-File',path.join(root,'tests/server-operations.ps1'),'-FixtureRoot',psFixture],psEnv);
assert.equal(ps.exit,0,ps.output);const psChecks=JSON.parse(ps.output.slice(ps.output.indexOf('{')));checks.push(...psChecks.checks);console.log('PASS '+psChecks.checks.length+' Windows operations checks');
for(const project of ['Server','AnnouncementServer']){
 const portal=project==='AnnouncementServer',prefix=portal?'Portal':'School',service=portal?'announcements':'office';
 const folder=path.join(testRoot,project),data=path.join(folder,'data'),backup=path.join(folder,'separate-backup');mkdirSync(folder,{recursive:true});
 const base='http://127.0.0.1:'+await port(),key='Synthetic-'+randomUUID()+randomUUID(),dll=path.join(root,'src/SchoolMessenger.'+project,'bin/Debug/net10.0/SchoolMessenger.'+project+'.dll');
 const env={...process.env,ASPNETCORE_ENVIRONMENT:'Development',[prefix+'__DataDirectory']:data,[prefix+'__BackupDirectory']:backup,[prefix+'__AttachmentBackup']:'true',[prefix+'__AdminPassword']:'Synthetic-'+randomUUID(),Portal__BridgeKey:'Synthetic-'+randomUUID(),Announcements__Address:'',ASPNETCORE_FORWARDEDHEADERS_ENABLED:'false',DOTNET_FORWARDEDHEADERS_ENABLED:'false',Operations__MonitorKey:key,Operations__MinimumFreeBytes:'0',School__CleanupSeconds:'1',Portal__CleanupSeconds:'1'};
 let child,logs='';async function start(){logs='';child=spawn(dotnet,[dll,'--urls',base],{cwd:path.dirname(dll),env,windowsHide:true});child.stdout.on('data',b=>logs+=b);child.stderr.on('data',b=>logs+=b);await wait(async()=>{if(child.exitCode!==null)throw Error('Server exited '+logs);try{return(await fetch(base+'/health')).ok;}catch{return false;}});}
 async function stop(){if(child?.exitCode===null&&child?.signalCode===null){child.kill();await new Promise(r=>child.once('exit',r));}}
 function db(){const d=new DatabaseSync(path.join(data,portal?'announcements.db':'school.db'));d.exec('PRAGMA busy_timeout=30000');return d;}
 async function health(){const response=await fetch(base+'/health/operations',{headers:{'X-Operations-Key':key}});return{status:response.status,value:await response.json()};}
 try{
  await start();await wait(async()=>(await health()).status===200);
  for(const headers of [{},{'X-Operations-Key':'wrong'},{'X-Forwarded-For':'127.0.0.1'}])assert.equal((await fetch(base+'/health/operations',{headers})).status,404);
  const status=await health();assert.equal(status.value.service,service);assert.deepEqual(Object.keys(status.value).sort(),['issues','service','status']);
  assert.match((await fetch(base+'/api/session')).headers.get('Cache-Control'),/no-store/);
  assert.ok(!JSON.stringify(status.value).includes(data)&&!JSON.stringify(status.value).includes(key));
  check(project+': private monitor authentication, forwarded-header rejection and minimal status');
  const busy=await run(dotnet,[dll,'--operations-backup'],env);assert.notEqual(busy.exit,0);
  check(project+': offline backup refuses a live server');
  let d=db();const owner=d.prepare(portal?'SELECT Id FROM People LIMIT 1':'SELECT Id FROM Users LIMIT 1').get().Id;
  const id=randomUUID().replaceAll('-',''),bytes=Buffer.from('Synthetic immutable attachment '+randomUUID()),hash=createHash('sha256').update(bytes).digest('hex').toUpperCase(),expires=Date.now()+30*86400000;
  if(portal)d.prepare('INSERT INTO Files(Id,OwnerId,ClientId,Hash,Name,Size,UploadedAt,ExpiresAt) VALUES(?,?,?,?,?,?,?,?)').run(id,owner,randomUUID(),'synthetic','fixture.txt',bytes.length,Date.now(),expires);
  else d.prepare('INSERT INTO Attachments(Id,OwnerId,Name,Size,UploadedAt,ExpiresAt) VALUES(?,?,?,?,?,?)').run(id,owner,'fixture.txt',bytes.length,Date.now(),expires);
  d.prepare('INSERT INTO FileVerification(Kind,FileId,State,Sha256,UpdatedAt) VALUES(?,?,?,?,?)').run(portal?'portal':'office',id,'ready',hash,Date.now());
  writeFileSync(path.join(data,'files',id),bytes);d.prepare("UPDATE BackendJobs SET LastSuccess=0 WHERE Name='backup'").run();d.close();
  const mirrored=path.join(backup,'attachments',id);await wait(async()=>existsSync(mirrored)&&existsSync(mirrored+'.json'));
  const metadata=JSON.parse(readFileSync(mirrored+'.json','utf8'));assert.equal(metadata.expiresAt,expires);assert.equal(metadata.sha256,hash);assert.deepEqual(readFileSync(mirrored),bytes);
  check(project+': configured-device attachment copy preserves expiry and verifies SHA256');
  if(portal){
   const student=randomUUID().replaceAll('-','');d=db();d.prepare("INSERT INTO People(Id,Name,Role) VALUES(?,?,'student')").run(student,'Synthetic graduating pupil');d.prepare("UPDATE BackendJobs SET LastSuccess=0 WHERE Name='backup'").run();d.close();
   const snapshot=path.join(backup,'announcements-'+new Date().toISOString().slice(0,10).replaceAll('-','')+'.db');
   await wait(async()=>{const b=new DatabaseSync(snapshot,{readOnly:true});try{return!!b.prepare('SELECT 1 FROM People WHERE Id=?').get(student);}finally{b.close();}});
   d=db();d.prepare('INSERT INTO Graduations VALUES(?,?)').run(student,Date.now()-1000);d.close();
   await wait(async()=>{const b=new DatabaseSync(snapshot,{readOnly:true});try{return!b.prepare('SELECT 1 FROM People WHERE Id=?').get(student);}finally{b.close();}});
   check('AnnouncementServer: graduation erases structured student data from configured external DB backups');
   const waitingStudent=randomUUID().replaceAll('-','');d=db();d.prepare("INSERT INTO People(Id,Name,Role) VALUES(?,?,'student')").run(waitingStudent,'Synthetic waiting pupil');d.close();
   await stop();let command=await run(dotnet,[dll,'--operations-backup'],env);assert.equal(command.exit,0,command.output);
   env.Portal__CleanupSeconds='3600';await start();
   const disconnected=backup+'.disconnected';assert.ok(path.resolve(backup).startsWith(testRoot+path.sep)&&path.resolve(disconnected).startsWith(testRoot+path.sep));renameSync(backup,disconnected);
   try{
    d=db();d.prepare('INSERT INTO Graduations VALUES(?,?)').run(waitingStudent,Date.now()-1000);d.close();await fetch(base+'/health');
    d=db();assert.equal(d.prepare('SELECT PublicDone FROM PrivacyErasure WHERE Id=?').get(waitingStudent).PublicDone,0);d.close();
    const old=new DatabaseSync(path.join(disconnected,path.basename(snapshot)),{readOnly:true});assert.ok(old.prepare('SELECT 1 FROM People WHERE Id=?').get(waitingStudent));old.close();
   }finally{await stop();renameSync(disconnected,backup);}
   await stop();env.Portal__CleanupSeconds='1';await start();
   await wait(async()=>{const state=db();try{return state.prepare('SELECT PublicDone FROM PrivacyErasure WHERE Id=?').get(waitingStudent).PublicDone===1;}finally{state.close();}});
   const erased=new DatabaseSync(snapshot,{readOnly:true});assert.ok(!erased.prepare('SELECT 1 FROM People WHERE Id=?').get(waitingStudent));erased.close();
   check('AnnouncementServer: disconnected backup keeps graduation erasure pending until verified on reconnection');
  }
  await stop();let result=await run(dotnet,[dll,'--operations-backup'],env);assert.equal(result.exit,0,result.output);
  removeFixture(path.join(data,'files',id));result=await run(dotnet,[dll,'--operations-restore-attachments'],env);assert.notEqual(result.exit,0);assert.ok(!existsSync(path.join(data,'files',id)));
  d=db();d.prepare('INSERT INTO BackendRecoveryState(Id,ReviewRequired,RestoredAt) VALUES(1,1,?) ON CONFLICT(Id) DO UPDATE SET ReviewRequired=1').run(Date.now());d.close();
  result=await run(dotnet,[dll,'--operations-restore-attachments'],env);assert.equal(result.exit,0,result.output);assert.deepEqual(readFileSync(path.join(data,'files',id)),bytes);
  writeFileSync(mirrored,'corrupt synthetic backup');result=await run(dotnet,[dll,'--operations-restore-attachments'],env);assert.notEqual(result.exit,0);assert.deepEqual(readFileSync(path.join(data,'files',id)),bytes);writeFileSync(mirrored,bytes);
  check(project+': attachment restore requires review, rejects corruption and preserves existing originals');
  d=db();d.prepare('UPDATE '+(portal?'Files':'Attachments')+' SET ExpiresAt=? WHERE Id=?').run(Date.now()-1000,id);d.close();
  result=await run(dotnet,[dll,'--operations-restore-attachments'],env);assert.equal(result.exit,0,result.output);assert.ok(!existsSync(mirrored)&&!existsSync(mirrored+'.json'));
  writeFileSync(mirrored+'.json.tmp','interrupted synthetic metadata');writeFileSync(mirrored+'.tmp','interrupted synthetic content');
  await start();await wait(async()=>!existsSync(path.join(data,'files',id)));
  assert.ok(!existsSync(mirrored+'.json.tmp')&&!existsSync(mirrored+'.tmp'));
  check(project+': expired originals and backup replicas are removed after restore/restart');
  await stop();
  d=db();d.prepare("UPDATE BackendJobs SET LastSuccess=?,ErrorCode='SyntheticFailure' WHERE Name='backup'").run(Date.now()-31*3600000);d.close();
  env.School__CleanupSeconds='3600';env.Portal__CleanupSeconds='3600';env.Operations__MinimumFreeBytes=String(Number.MAX_SAFE_INTEGER);
  await start();await wait(async()=>(await health()).value.issues.includes('disk-low'));
  // Modify persisted state only after startup maintenance, which legitimately retries failed jobs.
  d=db();d.prepare("UPDATE BackendJobs SET LastSuccess=?,ErrorCode='SyntheticFailure' WHERE Name='backup'").run(Date.now()-31*3600000);d.close();
  const degraded=await health();assert.equal(degraded.status,503);assert.ok(degraded.value.issues.includes('backup-failed')&&degraded.value.issues.includes('backup-stale')&&degraded.value.issues.includes('disk-low'));
  check(project+': disk shortage and failed/stale backups produce authenticated degraded status');
  if(portal){
   await stop();env.Portal__TunnelOnly='true';env.Portal__LoginLimitPerMinute='20';await start();
   const sessionResponse=await fetch(base+'/api/session'),session=await sessionResponse.json(),cookie=sessionResponse.headers.getSetCookie().map(c=>c.split(';')[0]).join('; ');
   async function login(ip){return (await fetch(base+'/api/login',{method:'POST',headers:{Cookie:cookie,'X-CSRF-TOKEN':session.csrfToken,'Content-Type':'application/json','CF-Connecting-IP':ip,'X-Forwarded-For':'203.0.113.17'},body:JSON.stringify({username:'missing.synthetic.account',password:'SyntheticOnly'})})).status;}
   for(let i=1;i<=25;i++)assert.equal(await login('198.51.100.'+i),401);
   for(let i=0;i<20;i++)assert.equal(await login('198.51.100.250'),401);
   assert.equal(await login('198.51.100.250'),429);
   check('AnnouncementServer: loopback Cloudflare client address separates login limits while arbitrary forwarding is ignored');
   await stop();const unsafe=await run(dotnet,[dll,'--urls','http://0.0.0.0:'+await port()],env);assert.notEqual(unsafe.exit,0);
   check('AnnouncementServer: tunnel-only mode rejects a public origin binding');
  }
 }finally{await stop();}
}
mkdirSync(path.join(root,'artifacts'),{recursive:true});writeFileSync(path.join(root,'artifacts/server-operations-checks.json'),JSON.stringify({checks,realEmailSent:false,actualSchoolServerConfigured:false,tasksRegistered:false,upsPowerLossTested:false},null,2));
console.log('PASS total '+checks.length+' operations checks');
