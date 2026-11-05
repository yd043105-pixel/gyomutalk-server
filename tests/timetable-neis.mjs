import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {mkdirSync,existsSync} from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import zlib from 'node:zlib';

// NEIS timetable workbooks, built here with synthetic names in the same layout: pages repeat their headers, a day runs across pages,
// and the changed-lessons file lists only what moved.
const root=process.cwd(),sdk=path.join(root,'.tools/dotnet/dotnet.exe'),dotnet=existsSync(sdk)?sdk:'dotnet';
const data=path.join(process.env.YY_TEST_DATA_ROOT??path.join(root,'.test-data'),'timetable-neis-'+randomUUID());mkdirSync(data,{recursive:true});
const password='Synthetic-'+randomUUID();
const check=name=>console.log('PASS '+name);
function zip(files){const locals=[],centrals=[];let offset=0;for(const [name,text] of Object.entries(files)){const n=Buffer.from(name),d=Buffer.from(text),h=Buffer.alloc(26);h.writeUInt16LE(20,0);h.writeUInt16LE(0x0800,2);h.writeUInt32LE(zlib.crc32(d),10);h.writeUInt32LE(d.length,14);h.writeUInt32LE(d.length,18);h.writeUInt16LE(n.length,22);const local=Buffer.concat([Buffer.from([0x50,0x4b,3,4]),h,n,d]);locals.push(local);const c=Buffer.alloc(46);c.writeUInt32LE(0x02014b50,0);c.writeUInt16LE(20,4);c.writeUInt16LE(20,6);c.writeUInt16LE(0x0800,8);c.writeUInt32LE(zlib.crc32(d),16);c.writeUInt32LE(d.length,20);c.writeUInt32LE(d.length,24);c.writeUInt16LE(n.length,28);c.writeUInt32LE(offset,42);centrals.push(Buffer.concat([c,n]));offset+=local.length;}const directory=Buffer.concat(centrals),end=Buffer.alloc(22);end.writeUInt32LE(0x06054b50,0);end.writeUInt16LE(centrals.length,8);end.writeUInt16LE(centrals.length,10);end.writeUInt32LE(directory.length,12);end.writeUInt32LE(offset,16);return Buffer.concat([...locals,directory,end]);}
const column=n=>{let s='';n++;while(n>0){n--;s=String.fromCharCode(65+n%26)+s;n=Math.floor(n/26);}return s;};
const escape=t=>String(t).replace(/&/g,'&amp;').replace(/</g,'&lt;');
function book(rows,merges=[]){
 const sheet=rows.map((r,i)=>`<row r="${i+1}">${Object.entries(r).map(([c,v])=>`<c r="${column(+c)}${i+1}" t="inlineStr"><is><t>${escape(v)}</t></is></c>`).join('')}</row>`).join('');
 return zip({'[Content_Types].xml':'<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>',
  'xl/workbook.xml':'<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>',
  'xl/_rels/workbook.xml.rels':'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
  'xl/worksheets/sheet1.xml':`<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>${sheet}</sheetData>${merges.length?`<mergeCells>${merges.map(m=>`<mergeCell ref="${m}"/>`).join('')}</mergeCells>`:''}</worksheet>`});
}
const kst=new Date(Date.now()+9*3_600_000),year=kst.getUTCFullYear(),semester=kst.getUTCMonth()+1>=8||kst.getUTCMonth()+1<3?2:1;
const dayText=d=>d.toISOString().slice(0,10);
// Monday of next week, so every day in the file is ahead and inside the school year of the title.
const monday=new Date(Date.UTC(year,kst.getUTCMonth(),kst.getUTCDate()));monday.setUTCDate(monday.getUTCDate()-((monday.getUTCDay()+6)%7)+7);
const date=n=>{const d=new Date(monday);d.setUTCDate(d.getUTCDate()+n);return d;};
const caption=n=>`${dayText(date(n)).replaceAll('-','.')}.\n(${'월화수목금'[n]}요일)`;
// Base: Monday-Friday, classes 1-1 and 1-2, first two periods. Class 1's self-governance hour is taught by its homeroom teacher.
const lesson=(c,d,p)=>c===1?[['자율·자치활동(가상담임)','국어(가상교사)'],['수학(계정없음)','영어(가상담임)'],['국어(가상교사)','수학(계정없음)'],['영어(가상담임)','국어(가상교사)'],['수학(계정없음)','영어(가상담임)']][d][p]:[['자율·자치활동(둘째담임)','수학(계정없음)'],['국어(가상교사)','영어(둘째담임)'],['영어(둘째담임)','국어(가상교사)'],['수학(계정없음)','영어(둘째담임)'],['국어(가상교사)','수학(계정없음)']][d][p];
const baseRows=[{},{2:'2030.01.01.'},{1:`${year} 년 ${semester}학기 시간표`},{},{3:'월',12:'화',21:'수',30:'목',39:'금',48:'토',57:'일'},{}];
for(const start of [3,12,21,30,39,48,57])for(let p=0;p<9;p++)baseRows[5][start+p]=p+1+(start===3?'.0':'');
for(const c of [1,2]){const row={2:`${c}(1학년 ${c}반)`};if(c===1)row[1]='1학년';for(let d=0;d<5;d++)for(let p=0;p<2;p++)row[3+d*9+p]=lesson(c,d,p);baseRows.push(row);}
baseRows.push({2:'도서관 m'},{},{20:'가상고등학교'});
const base=book(baseRows,['B7:B8']);
// Weekly: two pages; Wednesday starts on the first page and goes on to the second. On Thursday 1-1's first period moved.
function weekly(title,cell,periodLabel){
 const rows=[{},{},{},{1:title},{1:`${year} 학년도 ${semester} 학기 1 학년`}],merges=[];
 const head=()=>{rows.push({1:'일자',2:'교시',3:'1학년'});rows.push({3:'1(1학년 1반)',4:'2(1학년 2반)'});};
 head();
 for(const [d,periods] of [[0,[1,2]],[1,[1,2]],[2,[1]]])periods.forEach((p,i)=>{const row={2:periodLabel(p),3:cell(1,d,p),4:cell(2,d,p)};if(i===0){row[1]=caption(d);if(periods.length>1)merges.push(`B${rows.length+1}:B${rows.length+periods.length}`);}rows.push(row);});
 rows.push({8:'1',10:'/',11:'2',14:'가상고등학교'},{},{});
 head();
 for(const [d,periods] of [[2,[2]],[3,[1,2]],[4,[1,2]]])periods.forEach((p,i)=>{const row={2:periodLabel(p),3:cell(1,d,p),4:cell(2,d,p)};if(i===0){row[1]=caption(d);if(periods.length>1)merges.push(`B${rows.length+1}:B${rows.length+periods.length}`);}rows.push(row);});
 rows.push({8:'2',10:'/',11:'2',14:'가상고등학교'});
 return book(rows,merges);
}
const moved=(c,d,p)=>c===1&&d===3&&p===1?'음악(가상교사)':lesson(c,d,p-1);
const classroom=weekly('전체시간표 (강의실)',moved,p=>p+'.0');
const changes=weekly('전체시간표 (변경수업)',(c,d,p)=>c===1&&d===3&&p===1?'음악(가상교사)':'',p=>p+'교시');
const noChanges=weekly('전체시간표 (변경수업)',()=>'',p=>p+'교시');
const oneDay=book([{},{},{},{1:'전체시간표 (강의실)'},{1:`${year} 학년도 ${semester} 학기 1 학년`},{1:'일자',2:'교시',3:'1학년'},{3:'1(1학년 1반)',4:'2(1학년 2반)'},{1:caption(4),2:'1.0',3:'과학(가상교사)',4:lesson(2,4,0)},{2:'2.0',3:lesson(1,4,1),4:lesson(2,4,1)}],['B8:B9']);

