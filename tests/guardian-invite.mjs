import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {mkdirSync,existsSync} from 'node:fs';
import {DatabaseSync} from 'node:sqlite';
import net from 'node:net';
import path from 'node:path';

// A guardian joins from the QR code on a signed-in student's phone and is linked without school review.
const root=process.cwd(),sdk=path.join(root,'.tools/dotnet/dotnet.exe'),dotnet=existsSync(sdk)?sdk:'dotnet';
const data=path.join(process.env.YY_TEST_DATA_ROOT??path.join(root,'.test-data'),'guardian-invite-'+randomUUID());mkdirSync(data,{recursive:true});
const password='Synthetic-'+randomUUID();
const check=name=>console.log('PASS '+name);
async function port(){const s=net.createServer();await new Promise(r=>s.listen(0,'127.0.0.1',r));const p=s.address().port;await new Promise(r=>s.close(r));return p;}
class Client{
 cookies=new Map();csrf='';user=null;
 constructor(base){this.base=base;}
 async request(route,method='GET',body,status=200){const headers={Cookie:[...this.cookies].map(([k,v])=>k+'='+v).join('; ')};
  if(method!=='GET')headers['X-CSRF-TOKEN']=this.csrf;
  if(body!==undefined)headers['Content-Type']='application/json';
  const response=await fetch(this.base+'/api/'+route,{method,headers,body:body===undefined?undefined:JSON.stringify(body)});
  for(const cookie of response.headers.getSetCookie()){const p=cookie.split(';')[0],i=p.indexOf('=');this.cookies.set(p.slice(0,i),p.slice(i+1));}
  const text=await response.text();assert.equal(response.status,status,method+' '+route+' '+text);return text&&response.headers.get('Content-Type')?.includes('json')?JSON.parse(text):text;
 }
 async start(){this.csrf=(await this.request('session')).csrfToken;return this;}
 async login(username='admin'){await this.start();await this.request('login','POST',{username,password,rememberLogin:false});const s=await this.request('session');this.csrf=s.csrfToken;this.user=s.user;return this;}
}
const base='http://127.0.0.1:'+await port(),dll=path.join(root,'src/SchoolMessenger.AnnouncementServer/bin/Debug/net10.0/SchoolMessenger.AnnouncementServer.dll');
const server=spawn(dotnet,[dll,'--urls',base],{cwd:path.dirname(dll),windowsHide:true,env:{...process.env,ASPNETCORE_ENVIRONMENT:'Development',Portal__DataDirectory:data,Portal__AdminPassword:password,Portal__LoginLimitPerMinute:'300',Portal__BridgeKey:'Bridge-'+randomUUID()}});
let logs='';server.stdout.on('data',b=>logs+=b);server.stderr.on('data',b=>logs+=b);
try{
 for(let i=0;i<150;i++){try{if((await fetch(base+'/health')).ok)break;}catch{}await new Promise(r=>setTimeout(r,100));}
 const admin=await new Client(base).login();
 const room=await admin.request('admin/classes','POST',{name:'가상 1학년 2반',grade:1});
 // The student is seeded with the admin's password hash so the test needs no separate approval flow.
 const db=new DatabaseSync(path.join(data,'announcements.db'));db.exec('PRAGMA busy_timeout=30000');
 const hash=db.prepare("SELECT PasswordHash FROM People WHERE Username='admin'").get().PasswordHash;
 db.prepare("INSERT INTO People(Id,Username,Name,Role,ClassId,Active,PasswordHash) VALUES(?,?,?,'student',?,1,?)").run(randomUUID().replaceAll('-',''),'qr.student','가상 학생',room.id,hash);
 const student=await new Client(base).login('qr.student');
 const policy=await new Client(base).request('registration/policy');const agreement={revision:policy.revision,acceptedIds:policy.documents.filter(d=>d.required).map(d=>d.id)};
 const pin='A'.repeat(64);
 const invite=await student.request('settings/guardian-invite','POST',{address:base,pin});
 const token=new URL(invite.link).searchParams.get('invite');
 assert.match(invite.link,/^schooltalk:\/\/guardian\?server=/);assert.equal(new URL(invite.link).searchParams.get('server'),base);assert.equal(new URL(invite.link).searchParams.get('pin'),pin);
 assert.ok(Math.abs(invite.expiresAt-Date.now()-600_000)<60_000);
 assert.deepEqual([...Buffer.from(invite.qr,'base64').subarray(0,4)],[0x89,0x50,0x4e,0x47]);
 check('a signed-in student gets a ten-minute QR code that carries the server address and its certificate fingerprint');

 const preview=await new Client(base).request('registration/guardian-invite/'+token);
 assert.deepEqual(preview,{studentName:'가상 학생',className:'가상 1학년 2반'});
 check('the guardian sees whose guardian they are about to become before signing up');

 const joining=await new Client(base).start();
 const joined=await joining.request('register','POST',{name:'가상 보호자',username:'qr.parent',password,role:'parent',agreement,guardianInvite:token});
 assert.equal(joined.membership,'regular');assert.match(joined.message,/가상 학생/);
 const parent=await new Client(base).login('qr.parent');
 assert.deepEqual((await parent.request('children')).map(c=>c.name),['가상 학생']);
 assert.equal((await parent.request('settings')).membership,'regular');
 assert.equal(db.prepare("SELECT COUNT(*) n FROM GuardianRequests").get().n,0);
 check('signing up from the QR code links the guardian at once, without a school review request');

 const second=await new Client(base).start();
 await second.request('register','POST',{name:'다른 보호자',username:'qr.other',password,role:'parent',agreement,guardianInvite:token},400);
 assert.equal(db.prepare("SELECT COUNT(*) n FROM People WHERE Username='qr.other'").get().n,0);
 await new Client(base).request('registration/guardian-invite/'+token,'GET',undefined,404);
 check('an invite works once; a second sign-up with it creates no account');

 const first=new URL((await student.request('settings/guardian-invite','POST',{address:base})).link).searchParams.get('invite');
 const latest=new URL((await student.request('settings/guardian-invite','POST',{address:base})).link).searchParams.get('invite');
 await new Client(base).request('registration/guardian-invite/'+first,'GET',undefined,404);
 db.prepare('UPDATE GuardianInvites SET ExpiresAt=? WHERE UsedAt IS NULL').run(Date.now()-1);
 await new Client(base).request('registration/guardian-invite/'+latest,'GET',undefined,404);
 check('only the newest invite of a student is valid, and only for ten minutes');

 const fresh=new URL((await student.request('settings/guardian-invite','POST',{address:base})).link).searchParams.get('invite');
 const later=await new Client(base).start();await later.request('register','POST',{name:'나중 보호자',username:'qr.later',password,role:'parent',agreement});
 const laterParent=await new Client(base).login('qr.later');
 assert.equal((await laterParent.request('settings/children/invite','POST',{code:fresh})).membership,'regular');
 assert.equal((await laterParent.request('children')).length,1);
 check('a guardian who already has an account adds the child from the QR code');

 await parent.request('settings/guardian-invite','POST',{address:base},403);
 await student.request('settings/guardian-invite','POST',{address:'javascript:alert(1)'},400);
 await student.request('settings/guardian-invite','POST',{address:base,pin:'not-a-fingerprint'},400);
 const admins=await new Client(base).start();await admins.request('register','POST',{name:'학생 위장',username:'qr.fake',password,role:'student',classId:room.id,studentNumber:3,agreement,guardianInvite:fresh},400);
 check('only students make invites, only parents use them, and the address and fingerprint are validated');
 db.close();
}catch(error){console.error(logs);throw error;}
finally{if(server.exitCode===null){server.kill();await new Promise(r=>server.once('exit',r));}}
