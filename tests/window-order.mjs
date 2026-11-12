// Actual WPF windows against a temporary loopback server. No school settings or data are used.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {existsSync,mkdirSync,readFileSync} from 'node:fs';
import {DatabaseSync} from 'node:sqlite';
import path from 'node:path';
import net from 'node:net';
assert.equal(process.platform,'win32');
const root=process.cwd(),services=path.resolve(process.env.YY_TEST_SERVICE_ROOT??root);
const sdk=existsSync('.tools/dotnet/dotnet.exe')?path.resolve('.tools/dotnet/dotnet.exe'):process.env.DOTNET_ROOT?path.join(process.env.DOTNET_ROOT,'dotnet.exe'):'dotnet';
const fixture=path.join(root,'.test-data','window-order-'+randomUUID()),output=path.join(root,'artifacts','window-order');mkdirSync(output,{recursive:true});
const password='Test-'+randomUUID();const listener=net.createServer();await new Promise(r=>listener.listen(0,'127.0.0.1',r));const address='http://127.0.0.1:'+listener.address().port;await new Promise(r=>listener.close(r));
let logs='',ui;
const server=spawn(sdk,[path.join(services,'src/SchoolMessenger.Server/bin/Debug/net10.0/SchoolMessenger.Server.dll'),'--urls',address],{cwd:services,windowsHide:true,env:{...process.env,ASPNETCORE_ENVIRONMENT:'Development',School__DataDirectory:path.join(fixture,'server'),School__AdminPassword:password,FeedbackMail__Enabled:'false',Feedback__AutomaticReportsEnabled:'false',Operations__AlertMailEnabled:'false'}});
server.stdout.on('data',b=>logs+=b);server.stderr.on('data',b=>logs+=b);
const pause=ms=>new Promise(r=>setTimeout(r,ms));
try{
  let ready=false;for(let i=0;i<200;i++){assert.equal(server.exitCode,null,logs);try{if((await fetch(address+'/health')).ok){ready=true;break;}}catch{}await pause(150);}assert.ok(ready,'Fixture did not start');
  const db=new DatabaseSync(path.join(fixture,'server','school.db'));
  try {
    const {Id:owner,Name:teacherName}=db.prepare("SELECT Id,Name FROM Users WHERE Username='admin'").get();
    const id=()=>randomUUID().replaceAll('-',''),batch=id(),client=id();
    // A school-sized week: 18 classes and 48 teachers, so the swap board is checked at the size teachers really see.
    const rooms=Array.from({length:18},(_,c)=>({id:id(),name:`${Math.floor(c/6)+1}학년 ${c%6+1}반`,grade:Math.floor(c/6)+1}));
    const subjects=['국어','수학','영어','통합사회','통합과학','체육','음악 감상과 비평','미술 창작'];
    const monday=new Date(Date.now()+9*3600000);monday.setUTCDate(monday.getUTCDate()-(monday.getUTCDay()+6)%7);
    const date=day=>new Date(monday.getTime()+day*86400000).toISOString().slice(0,10);
    // Teacher (class + 5 x slot) mod 48 never teaches two classes in one period; teacher 0 is the signed-in account.
    const cells=rooms.flatMap((room,c)=>Array.from({length:35},(_,i)=>{const t=(c+i*5)%48;return{classId:room.id,className:room.name,day:Math.floor(i/7)+1,period:i%7+1,subject:subjects[t%subjects.length],teacherId:t?'':owner,teacherName:t?'가상교사 '+String(t).padStart(2,'0'):teacherName};}));
    for(const room of rooms)db.prepare('INSERT INTO TimetableClasses VALUES(?,?,?)').run(room.id,room.name,room.grade);
    const payload={id:batch,revision:1,owner,clientId:client,kind:'base',year:monday.getUTCFullYear(),semester:2,start:date(0),end:date(6),date:null,fileName:'가상시간표.xlsx',publishedAt:Date.now(),withdrawn:false,cells};
    db.prepare('INSERT INTO TimetableBatches(Revision,Id,Owner,ClientId,Payload,Delivered) VALUES(1,?,?,?,?,1)').run(batch,owner,client,JSON.stringify(payload));
  } finally { db.close(); }
  ui=spawn(sdk,[path.join(root,'src/SchoolMessenger.Desktop/bin/Debug/net10.0-windows/SchoolMessenger.dll'),'--verify-ui'],{cwd:root,windowsHide:true,env:{...process.env,SCHOOL_UI_SERVER:address,SCHOOL_UI_USER:'admin',SCHOOL_UI_PASSWORD:password,SCHOOL_UI_OUTPUT:path.join(output,'client-preview.png'),SCHOOL_UI_LOCALDIR:path.join(fixture,'client'),YY_WINDOW_ORDER_UI_ONLY:'1'}});
  let timeout;const code=await Promise.race([new Promise((resolve,reject)=>{ui.once('error',reject);ui.once('exit',resolve);}),new Promise(resolve=>{timeout=setTimeout(()=>resolve(-1),90000);})]);clearTimeout(timeout);
  assert.equal(code,0,existsSync('artifacts/ui-error.txt')?readFileSync('artifacts/ui-error.txt','utf8'):'Window order verification failed');
  const report=JSON.parse(readFileSync(path.join(output,'window-order-checks.json'),'utf8'));for(const check of report.checks)console.log('PASS '+check);console.log(report.checks.length+' native window order checks passed.');
}finally{if(ui?.exitCode===null)ui.kill();if(server.exitCode===null){const closed=new Promise(r=>server.once('exit',r));server.kill();await closed;}}
