import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {mkdirSync,existsSync} from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import zlib from 'node:zlib';
import {startScriptHost} from './course-script-host.mjs';

// 수강신청 from roster to results, against a local stand-in for Google that runs the very script a school pastes.
// Every name here is synthetic.
const root=process.cwd(),sdk=path.join(root,'.tools/dotnet/dotnet.exe'),dotnet=existsSync(sdk)?sdk:'dotnet';
const data=path.join(process.env.YY_TEST_DATA_ROOT??path.join(root,'.test-data'),'course-selection-'+randomUUID());mkdirSync(data,{recursive:true});
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
 upload(bytes,name,status=200){const form=new FormData();form.set('file',new Blob([bytes]),name);return this.request('courses/students/read','POST',form,status);}
}
const bytes=s=>Buffer.from(s.replace(/-/g,'+').replace(/_/g,'/'),'base64');
const me=hash=>JSON.parse(bytes(hash).toString());
const day=n=>new Date(Date.now()+9*3_600_000+n*86_400_000).toISOString().slice(0,10);

const host=await startScriptHost(await port());
const base0='http://127.0.0.1:'+await port(),dll=path.join(root,'src/SchoolMessenger.Server/bin/Debug/net10.0/SchoolMessenger.Server.dll');
let server,logs='';
async function start(){
 server=spawn(dotnet,[dll,'--urls',base0],{cwd:path.dirname(dll),env:{...process.env,ASPNETCORE_ENVIRONMENT:'Development',School__DataDirectory:data,School__AdminPassword:password,School__LoginLimitPerMinute:'300',Courses__TestScriptUrl:host.url},windowsHide:true});
 server.stdout.on('data',b=>logs+=b);server.stderr.on('data',b=>logs+=b);
 for(let i=0;;i++){try{if((await fetch(base0+'/health')).ok)break;}catch{}if(i>150||server.exitCode!==null)throw Error('Server did not start\n'+logs);await new Promise(r=>setTimeout(r,100));}
}
async function stop(){if(server?.exitCode===null&&server.signalCode===null){const done=new Promise(r=>server.once('exit',r));server.kill();await done;}}
try{
 await start();
 const admin=await new Client(base0).login();
 const person=async(username,name)=>{await admin.request('admin/users','POST',{username,name,department:'가상부서',password,isAdmin:false,canBroadcast:false});return new Client(base0).login(username);};
 const duty=await person('course-a','가상교육과정'),teacher=await person('teacher-a','가상교사');

 assert.equal((await teacher.request('courses/access')).manager,false);await teacher.request('courses/setup','GET',undefined,403);
 assert.equal((await admin.request('courses/access')).manager,false);await admin.request('courses/students','GET',undefined,403);
 await admin.request('admin/office/permissions','POST',{userId:duty.user.id,calendarManager:false,roomManager:false,courseManager:true});
 await admin.request('admin/office/permissions','POST',{userId:duty.user.id,calendarManager:true,roomManager:false});
 assert.equal((await duty.request('courses/access')).manager,true);
 assert.equal((await admin.request('admin/office/permissions')).find(p=>p.userId===duty.user.id).courseManager,true);
 check('only the curriculum duty opens 수강신청, administrators included, and an older permission request keeps the duty');

 const {code}=await duty.request('courses/script');assert.match(code,/var SECRET = '[A-Za-z0-9_-]{43}'/);assert.match(code,/var VERSION = 1;/);
 host.load(code);
 await duty.request('courses/connect','POST',{url:'https://example.com/exec'},400);
 assert.equal((await duty.request('courses/connect','POST',{url:host.url})).scriptCurrent,true);
 assert.equal((await duty.request('courses/setup')).connected,true);
 check('the program writes a school-specific script and checks the pasted web app address by talking to it');

 const neis=book([{0:'2026학년도 학생 명렬표'},{},{0:'학년',1:'반',2:'번호',3:'성명',4:'성별'},
  {0:'2',1:'1',2:'1',3:'가상일',4:'남'},{0:'2',1:'1',2:'2',3:'가상이',4:'여'},{0:'2',1:'2',2:'1',3:'가상삼',4:'남'},{0:'2',1:'2',2:'2',3:'가상사',4:'여'},{},{0:'계',3:'4명'}]);
 const read=await duty.upload(neis,'2학년 명렬표.xlsx');
 assert.equal(read.students.length,4);assert.equal(read.skipped,1);assert.deepEqual(read.students[0],{grade:2,classNo:1,number:1,name:'가상일'});
 await duty.upload(Buffer.from('not a workbook'),'명단.xlsx',400);
 await duty.upload(Buffer.from([0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1]),'명단.xls',400);
 const pasted=await duty.request('courses/students/read','POST',{text:'2학년 명단\n학번\t이름\n20301\t가상오\n20302\t가상육\n'});
 assert.deepEqual(pasted.students.map(s=>[s.grade,s.classNo,s.number,s.name]),[[2,3,1,'가상오'],[2,3,2,'가상육']]);
 assert.match((await duty.upload(Buffer.from('﻿반,번호,"성명"\n1,1,가상칠\n','utf8'),'1학년.csv',400)).error,/머리글/);
 assert.deepEqual((await duty.upload(Buffer.from('1학년 명단,,\n반,번호,성명\n1,1,가상칠\n','utf8'),'1학년.csv')).students,[{grade:1,classNo:1,number:1,name:'가상칠'}]);
 check('a NEIS list is read from its header row (학년·반·번호·성명, 학번·성명, or a grade in the title), from .xlsx, .csv or pasted rows');

 assert.equal((await duty.request('courses/students/import','POST',{grades:[2],students:[...read.students,...pasted.students]})).added,6);
 await duty.request('courses/students','POST',{grade:2,classNo:3,number:3,name:'가상팔'});
 await duty.request('courses/students','POST',{grade:2,classNo:3,number:3,name:'가상팔'},409);
 const eighth=(await duty.request('courses/students')).find(s=>s.name==='가상팔');await duty.request(`courses/students/${eighth.id}/delete`,'POST');
 assert.equal((await duty.request('courses/students')).length,6);
 check('the roster is filled from the list and a student can be added or removed one at a time');

 const groups=[{name:'선택군 A',pick:2,courses:['물리학Ⅱ','화학Ⅱ','생명과학Ⅱ']},{name:'선택군 B',pick:1,courses:['경제','정치와 법']}];
 await duty.request('courses/rounds','POST',{title:'3학년 선택과목',grade:2,closes:day(7),groups:[{name:'선택군 A',pick:4,courses:['물리학Ⅱ','화학Ⅱ']}]},400);
 const {id}=await duty.request('courses/rounds','POST',{title:'3학년 선택과목',grade:2,closes:day(7),groups});
 assert.equal((await duty.request(`courses/rounds/${id}/publish`,'POST')).students,6);
 assert.ok(!JSON.stringify([host.rows('students'),host.rows('rounds')]).includes('가상'));
 const links=await duty.request(`courses/rounds/${id}/links`);assert.equal(links.length,6);
 const link=name=>{const l=links.find(l=>l.name===name);const u=new URL(l.link);assert.equal(u.origin+u.pathname,host.url);return{token:u.searchParams.get('t'),me:me(u.hash.slice(1))};};
 const one=link('가상일');assert.deepEqual(one.me,{n:'가상일',g:2,c:1,no:1});assert.ok(!host.context.load(one.token).error);
 assert.ok(host.rows('students').every(r=>r.length===2&&/^[A-Za-z0-9_-]{22}$/.test(r[0])));
 check('each student link carries the name after #; Google gets only random tokens, never a name or class');

 assert.equal(host.context.submit(one.token,[['물리학Ⅱ'],['경제']]).error,'invalid');
 assert.equal(host.context.submit(one.token,[['물리학Ⅱ','물리학Ⅱ'],['경제']]).error,'invalid');
 assert.equal(host.context.submit('x'.repeat(22),[['물리학Ⅱ','화학Ⅱ'],['경제']]).error,'gone');
 assert.ok(host.context.submit(one.token,[['물리학Ⅱ','화학Ⅱ'],['경제']]).ok);
 assert.ok(host.context.submit(link('가상오').token,[['화학Ⅱ','생명과학Ⅱ'],['정치와 법']]).ok);
 assert.ok(host.context.submit(one.token,[['물리학Ⅱ','생명과학Ⅱ'],['경제']]).ok);
 assert.equal(host.rows('answers').length,2);
 check('the student page takes exactly the number asked from each group and keeps one answer per student, changeable until the deadline');

 host.rows('answers').push([link('가상삼').token,id,JSON.stringify([['천문학','화학Ⅱ'],['경제']]),String(Date.now())]);
 let result=await duty.request(`courses/rounds/${id}/refresh`,'POST');
 const count=(g,c)=>result.groups[g].courses.find(x=>x.name===c).count;
 assert.deepEqual([count(0,'물리학Ⅱ'),count(0,'화학Ⅱ'),count(0,'생명과학Ⅱ'),count(1,'경제'),count(1,'정치와 법')],[1,1,2,1,1]);
 assert.deepEqual(result.students.filter(s=>!s.picks).map(s=>s.name),['가상이','가상삼','가상사','가상육']);
 assert.deepEqual(result.students.find(s=>s.name==='가상일').picks,[['물리학Ⅱ','생명과학Ⅱ'],['경제']]);
 assert.equal((await duty.request('courses/rounds'))[0].submitted,2);
 check('results count each course per group and list who has not submitted; an answer outside the courses is not counted');

 await duty.request(`courses/rounds/${id}`,'POST',{title:'3학년 선택과목',grade:2,closes:day(7),groups:[groups[0]]},409);
 await duty.request(`courses/rounds/${id}`,'POST',{title:'3학년 선택과목(연장)',grade:2,closes:day(9),groups});
 assert.equal(host.context.load(one.token).title,'3학년 선택과목(연장)');
 await duty.request(`courses/rounds/${id}`,'POST',{title:'3학년 선택과목(연장)',grade:2,closes:day(9),groups:groups.map((g,i)=>({...g,semester:i+1}))});
 assert.deepEqual((await duty.request(`courses/rounds/${id}/results`)).round.groups.map(g=>g.semester),[1,2]);
 await duty.request(`courses/rounds/${id}`,'POST',{title:'3학년 선택과목(연장)',grade:2,closes:day(9),groups:[{...groups[0],semester:3},groups[1]]},400);
 check('after answers arrive only the name, deadline and term change, and the change reaches the student page at once');

 await duty.request(`courses/rounds/${id}/closed`,'POST',{group:0,course:'물리학Ⅱ',closed:true});
 assert.deepEqual(host.context.load(one.token).groups[0].closed,['물리학Ⅱ']);assert.ok(host.context.load(one.token).picks[0].includes('물리학Ⅱ'));
 assert.equal(host.context.submit(one.token,[['물리학Ⅱ','화학Ⅱ'],['경제']]).error,'invalid');
 await duty.request(`courses/rounds/${id}/closed`,'POST',{group:0,course:'화학Ⅱ',closed:true},409);
 await duty.request(`courses/rounds/${id}`,'POST',{title:'3학년 선택과목(연장)',grade:2,closes:day(9),groups:groups.map((g,i)=>({...g,semester:i+1}))});
 assert.deepEqual((await duty.request(`courses/rounds/${id}/results`)).round.groups[0].closed,['물리학Ⅱ']);
 await duty.request(`courses/rounds/${id}/closed`,'POST',{group:0,course:'물리학Ⅱ',closed:false});
 assert.deepEqual(host.context.load(one.token).groups[0].closed,[]);
 check('a course can be closed (폐강) after answers come in: no one can pick it, earlier answers stay, editing keeps it closed, and it can be opened again');

 const moved=[...read.students.filter(s=>s.name!=='가상이'),...pasted.students,{grade:2,classNo:1,number:2,name:'가상구'}];
 assert.ok(host.context.submit(link('가상이').token,[['물리학Ⅱ','화학Ⅱ'],['경제']]).ok);
 const swap=await duty.request('courses/students/import','POST',{grades:[2],students:moved});assert.deepEqual([swap.added,swap.removed],[1,1]);
 assert.equal(host.context.load(link('가상이').token).error,'gone');assert.ok(!host.rows('answers').some(r=>r[0]===link('가상이').token));
 const sixth=(await duty.request('courses/students')).find(s=>s.name==='가상육');await duty.request(`courses/students/${sixth.id}/delete`,'POST');
 assert.equal(host.context.load(link('가상육').token).error,'gone');assert.ok(host.context.load(one.token).picks);
 result=await duty.request(`courses/rounds/${id}/results`);assert.ok(result.students.some(s=>s.name==='가상구'&&s.linked));assert.ok(result.students.find(s=>s.name==='가상일').picks);
 check('loading the list again keeps everyone unchanged with their link and answer; a student taken off loses their link and answer on Google at once');

 const late=await duty.request('courses/rounds','POST',{title:'지난 신청',grade:2,closes:day(-1),groups});await duty.request(`courses/rounds/${late.id}/publish`,'POST');
 const lateLinks=await duty.request(`courses/rounds/${late.id}/links`);
 assert.equal(host.context.submit(new URL(lateLinks[0].link).searchParams.get('t'),[['물리학Ⅱ','화학Ⅱ'],['경제']]).error,'closed');
 check('a round past its deadline refuses answers');

 await teacher.request(`courses/rounds/${id}/links`,'GET',undefined,403);
 await admin.request('admin/features','POST',{name:'courses',enabled:false});
 assert.equal((await duty.request('courses/access')).manager,false);await duty.request(`courses/rounds/${id}/results`,'GET',undefined,403);
 await admin.request('admin/features','POST',{name:'courses',enabled:true});
 assert.equal((await duty.request(`courses/rounds/${id}/results`)).students.length,5);
 check('an administrator can switch 수강신청 off for the school; its data comes back when switched on');

 await duty.request(`courses/rounds/${late.id}/delete`,'POST');
 assert.ok(!host.rows('rounds').some(r=>r[0]===late.id)&&!host.rows('students').some(r=>r[1]===late.id));
 assert.equal((await duty.request('courses/rounds')).length,1);
 check('deleting a round takes it off Google too');

 await stop();await start();
 assert.equal((await (await new Client(base0).login('course-a')).request(`courses/rounds/${id}/results`)).students.filter(s=>s.picks).length,2);
 check('everything survives a server restart');
}catch(error){console.error(logs);throw error;}
finally{await stop();await host.close();}
