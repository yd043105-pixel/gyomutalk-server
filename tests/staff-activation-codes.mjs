import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {mkdirSync,existsSync} from 'node:fs';
import {DatabaseSync} from 'node:sqlite';
import net from 'node:net';
import path from 'node:path';

// The administrator downloads every pending staff activation code at once; reissued codes replace the old ones.
const root=process.cwd(),sdk=path.join(root,'.tools/dotnet/dotnet.exe'),dotnet=existsSync(sdk)?sdk:'dotnet';
const data=path.join(process.env.YY_TEST_DATA_ROOT??path.join(root,'.test-data'),'staff-codes-'+randomUUID());mkdirSync(data,{recursive:true});
const password='Synthetic-'+randomUUID();
const check=name=>console.log('PASS '+name);
async function port(){const s=net.createServer();await new Promise(r=>s.listen(0,'127.0.0.1',r));const p=s.address().port;await new Promise(r=>s.close(r));return p;}
class Client{
 cookies=new Map();csrf='';
 constructor(base){this.base=base;}
 async request(route,method='GET',body,status=200){const headers={Cookie:[...this.cookies].map(([k,v])=>k+'='+v).join('; ')};
  if(method!=='GET')headers['X-CSRF-TOKEN']=this.csrf;
  if(body!==undefined)headers['Content-Type']='application/json';
  const response=await fetch(this.base+'/api/'+route,{method,headers,body:body===undefined?undefined:JSON.stringify(body)});
  for(const cookie of response.headers.getSetCookie()){const p=cookie.split(';')[0],i=p.indexOf('=');this.cookies.set(p.slice(0,i),p.slice(i+1));}
  const text=await response.text();assert.equal(response.status,status,method+' '+route+' '+text);return text&&response.headers.get('Content-Type')?.includes('json')?JSON.parse(text):text;
 }
 async login(username,secret){this.csrf=(await this.request('session')).csrfToken;await this.request('login','POST',{username,password:secret,rememberLogin:false});this.csrf=(await this.request('session')).csrfToken;return this;}
}
const base='http://127.0.0.1:'+await port(),dll=path.join(root,'src/SchoolMessenger.Server/bin/Debug/net10.0/SchoolMessenger.Server.dll');
const server=spawn(dotnet,[dll,'--urls',base],{cwd:path.dirname(dll),windowsHide:true,env:{...process.env,ASPNETCORE_ENVIRONMENT:'Development',School__DataDirectory:data,School__AdminPassword:password,School__LoginLimitPerMinute:'300'}});
let logs='';server.stdout.on('data',b=>logs+=b);server.stderr.on('data',b=>logs+=b);
try{
 for(let i=0;i<150;i++){try{if((await fetch(base+'/health')).ok)break;}catch{}await new Promise(r=>setTimeout(r,100));}
 const admin=await new Client(base).login('admin',password);
 const temporary='Temporary-'+randomUUID();
 const first=await admin.request('admin/staff','POST',{loginName:'가상교사1',name:'가상 교사1',department:'교무부',seatIp:null,temporaryPassword:temporary,position:'교사'});
 await admin.request('admin/staff','POST',{loginName:'가상교사2',name:'가상 교사2',department:'과학부',seatIp:null,temporaryPassword:temporary,position:''});
 const list=await admin.request('admin/staff/activation-codes','POST');
 assert.deepEqual(list.map(p=>p.loginName).sort(),['가상교사1','가상교사2']);
 assert.ok(list.every(p=>/^[A-Za-z0-9]{20}$/.test(p.activationCode)&&p.expiresAt>Date.now()));
 assert.ok(!list.some(p=>p.loginName==='admin'));
 check('the administrator gets one list with a fresh code for every staff account still waiting for activation');

 const teacher=await new Client(base).login('가상교사1',temporary);
 const policy=await teacher.request('registration/policy');const agreement={revision:policy.revision,acceptedIds:policy.documents.filter(d=>d.required).map(d=>d.id)};
 await teacher.request('activate','POST',{code:first.activationCode,newPassword:'Personal-'+randomUUID(),agreement},400);
 const code=list.find(p=>p.loginName==='가상교사1').activationCode;
 await teacher.request('activate','POST',{code,newPassword:'Personal-'+randomUUID(),agreement});
 check('the code issued at registration stops working and the downloaded code activates the account');

 assert.deepEqual((await admin.request('admin/staff/activation-codes','POST')).map(p=>p.loginName),['가상교사2']);
 check('activated teachers drop out of the next download');
 const teacherOnly=await new Client(base).login('가상교사2',temporary);
 await teacherOnly.request('admin/staff/activation-codes','POST',undefined,403);
 check('only administrators can download activation codes');
 const stopped=(await admin.request('admin/users')).find(u=>u.username==='가상교사2');const patch=active=>admin.request('admin/users/'+stopped.id,'PATCH',{name:stopped.name,department:stopped.department,position:stopped.position,password:'',isAdmin:false,canBroadcast:false,active});
 await patch(false);
 const blocked=new Client(base);blocked.csrf=(await blocked.request('session')).csrfToken;
 const told=await blocked.request('login','POST',{username:'가상교사2',password:temporary,rememberLogin:false},403);assert.match(told.error,/사용 중지/);
 const hidden=await blocked.request('login','POST',{username:'가상교사2',password:'Wrong-'+randomUUID(),rememberLogin:false},401);assert.doesNotMatch(hidden.error,/사용 중지/);
 check('a stopped account says so to someone with the right password, and a wrong guess learns nothing');
 await patch(true);await new Client(base).login('가상교사2',temporary);
 check('turning the account back on lets the same password sign in');
 const lockout=new Client(base);lockout.csrf=(await lockout.request('session')).csrfToken;
 const attempt=(username,secret,status)=>lockout.request('login','POST',{username,password:secret,rememberLogin:false},status);
 const wrong=async times=>{let last;for(let i=0;i<times;i++)last=await attempt('가상교사2','Wrong-'+randomUUID(),401);return last;};
 const unknown=await attempt('없는계정','Wrong-'+randomUUID(),401);assert.match(unknown.error,/5회 이상 틀리면 15분간 잠깁니다/);assert.match(unknown.error,/담당자에게 문의/);
 assert.deepEqual(await wrong(1),unknown);
 await wrong(3);await new Client(base).login('가상교사2',temporary);
 check('four wrong passwords do not lock an account, and a right one clears the count');
 await wrong(5);
 assert.deepEqual(await attempt('가상교사2',temporary,401),unknown);
 check('five wrong passwords lock the account, the right password is refused, and the answer matches an unknown account');
 const database=new DatabaseSync(path.join(data,'school.db'));const lockedUntil=database.prepare("SELECT LockedUntil FROM Users WHERE Username='가상교사2'").get().LockedUntil;
 const minutes=(lockedUntil-Date.now())/60000;assert.ok(minutes>14&&minutes<=15,'locked for '+minutes+' minutes');
 database.prepare("UPDATE Users SET LockedUntil=1 WHERE Username='가상교사2'").run();database.close();
 await wrong(1);await new Client(base).login('가상교사2',temporary);
 check('the lock lasts 15 minutes, and one mistake afterwards does not lock again');
}catch(e){console.error(logs);throw e;}
finally{server.kill();}
