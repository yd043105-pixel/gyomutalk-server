import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {createHash, randomBytes, randomUUID} from 'node:crypto';
import {mkdirSync} from 'node:fs';
import net from 'node:net';
import path from 'node:path';

const root=process.cwd(),data=path.resolve(process.env.YY_TEST_DATA_ROOT??'.test-data','large-attachments-'+randomUUID());
mkdirSync(data,{recursive:true});
const listener=net.createServer();await new Promise(resolve=>listener.listen(0,'127.0.0.1',resolve));
const address='http://127.0.0.1:'+listener.address().port;await new Promise(resolve=>listener.close(resolve));
const password='Test-'+randomUUID();
const dll=path.join(root,'src/SchoolMessenger.Server/bin/Debug/net10.0/SchoolMessenger.Server.dll');
const server=spawn('dotnet',[dll,'--urls',address],{cwd:path.dirname(dll),windowsHide:true,
 env:{...process.env,ASPNETCORE_ENVIRONMENT:'Development',School__DataDirectory:data,School__AdminPassword:password}});
let logs='';server.stdout.on('data',bytes=>logs+=bytes);server.stderr.on('data',bytes=>logs+=bytes);
function client(){
 const cookies=new Map();let csrf='';
 async function call(route,options={}){
  const response=await fetch(address+'/api/'+route,{...options,headers:{Cookie:[...cookies].map(pair=>pair.join('=')).join('; '),'X-CSRF-TOKEN':csrf,...options.headers}});
  for(const entry of response.headers.getSetCookie()){
   const pair=entry.split(';')[0],split=pair.indexOf('=');cookies.set(pair.slice(0,split),pair.slice(split+1));
  }
  return response;
 }
 async function json(route,body){const response=await call(route,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});assert.equal(response.status,200,await response.clone().text());return response.json();}
 return {call,json,async login(username){csrf=(await (await call('session')).json()).csrfToken;await json('login',{username,password});const session=await (await call('session')).json();csrf=session.csrfToken;return session.user;}};
}

try{
 let ready=false;
 for(let i=0;i<120&&!ready;i++){assert.equal(server.exitCode,null,logs);try{ready=(await fetch(address+'/health')).ok;}catch{}if(!ready)await new Promise(resolve=>setTimeout(resolve,100));}
 assert.ok(ready,logs);
 const sender=client(),other=client();const self=await sender.login('admin');
 await sender.json('admin/users',{username:'other',name:'가상 사용자',department:'시험',password,isAdmin:false,canBroadcast:false});await other.login('other');

 const size=Number(process.env.YY_LARGE_ATTACHMENT_MIB??101)*1024*1024,block=randomBytes(256*1024),original=createHash('sha256');
 async function* payload(){for(let sent=0;sent<size;sent+=block.length){const part=block.subarray(0,Math.min(size-sent,block.length));original.update(part);yield part;}}
 const start=performance.now();
 const upload=await sender.call('attachments',{method:'POST',duplex:'half',body:payload(),headers:{'Content-Type':'application/octet-stream','Content-Length':String(size),'X-Gyomutalk-File-Name':encodeURIComponent('대용량 자료.txt')}});
 assert.equal(upload.status,200,await upload.clone().text());
 const attachment=await upload.json(),hash=original.digest('hex');assert.equal(attachment.size,size);assert.equal(attachment.name,'대용량 자료.txt');
 console.log(`PASS ${(size/2**20).toFixed(0)} MiB raw upload saved and checked in ${((performance.now()-start)/1000).toFixed(1)}s`);
 await sender.json('messages',{clientId:randomUUID(),title:'대용량 시험',body:'합성 파일입니다.',recipientIds:[self.id],attachmentIds:[attachment.id]});

 const route='attachments/'+attachment.id+'/download',cut=3*1024*1024;
 const denied=await other.call(route,{headers:{Range:`bytes=${cut}-`}});assert.equal(denied.status,404);await denied.body.cancel();
 const first=await sender.call(route,{headers:{Range:`bytes=0-${cut-1}`}});assert.equal(first.status,206);assert.equal(first.headers.get('content-range'),`bytes 0-${cut-1}/${size}`);
 const tag=first.headers.get('etag');assert.equal(tag?.toLowerCase(),`"${hash}"`);
 const merged=createHash('sha256');for await(const chunk of first.body)merged.update(chunk);
 const began=performance.now();const rest=await sender.call(route,{headers:{Range:`bytes=${cut}-`,'If-Range':tag}});
 assert.equal(rest.status,206);assert.equal(rest.headers.get('content-range'),`bytes ${cut}-${size-1}/${size}`);
 let received=0;for await(const chunk of rest.body){merged.update(chunk);received+=chunk.length;}
 assert.equal(received,size-cut);assert.equal(merged.digest('hex'),hash);
 console.log(`PASS authorized range resume rebuilt the exact file at ${(received/2**20/((performance.now()-began)/1000)).toFixed(1)} MiB/s including server verification (loopback only)`);
 const stale=await sender.call(route,{headers:{Range:`bytes=${cut}-`,'If-Range':'"obsolete"'}});
 assert.equal(stale.status,200);assert.equal(Number(stale.headers.get('content-length')),size);await stale.body.cancel();
 console.log('PASS other users cannot request ranges and an obsolete validator requires a fresh download');
}catch(error){console.error(logs.slice(-3000));throw error;}
finally{if(server.exitCode===null){const closed=new Promise(resolve=>server.once('exit',resolve));server.kill();await closed;}}
