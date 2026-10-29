import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {existsSync,mkdirSync} from 'node:fs';
import net from 'node:net';
import path from 'node:path';
const root=process.cwd(),folder=path.join(root,'.test-data','school-routing-'+randomUUID());mkdirSync(folder,{recursive:true});
const localDotnet=path.join(root,'.tools/dotnet/dotnet.exe');
const dotnet=existsSync(localDotnet)?localDotnet:'dotnet',password='Synthetic-'+randomUUID();
const children=[];
async function launch(role,id){
 const listener=net.createServer();await new Promise(resolve=>listener.listen(0,'127.0.0.1',resolve));const port=listener.address().port;await new Promise(resolve=>listener.close(resolve));
 const project=role==='office'?'Server':'AnnouncementServer',prefix=role==='office'?'School':'Portal',address='http://127.0.0.1:'+port;
 const child=spawn(dotnet,[path.join(root,'src','SchoolMessenger.'+project,'bin/Debug/net10.0','SchoolMessenger.'+project+'.dll'),'--urls',address],{cwd:path.join(root,'src','SchoolMessenger.'+project),windowsHide:true,env:{...process.env,ASPNETCORE_ENVIRONMENT:'Development',Registration__PolicyFile:'',Operations__SecurityReviewFile:'',Operations__SchoolId:id,FeedbackMail__Enabled:'false',Operations__AlertMailEnabled:'false',Announcements__Address:'',Portal__TunnelOnly:'false',Portal__BridgeKey:password,[prefix+'__Name']:'Synthetic same-name school',[prefix+'__DataDirectory']:path.join(folder,id+'-'+role),[prefix+'__AdminPassword']:password}});
 children.push(child);let output='',startupError;child.on('error',error=>startupError=error);child.stdout.on('data',chunk=>output+=chunk);child.stderr.on('data',chunk=>output+=chunk);
 for(let i=0;i<120;i++){
  if(startupError)throw Error('School fixture process failed: '+startupError.message);
  if(child.exitCode!==null)throw Error('School fixture stopped: '+output);
  try{const response=await fetch(address+'/server-info',{signal:AbortSignal.timeout(1000)});if(response.ok)return {address,identity:await response.json()};}catch{}
  await new Promise(resolve=>setTimeout(resolve,200));
 }
 throw Error('School fixture failed to start: '+output);
}
try{
 const [a,b,portal]=await Promise.all([launch('office','SCHOOL-A'),launch('office','SCHOOL-B'),launch('announcements','SCHOOL-A')]);
 for(const [fixture,id,role] of [[a,'SCHOOL-A','office'],[b,'SCHOOL-B','office'],[portal,'SCHOOL-A','announcements']]){
  assert.equal(fixture.identity.schoolId,id);assert.equal(fixture.identity.service,role);assert.equal(fixture.identity.directoryProduct,'SchoolMessenger.School.1');
  assert.deepEqual(Object.keys(fixture.identity).sort(),['directoryProduct','product','schoolId','schoolName','service']);
  assert.equal((await fetch(fixture.address+'/api/admin/deployment-review')).status,401);
 }
 console.log('PASS Both actual server roles publish bounded public school identity without exposing administrator APIs');
 const jar=new Map();let csrf;
 async function request(base,route,body){const headers={Cookie:[...jar].map(([name,value])=>name+'='+value).join('; ')};if(body){headers['Content-Type']='application/json';headers['X-CSRF-TOKEN']=csrf;}const response=await fetch(base+route,{method:body?'POST':'GET',headers,body:body?JSON.stringify(body):undefined});for(const cookie of response.headers.getSetCookie()){const part=cookie.split(';')[0],at=part.indexOf('=');jar.set(part.slice(0,at),part.slice(at+1));}return response;}
 csrf=(await(await request(a.address,'/api/session')).json()).csrfToken;assert.equal((await request(a.address,'/api/login',{username:'admin',password})).status,200);
 const aSession=await(await request(a.address,'/api/session')).json();assert.ok(aSession.user);
 assert.equal((await request(b.address,'/api/admin/deployment-review')).status,401);
 const bSession=await(await request(b.address,'/api/session')).json();assert.equal(bSession.user,null);
 assert.notEqual(a.identity.schoolId,b.identity.schoolId);assert.equal(a.identity.schoolName,b.identity.schoolName);
 console.log('PASS Same-name schools have separate identities and databases; even a copied other-school cookie cannot authorize access');
}finally{await Promise.all(children.map(child=>child.pid&&child.exitCode===null?new Promise(resolve=>{child.once('exit',resolve);child.kill();}):Promise.resolve()));}
console.log('2 real multi-school server checks passed; synthetic loopback data only.');
