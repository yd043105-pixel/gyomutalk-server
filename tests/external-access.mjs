import assert from 'node:assert/strict';
import {spawn,spawnSync} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {mkdirSync,existsSync} from 'node:fs';
import http from 'node:http';
import zlib from 'node:zlib';
import net from 'node:net';
import path from 'node:path';

// Outside the school only the read-only list opens, whatever the account may do inside.
const root=process.cwd(),sdk=path.join(root,'.tools/dotnet/dotnet.exe'),dotnet=existsSync(sdk)?sdk:'dotnet';
const data=path.join(process.env.YY_TEST_DATA_ROOT??path.join(root,'.test-data'),'external-'+randomUUID());mkdirSync(data,{recursive:true});
const password='Synthetic-'+randomUUID(),host='gyomutalk.school.example';
const check=name=>console.log('PASS '+name);
async function freePort(){const s=net.createServer();await new Promise(r=>s.listen(0,'127.0.0.1',r));const p=s.address().port;await new Promise(r=>s.close(r));return p;}
// A stored (uncompressed) .xlsx is enough for the timetable reader.
function zip(files){
 const locals=[],centrals=[];let offset=0;
 for(const [name,text] of Object.entries(files)){
  const nameBytes=Buffer.from(name),data=Buffer.from(text),crc=zlib.crc32(data);
  const shared=Buffer.alloc(26);shared.writeUInt16LE(20,0);shared.writeUInt16LE(0x0800,2);shared.writeUInt32LE(crc,10);shared.writeUInt32LE(data.length,14);shared.writeUInt32LE(data.length,18);shared.writeUInt16LE(nameBytes.length,22);
  const local=Buffer.concat([Buffer.from([0x50,0x4b,3,4]),shared,nameBytes,data]);locals.push(local);
  const tail=Buffer.alloc(14);tail.writeUInt32LE(offset,10);
  centrals.push(Buffer.concat([Buffer.from([0x50,0x4b,1,2,20,0]),shared,tail,nameBytes]));offset+=local.length;
 }
 const central=Buffer.concat(centrals),end=Buffer.alloc(22);end.writeUInt32LE(0x06054b50,0);end.writeUInt16LE(centrals.length,8);end.writeUInt16LE(centrals.length,10);end.writeUInt32LE(central.length,12);end.writeUInt32LE(offset,16);
 return Buffer.concat([...locals,central,end]);
}
function timetable(year,teacher){
 const cell=(at,value)=>`<c r="${at}" t="inlineStr"><is><t>${value}</t></is></c>`;
 const days=['월','화','수','목','금'],cols=['B','C','D','E','F'];
 const sheet=`<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1">${cell('A1',year+'학년도 1학기')}</row><row r="2">${days.map((d,i)=>cell(cols[i]+'2',d)).join('')}</row><row r="3">${cols.map(c=>`<c r="${c}3"><v>1</v></c>`).join('')}</row><row r="4">${cell('A4','1학년 1반')}${cols.map(c=>cell(c+'4','국어('+teacher+')')).join('')}</row></sheetData></worksheet>`;
 return zip({'[Content_Types].xml':'<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>','xl/workbook.xml':'<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="기초" sheetId="1" r:id="rId1"/></sheets></workbook>','xl/_rels/workbook.xml.rels':'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/></Relationships>','xl/worksheets/sheet1.xml':sheet});
}
class Client{
 cookies=new Map();csrf='';
 constructor(port,extra={}){this.port=port;this.extra=extra;}
 request(route,method='GET',body,status=200){return new Promise((resolve,reject)=>{
  const payload=body===undefined?undefined:JSON.stringify(body);
  const headers={...this.extra,Cookie:[...this.cookies].map(([k,v])=>k+'='+v).join('; ')};
  if(method!=='GET')headers['X-CSRF-TOKEN']=this.csrf;
  if(payload!==undefined){headers['Content-Type']='application/json';headers['Content-Length']=Buffer.byteLength(payload);}
  const req=http.request({host:'127.0.0.1',port:this.port,path:route,method,headers},response=>{
   const chunks=[];response.on('data',c=>chunks.push(c));response.on('end',()=>{
    this.setCookies=response.headers['set-cookie']??[];
    for(const cookie of this.setCookies){const p=cookie.split(';')[0],i=p.indexOf('=');this.cookies.set(p.slice(0,i),p.slice(i+1));}
    const text=Buffer.concat(chunks).toString();
    try{assert.equal(response.statusCode,status,method+' '+route+' '+text);}catch(e){reject(e);return;}
    resolve(text&&response.headers['content-type']?.includes('json')?JSON.parse(text):text);
   });
  });
  req.on('error',reject);if(payload!==undefined)req.write(payload);req.end();
 });}
 async ready(){this.csrf=(await this.request('/api/session')).csrfToken;return this;}
 async login(username,secret,status=200){await this.ready();const value=await this.request('/api/login','POST',{username,password:secret,rememberLogin:false},status);if(status===200)this.csrf=(await this.request('/api/session')).csrfToken;return value;}
}
const inside=await freePort(),outside=await freePort(),dll=path.join(root,'src/SchoolMessenger.Server/bin/Debug/net10.0/SchoolMessenger.Server.dll');
const environment={...process.env,ASPNETCORE_ENVIRONMENT:'Development',School__DataDirectory:data,School__AdminPassword:password,School__LoginLimitPerMinute:'300',School__ExternalPort:String(outside),School__ExternalHost:host};
const refused=urls=>spawnSync(dotnet,[dll,'--urls',urls],{cwd:path.dirname(dll),windowsHide:true,timeout:60000,encoding:'utf8',env:environment});
const server=spawn(dotnet,[dll,'--urls',`http://127.0.0.1:${inside};http://127.0.0.1:${outside}`],{cwd:path.dirname(dll),windowsHide:true,env:environment});
let logs='';server.stdout.on('data',b=>logs+=b);server.stderr.on('data',b=>logs+=b);
const from=ip=>new Client(outside,{Host:host,'CF-Connecting-IP':ip});
try{
 for(let i=0;i<150;i++){try{if((await fetch(`http://127.0.0.1:${inside}/health`)).ok)break;}catch{}await new Promise(r=>setTimeout(r,100));}
 const admin=new Client(inside);await admin.login('admin',password);
 const secret='Personal-'+randomUUID(),shared='12345678',temporary='Temporary-'+randomUUID();
 const created=await admin.request('/api/admin/staff','POST',{loginName:'teacher1',name:'가상 교사',department:'교무부',seatIp:null,temporaryPassword:temporary,position:'교사'});
 const first=new Client(inside);await first.login('teacher1',temporary);
 const policy=await first.request('/api/registration/policy');
 await first.request('/api/activate','POST',{code:created.activationCode,newPassword:secret,agreement:{revision:policy.revision,acceptedIds:policy.documents.filter(d=>d.required).map(d=>d.id)}});
 const waiting=await admin.request('/api/admin/staff','POST',{loginName:'teacher2',name:'가상 교사2',department:'교무부',seatIp:null,temporaryPassword:shared,position:'교사',useSharedPassword:true});assert.ok(waiting.activationCode);
 const teacher=new Client(inside);await teacher.login('teacher1',secret);
 await teacher.request('/api/todos','POST',{title:'가상 할 일',dueDate:null,status:'open',revision:0});

 const phone=from('203.0.113.10');
 const session=await phone.ready().then(p=>p.request('/api/session'));assert.equal(session.external,true);
 assert.equal((await teacher.request('/api/session')).external,false);
 check('the server tells the app when it is reached from outside');

 await new Client(outside,{Host:'other.example'}).request('/api/session','GET',undefined,404);
 await new Client(outside,{Host:'127.0.0.1'}).request('/api/session','GET',undefined,404);
 check('the entrance answers only to the school public address');

 await phone.login('teacher2',shared,403);assert.equal((await phone.request('/api/session')).user,null);
 check('the shared first password opens no session from outside');
 const signedIn=from('203.0.113.11');await signedIn.login('teacher1',secret);
 assert.ok(signedIn.cookies.has('SchoolMessenger.Session'));
 assert.ok((await signedIn.request('/api/session')).user);
 check('an activated teacher signs in from outside');
 const raw=from('203.0.113.12');await raw.ready();await raw.request('/api/login','POST',{username:'teacher1',password:secret,rememberLogin:false});
 assert.ok(raw.setCookies.some(c=>c.startsWith('SchoolMessenger.Session=')&&/;\s*secure/i.test(c)));
 check('session cookies are marked secure behind the tunnel');

 const today=new Date().toISOString().slice(0,10);
 for(const route of ['/health','/server-info','/api/registration/policy','/api/timetable/setup','/api/timetable/day?date='+today+'&mine=true','/api/submission-requests','/api/office/setup','/api/office/events?from='+today+'&to='+new Date(Date.now()+2*86400000).toISOString().slice(0,10)])await signedIn.request(route);
 assert.equal((await signedIn.request('/api/todos')).length,1);
 check('timetable, tasks, submissions and the school calendar can be read');

 const kst=offset=>new Date(Date.now()+9*3600000+offset*86400000).toISOString().slice(0,10);
 const form=new FormData();form.set('kind','base');form.set('file',new Blob([timetable(kst(0).slice(0,4),'가상 교사')]),'timetable.xlsx');
 const uploaded=await fetch(`http://127.0.0.1:${inside}/api/timetable/import`,{method:'POST',headers:{Cookie:[...admin.cookies].map(([k,v])=>k+'='+v).join('; '),'X-CSRF-TOKEN':admin.csrf},body:form});
 assert.equal(uploaded.status,200,await uploaded.clone().text());const imported=await uploaded.json();
 assert.equal(imported.classes.length,1);assert.equal(imported.classes[0].candidates.length,1);
 const teacherId=(await admin.request('/api/admin/users')).find(u=>u.username==='teacher1').id;
 const preview=await admin.request('/api/timetable/preview','POST',{draftId:imported.draftId,start:kst(0),end:kst(30),classes:[{source:imported.classes[0].source,id:imported.classes[0].candidates[0]}],teachers:[{source:'가상 교사',id:teacherId}]});
 await admin.request('/api/timetable/publish','POST',{candidateId:preview.candidateId,clientId:randomUUID().replaceAll('-','')});
 const setup=await signedIn.request('/api/timetable/setup');assert.deepEqual(setup.classes.map(c=>c.name),['1학년 1반']);assert.equal(setup.pending,0);
 const weekday=[0,1,2,3,4,5,6].map(kst).find(d=>new Date(d).getUTCDay()===1);
 const mine=await signedIn.request('/api/timetable/day?date='+weekday+'&mine=true');assert.equal(mine.slots.length,1);assert.equal(mine.slots[0].subject,'국어');
 const other=from('203.0.113.14');await other.login('admin',password);assert.equal((await other.request('/api/timetable/day?date='+weekday+'&mine=true')).slots.length,0);
 check('without a news server a posted timetable reaches its teacher, also from outside');

 const adminOutside=from('203.0.113.13');await adminOutside.login('admin',password);
 const blocked=[['GET','/'],['GET','/index.html'],['GET','/hub'],['GET','/remote'],['GET','/health/operations'],['GET','/api/messages?box=received'],['GET','/api/chats'],['GET','/api/users'],['GET','/api/admin/users'],['GET','/api/admin/staff'],['GET','/api/admin/deployment-review'],['GET','/api/submission-requests/1'],['GET','/api/submission-requests/1/zip'],['GET','/api/attachments/1/download'],['GET','/API/ADMIN/USERS'],['GET','/api/todos/'],['GET','/api/todos/../admin/users'],['GET','/api/admin%2fusers'],['HEAD','/api/todos'],['OPTIONS','/api/todos'],
  ['POST','/api/messages'],['POST','/api/todos'],['POST','/api/password'],['POST','/api/activate'],['POST','/api/register'],['POST','/api/admin/users'],['POST','/api/admin/staff/activation-codes'],['POST','/api/submission-requests/1/submit'],['POST','/api/office/events'],['POST','/api/attachments'],['POST','/api/presence/mobile'],['PATCH','/api/todos/1'],['PATCH','/api/admin/users/1'],['DELETE','/api/attachments/1']];
 for(const [method,route] of blocked){await adminOutside.request(route,method,method==='POST'||method==='PATCH'?{}:undefined,403);await signedIn.request(route,method,method==='POST'||method==='PATCH'?{}:undefined,403);}
 assert.equal((await teacher.request('/api/todos')).length,1);
 check(blocked.length+' other requests are refused from outside, even for an administrator');

 for(const route of ['/api/messages?box=received','/api/chats'])await teacher.request(route);await admin.request('/api/admin/users');
 check('inside the school everything still works');

 const sprayer=from('203.0.113.20');await sprayer.ready();let limited=false;
 let tries=0;for(;tries<30&&!limited;tries++){try{await sprayer.request('/api/login','POST',{username:'nobody'+tries,password:'Wrong-'+tries,rememberLogin:false},401);}catch{limited=true;}}
 assert.ok(limited&&tries<=21,'login attempts from one address are limited to 20 a minute, tried '+tries);
 await from('203.0.113.21').login('teacher1',secret);
 check('outside login limits follow the visitor, so one address cannot lock out the others');

 const spoof=new Client(inside,{'CF-Connecting-IP':'203.0.113.30'});assert.equal((await spoof.request('/api/session')).external,false);await spoof.ready();
 check('the address header means nothing on the normal port');

 for(const urls of [`http://127.0.0.1:${inside}`,`http://0.0.0.0:${outside}`,`http://192.0.2.10:${outside}`]){const result=refused(urls);assert.notEqual(result.status,0,urls);assert.match(result.stdout+result.stderr,/127\.0\.0\.1/,urls);}
 environment.School__ExternalHost='10.0.0.1';const ipHost=refused(`http://127.0.0.1:${outside}`);assert.notEqual(ipHost.status,0);assert.match(ipHost.stdout+ipHost.stderr,/InvalidOperationException/);
 check('the server refuses to start with an entrance another computer could reach');
}catch(e){console.error(logs);throw e;}
finally{server.kill();}