async function port(){const s=net.createServer();await new Promise(r=>s.listen(0,'127.0.0.1',r));const p=s.address().port;await new Promise(r=>s.close(r));return p;}
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
 async login(username='admin'){this.csrf=(await this.request('session')).csrfToken;await this.request('login','POST',{username,password,rememberLogin:false});const s=await this.request('session');this.csrf=s.csrfToken;this.user=s.user;return this;}
 upload(bytes,name,status=200){const form=new FormData();form.set('file',new Blob([bytes]),name);return this.request('timetable/import','POST',form,status);}
}
const base0='http://127.0.0.1:'+await port(),dll=path.join(root,'src/SchoolMessenger.Server/bin/Debug/net10.0/SchoolMessenger.Server.dll');
const server=spawn(dotnet,[dll,'--urls',base0],{cwd:path.dirname(dll),env:{...process.env,ASPNETCORE_ENVIRONMENT:'Development',School__DataDirectory:data,School__AdminPassword:password,School__LoginLimitPerMinute:'300'},windowsHide:true});
let logs='';server.stdout.on('data',b=>logs+=b);server.stderr.on('data',b=>logs+=b);
try{
 for(let i=0;;i++){try{if((await fetch(base0+'/health')).ok)break;}catch{}if(i>150||server.exitCode!==null)throw Error('Server did not start\n'+logs);await new Promise(r=>setTimeout(r,100));}
 const admin=await new Client(base0).login();
 const person=async(username,name)=>{await admin.request('admin/users','POST',{username,name,department:'가상부서',password,isAdmin:false,canBroadcast:false});return new Client(base0).login(username);};
 const homeroom=await person('homeroom-a','가상담임'),teacher=await person('teacher-a','가상교사'),duty=await person('duty-a','가상담당');

 await teacher.upload(base,'base.xlsx',403);
 await admin.request('admin/timetable-managers','POST',{userId:duty.user.id,enabled:true});
 assert.equal((await duty.request('timetable/setup')).canManage,true);
 check('uploading takes the timetable duty, which an administrator can hand to any staff member');

 const baseDraft=await duty.upload(base,'1학년 기초시간표.xlsx');assert.equal(baseDraft.workbook.kind,'base');
 const room=baseDraft.workbook.cells.filter(c=>c.classKey==='1학년 1반');assert.equal(room.find(c=>c.day===1&&c.period===1).subject,'자율·자치활동');assert.equal(room.find(c=>c.day===1&&c.period===2).teacher,'가상교사');
 assert.ok(!baseDraft.workbook.cells.some(c=>c.classKey.includes('도서관')));
 check('a base timetable is recognised by itself, with rooms that are not classes left out');
 const classroomDraft=await duty.upload(classroom,'1학년 강의실별 수업 조회(주간).xlsx');assert.equal(classroomDraft.workbook.kind,'daily');
 assert.deepEqual([...new Set(classroomDraft.workbook.cells.map(c=>c.date))].sort(),[0,1,2,3,4].map(n=>dayText(date(n))));
 assert.equal(classroomDraft.workbook.cells.find(c=>c.classKey==='1학년 1반'&&c.date===dayText(date(2))&&c.period===2).subject,lesson(1,2,1).split('(')[0]);
 check('a weekly classroom file reads every day, across pages, even when a day continues on the next page');
 const changeDraft=await duty.upload(changes,'1학년 변경 수업 조회(주간).xlsx');assert.equal(changeDraft.workbook.kind,'changes');assert.equal(changeDraft.workbook.cells.length,1);
 const emptyDraft=await duty.upload(noChanges,'변경 없음.xlsx');assert.equal(emptyDraft.workbook.cells.length,0);
 check('a changed-lessons file keeps only what moved, and one with no changes is not an error');

 const first=await duty.request('timetable/preview','POST',{draftId:baseDraft.draftId,draftIds:[baseDraft.draftId],start:'',end:'',classes:[],teachers:[]});
 assert.equal(first.changedCount,0);assert.deepEqual(first.unlinked,['계정없음','둘째담임']);
 await duty.request('timetable/publish','POST',{candidateId:first.candidateId,clientId:randomUUID().replaceAll('-','')});
 assert.deepEqual(await teacher.request('timetable/notices'),[]);
 check('the first base timetable links teachers by name, keeps the rest by name and tells nobody it changed');

 const thursday=dayText(date(3)),friday=dayText(date(4));
 const firstClass=(await duty.request('timetable/setup')).classes.find(c=>c.name==='1학년 1반').id;
 const preview=await duty.request('timetable/preview','POST',{draftId:changeDraft.draftId,draftIds:[changeDraft.draftId],start:'',end:'',classes:[],teachers:[]});
 assert.equal(preview.changedCount,1);assert.equal(preview.changes[0].date,thursday);assert.equal(preview.changes[0].subject,'음악');
 const client=randomUUID().replaceAll('-','');
 const published=await duty.request('timetable/publish','POST',{candidateId:preview.candidateId,clientId:client});
 assert.equal((await duty.request('timetable/publish','POST',{candidateId:preview.candidateId,clientId:client})).id,published.id);
 const day=await teacher.request(`timetable/day?date=${thursday}&classId=${firstClass}`);
 const changed=day.slots.find(s=>s.period===1);assert.equal(changed.changed,true);assert.equal(changed.subject,'음악');assert.equal(changed.teacherName,'가상교사');assert.equal(changed.beforeSubject,'영어');
 const kept=day.slots.find(s=>s.period===2);assert.equal(kept.changed,false);assert.equal(kept.subject,'국어');assert.equal(kept.teacherName,'가상교사');
 check('a changed lesson lays over the base timetable and is marked; everything else stays, with the teacher name beside the subject');

 const other=await teacher.request(`timetable/day?date=${thursday}&classId=${(await duty.request('timetable/setup')).classes.find(c=>c.name==='1학년 2반').id}`);assert.ok(other.slots.every(s=>!s.changed));
 const notices=await teacher.request('timetable/notices');assert.equal(notices.length,1);assert.match(notices[0].title,/1학년 1반 1교시 영어\(가상담임\)|1학년 1반 1교시 .*→ 음악\(가상교사\)/);
 assert.equal((await homeroom.request('timetable/notices')).length,1);
 check('the teachers involved and the homeroom teacher get a notice saying which lesson moved');

 const setup=await homeroom.request('timetable/setup');assert.deepEqual(setup.myClasses,[firstClass]);
 assert.deepEqual((await teacher.request('timetable/setup')).myClasses,[]);
 await admin.request('admin/timetable-homerooms','POST',{classId:firstClass,teacherId:teacher.user.id});
 assert.deepEqual((await teacher.request('timetable/setup')).myClasses,[firstClass]);assert.deepEqual((await homeroom.request('timetable/setup')).myClasses,[]);
 await admin.request('admin/timetable-homerooms','POST',{classId:firstClass,teacherId:homeroom.user.id});
 check('a homeroom teacher is read from the timetable, and an administrator can set another one');

 const late=await person('late-a','계정없음');
 const mine=await late.request(`timetable/day?date=${thursday}&mine=true`);assert.ok(mine.slots.some(s=>s.subject==='수학'));
 check('an account made after the upload finds its lessons by name');

 const same=await duty.upload(classroom,'1학년 강의실별 수업 조회(주간).xlsx');
 const same1=await duty.request('timetable/preview','POST',{draftId:same.draftId,draftIds:[same.draftId],start:'',end:'',classes:[],teachers:[]});
 assert.equal(same1.changedCount,0);assert.equal(same1.batches.length,5);
 await duty.request('timetable/publish','POST',{candidateId:same1.candidateId,clientId:randomUUID().replaceAll('-','')});
 const again=await teacher.request(`timetable/day?date=${thursday}&classId=${firstClass}`);assert.equal(again.slots.find(s=>s.period===1).changed,true);
 assert.equal((await teacher.request('timetable/notices')).length,1);
 check('the classroom file for the same week gives the same timetable and no new notice');

 const single=await duty.upload(oneDay,'1학년 강의실별 수업 조회(1일).xlsx');assert.equal(single.workbook.date,friday);
 const one=await duty.request('timetable/preview','POST',{draftId:single.draftId,draftIds:[single.draftId],start:'',end:'',classes:[],teachers:[]});
 assert.equal(one.changedCount,1);await duty.request('timetable/publish','POST',{candidateId:one.candidateId,clientId:randomUUID().replaceAll('-','')});
 const fri=await teacher.request(`timetable/day?date=${friday}&classId=${firstClass}`);assert.equal(fri.slots.find(s=>s.period===1).subject,'과학');assert.equal(fri.slots.find(s=>s.period===1).changed,true);
 check('a one-day classroom file works the same way');

 const empty=await duty.request('timetable/preview','POST',{draftId:emptyDraft.draftId,draftIds:[emptyDraft.draftId],start:'',end:'',classes:[],teachers:[]},undefined).catch(e=>e);
 assert.ok(String(empty.message??'').includes('400')||String(empty.message??'').includes('반영할'));
 check('uploading only an empty changes file says there is nothing to apply');
}catch(e){console.error(logs.slice(-2500));throw e;}
finally{if(server.exitCode===null){server.kill();await new Promise(r=>server.once('exit',r));}}
