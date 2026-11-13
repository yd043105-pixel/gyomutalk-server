// Presses every button of the real teacher program against a temporary loopback server holding a made-up school,
// and leaves a picture of every screen with a list of layout faults in artifacts/screen-tour/teacher.
// Open artifacts/screen-tour/teacher/index.html to look through them. No school settings or data are used.
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
const fixture=path.join(root,'.test-data','screen-tour-'+randomUUID()),output=path.join(root,'artifacts','screen-tour','teacher');mkdirSync(output,{recursive:true});
const password='Test-'+randomUUID(),personal='Personal-'+randomUUID();
const listener=net.createServer();await new Promise(r=>listener.listen(0,'127.0.0.1',r));const address='http://127.0.0.1:'+listener.address().port;await new Promise(r=>listener.close(r));
let logs='',ui;
const server=spawn(sdk,[path.join(services,'src/SchoolMessenger.Server/bin/Debug/net10.0/SchoolMessenger.Server.dll'),'--urls',address],{cwd:services,windowsHide:true,env:{...process.env,ASPNETCORE_ENVIRONMENT:'Development',School__Name:'가상고등학교',School__DataDirectory:path.join(fixture,'server'),School__AdminPassword:password,FeedbackMail__Enabled:'false',Feedback__AutomaticReportsEnabled:'false',Operations__AlertMailEnabled:'false'}});
server.stdout.on('data',b=>logs+=b);server.stderr.on('data',b=>logs+=b);
const pause=ms=>new Promise(r=>setTimeout(r,ms));
class Client{
 cookies=new Map();
 async request(route,method='GET',body){
  const headers={Cookie:[...this.cookies].map(([k,v])=>k+'='+v).join('; ')};if(method!=='GET')headers['X-CSRF-TOKEN']=this.csrf;if(body!==undefined)headers['Content-Type']='application/json';
  const response=await fetch(address+'/api/'+route,{method,headers,body:body===undefined?undefined:JSON.stringify(body)});
  for(const cookie of response.headers.getSetCookie()){const p=cookie.split(';')[0],i=p.indexOf('=');this.cookies.set(p.slice(0,i),p.slice(i+1));}
  const text=await response.text();if(!response.ok)throw Error(method+' '+route+' '+response.status+' '+text);return text&&response.headers.get('Content-Type')?.includes('json')?JSON.parse(text):text;
 }
 async login(username,secret){this.csrf=(await this.request('session')).csrfToken;await this.request('login','POST',{username,password:secret,rememberLogin:false});const s=await this.request('session');this.csrf=s.csrfToken;this.user=s.user;return this;}
}
// Each piece of made-up data is added on its own, so one refused request leaves the rest of the school in place.
const seed=async(name,work)=>{try{await work();}catch(error){console.log('SEED SKIPPED '+name+': '+error.message.slice(0,200));}};
try{
  let ready=false;for(let i=0;i<200;i++){assert.equal(server.exitCode,null,logs);try{if((await fetch(address+'/health')).ok){ready=true;break;}}catch{}await pause(150);}assert.ok(ready,'Fixture did not start');
  const admin=await new Client().login('admin',password);
  const departments=['교무기획부','교육연구부','학생생활안전부','1학년부','2학년부','3학년부','진로진학상담부','교육정보부'];
  const staff=[];
  await seed('staff',async()=>{for(let i=1;i<=16;i++){const name='가상교사'+String(i).padStart(2,'0');staff.push(await admin.request('admin/staff','POST',{loginName:name,name,department:departments[i%departments.length],seatIp:null,temporaryPassword:'12345678',useSharedPassword:true}));}});
  const colleague=new Client();
  await seed('colleague',async()=>{await colleague.login('가상교사01','12345678');await colleague.request('activate','POST',{newPassword:personal});await colleague.login('가상교사01',personal);});
  const ids=staff.map(s=>s.id);
  await seed('sent messages',async()=>{for(const [title,body] of [['11월 교직원 회의 안내','11월 교직원 회의를 수요일 16시에 시청각실에서 엽니다. 안건이 있으면 화요일까지 알려 주세요.'],['2학기 2차 지필평가 원안 제출','원안은 다음 주 금요일까지 교육연구부로 제출해 주세요. 서술형 채점 기준표를 함께 내 주세요.'],['학부모 상담 주간 운영 계획','상담 주간 동안 학급별 상담 시간표를 작성해 공유해 주세요.']])await admin.request('messages','POST',{clientId:randomUUID(),title,body,recipientIds:ids.slice(0,6),attachmentIds:[],all:false});});
  await seed('received messages',async()=>{for(const [title,body] of [['교과 협의회 일정 조정 부탁드립니다','목요일 7교시 교과 협의회를 금요일로 옮길 수 있을지 확인 부탁드립니다.'],['체험학습 인솔 교사 명단','다음 달 체험학습 인솔 교사 명단을 보내 드립니다. 확인 후 수정할 부분을 알려 주세요.'],['방송실 사용 신청','금요일 조회 시간에 방송실을 사용하려고 합니다.'],['아주 긴 제목이 목록에서 어떻게 보이는지 확인하기 위한 가상의 메시지 제목입니다 2026학년도 2학기 교육과정 운영 평가 설문 결과 공유','본문입니다.']])await colleague.request('messages','POST',{clientId:randomUUID(),title,body,recipientIds:[admin.user.id],attachmentIds:[],all:false});});
  await seed('chat',async()=>{const room=await admin.request('chats','POST',{memberIds:ids.slice(0,3),name:'1학년 담임 협의'});const roomId=room.id??room;for(const body of ['내일 조회 때 가정통신문 나눠 주세요.','네, 확인했습니다.'])await admin.request('chats/'+roomId+'/messages','POST',{clientId:randomUUID(),body,attachmentIds:[]});});
  await seed('survey',async()=>{await admin.request('surveys','POST',{title:'교직원 연수 희망 날짜 조사',description:'가능한 날짜를 골라 주세요.',deadline:Date.now()+5*86400000,targetIds:[admin.user.id,...ids.slice(0,5)],all:false,questions:[{text:'가능한 날짜',kind:'choice',required:true,options:['11월 18일(수)','11월 20일(금)']},{text:'바라는 연수 주제',kind:'text',required:false,options:[]}]});});
  // All-day events run from Korean midnight to a later Korean midnight.
  const day=n=>new Date(Date.now()+9*3600000+n*86400000).toISOString().slice(0,10)+'T00:00:00+09:00';
  await seed('events',async()=>{for(const [title,from,to] of [['2학기 2차 지필평가',6,9],['학교운영위원회',2,2],['교직원 연수',1,1]])await admin.request('office/events','POST',{clientId:randomUUID().replaceAll('-',''),title,description:'가상 일정',start:day(from),end:day(to+1),allDay:true});});
  await seed('rooms',async()=>{for(const [name,location] of [['과학실 1','본관 2층'],['컴퓨터실','본관 3층'],['시청각실','별관 1층']])await admin.request('office/rooms','POST',{name,location});});
  const db=new DatabaseSync(path.join(fixture,'server','school.db'));
  try {
    const {Id:owner,Name:teacherName}=db.prepare("SELECT Id,Name FROM Users WHERE Username='admin'").get();
    const id=()=>randomUUID().replaceAll('-',''),batch=id(),client=id();
    // A school-sized week: 18 classes and 48 teachers, the size teachers really see.
    const rooms=Array.from({length:18},(_,c)=>({id:id(),name:`${Math.floor(c/6)+1}학년 ${c%6+1}반`,grade:Math.floor(c/6)+1}));
    const subjects=['국어','수학','영어','통합사회','통합과학','체육','음악 감상과 비평','미술 창작'];
    const monday=new Date(Date.now()+9*3600000);monday.setUTCDate(monday.getUTCDate()-(monday.getUTCDay()+6)%7);
    const date=n=>new Date(monday.getTime()+n*86400000).toISOString().slice(0,10);
    const cells=rooms.flatMap((room,c)=>Array.from({length:35},(_,i)=>{const t=(c+i*5)%48;return{classId:room.id,className:room.name,day:Math.floor(i/7)+1,period:i%7+1,subject:subjects[t%subjects.length],teacherId:t?'':owner,teacherName:t?'가상교사 '+String(t).padStart(2,'0'):teacherName};}));
    for(const room of rooms)db.prepare('INSERT INTO TimetableClasses VALUES(?,?,?)').run(room.id,room.name,room.grade);
    const payload={id:batch,revision:1,owner,clientId:client,kind:'base',year:monday.getUTCFullYear(),semester:2,start:date(0),end:date(6),date:null,fileName:'가상시간표.xlsx',publishedAt:Date.now(),withdrawn:false,cells};
    db.prepare('INSERT INTO TimetableBatches(Revision,Id,Owner,ClientId,Payload,Delivered) VALUES(1,?,?,?,?,1)').run(batch,owner,client,JSON.stringify(payload));
  } finally { db.close(); }
  ui=spawn(sdk,[path.join(root,'src/SchoolMessenger.Desktop/bin/Debug/net10.0-windows/SchoolMessenger.dll'),'--verify-ui'],{cwd:root,windowsHide:true,env:{...process.env,SCHOOL_UI_SERVER:address,SCHOOL_UI_USER:'admin',SCHOOL_UI_PASSWORD:password,SCHOOL_UI_OUTPUT:path.join(output,'client-preview.png'),SCHOOL_UI_LOCALDIR:path.join(fixture,'client'),YY_SCREEN_TOUR:output}});
  let timeout;const code=await Promise.race([new Promise((resolve,reject)=>{ui.once('error',reject);ui.once('exit',resolve);}),new Promise(resolve=>{timeout=setTimeout(()=>resolve(-1),17*60000);})]);clearTimeout(timeout);
  const report=existsSync(path.join(output,'report.txt'))?readFileSync(path.join(output,'report.txt'),'utf8'):'';
  console.log(report.split('\n')[0]);
  assert.equal(code,0,existsSync('artifacts/ui-error.txt')?readFileSync('artifacts/ui-error.txt','utf8'):'Screen tour did not finish');
  // Sideways scrolling and text cut off at the size a window opens in are faults; a window squeezed to its smallest is reported, not failed.
  const faults=report.split('\n').slice(1).filter(l=>!l.startsWith('!')&&/글씨 잘림|자리 부족|가로 스크롤/.test(l)&&!l.includes('가장 작게 줄인 창'));
  for(const line of faults)console.log('FAULT '+line.split('\t').filter((_,i)=>i!==3).join(' | '));
  console.log(faults.length+' screens with layout faults. Pictures: '+path.join(output,'index.html'));
}finally{if(ui?.exitCode===null)ui.kill();if(server.exitCode===null){const closed=new Promise(r=>server.once('exit',r));server.kill();await closed;}}
