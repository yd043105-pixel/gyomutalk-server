import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomBytes,randomUUID} from 'node:crypto';
import dgram from 'node:dgram';
import {existsSync,mkdirSync} from 'node:fs';
import net from 'node:net';
import path from 'node:path';

// A teacher PC that disappears before the answer arrives must not stop the school server's discovery.
const root=process.cwd(),sdk=path.join(root,'.tools/dotnet/dotnet.exe'),dotnet=existsSync(sdk)?sdk:'dotnet';
const data=path.join(process.env.YY_TEST_DATA_ROOT??path.join(root,'.test-data'),'discovery-'+randomUUID());mkdirSync(data,{recursive:true});
const password='Synthetic-'+randomUUID();
async function freeTcp(){const s=net.createServer();await new Promise(r=>s.listen(0,'127.0.0.1',r));const p=s.address().port;await new Promise(r=>s.close(r));return p;}
async function freeUdp(){const s=dgram.createSocket('udp4');await new Promise(r=>s.bind(0,'0.0.0.0',r));const p=s.address().port;await new Promise(r=>s.close(r));return p;}
const pause=ms=>new Promise(r=>setTimeout(r,ms));
const base='http://127.0.0.1:'+await freeTcp(),port=await freeUdp(),dll=path.join(root,'src/SchoolMessenger.Server/bin/Debug/net10.0/SchoolMessenger.Server.dll');
const server=spawn(dotnet,[dll,'--urls',base],{cwd:path.dirname(dll),windowsHide:true,env:{...process.env,ASPNETCORE_ENVIRONMENT:'Development',School__DataDirectory:data,School__AdminPassword:password,School__DiscoveryPort:String(port)}});
let logs='';server.stdout.on('data',b=>logs+=b);server.stderr.on('data',b=>logs+=b);
const query=()=>Buffer.from(JSON.stringify({product:'Yeoyang.Office.Discovery.1',nonce:randomBytes(16).toString('hex')}));
function ask(){return new Promise((resolve,reject)=>{
 const socket=dgram.createSocket('udp4');const packet=query();
 const timer=setTimeout(()=>{socket.close();reject(Error('no discovery answer'));},4000);
 socket.on('message',message=>{clearTimeout(timer);socket.close();resolve(JSON.parse(message.toString()));});
 socket.send(packet,port,'127.0.0.1');
});}
function leave(){return new Promise(resolve=>{const socket=dgram.createSocket('udp4');socket.send(query(),port,'127.0.0.1',()=>socket.close(resolve));});}
try{
 for(let i=0;i<150;i++){try{if((await fetch(base+'/health')).ok)break;}catch{}await pause(100);}
 let cookies='';const call=async(route,method='GET',body,csrf)=>{const r=await fetch(base+route,{method,headers:{Cookie:cookies,'Content-Type':'application/json',...(csrf?{'X-CSRF-TOKEN':csrf}:{})},body:body&&JSON.stringify(body)});for(const c of r.headers.getSetCookie()){const p=c.split(';')[0],n=p.split('=')[0];cookies=[...cookies.split('; ').filter(x=>x&&!x.startsWith(n+'=')),p].join('; ');}assert.ok(r.ok,route+' '+r.status);const t=await r.text();return t?JSON.parse(t):null;};
 await call('/api/login','POST',{username:'admin',password,rememberLogin:false},(await call('/api/session')).csrfToken);
 await call('/api/admin/server-discovery','POST',{enabled:true,address:'https://192.0.2.10:7443/'},(await call('/api/session')).csrfToken);
 await pause(1500);
 assert.equal((await ask()).address,'https://192.0.2.10:7443/');
 console.log('PASS the server answers a discovery query');
 for(let i=0;i<3;i++){await pause(1100);await leave();}
 await pause(1100);
 assert.equal((await ask()).address,'https://192.0.2.10:7443/');
 console.log('PASS the server keeps answering after teacher PCs leave before the answer arrives');
}catch(e){console.error(logs.slice(-2000));throw e;}
finally{server.kill();}
