import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {mkdirSync,existsSync,writeFileSync,rmSync} from 'node:fs';
import net from 'node:net';
import path from 'node:path';

// The administrator program writes school-app.json beside the data: phones learn the daily check time, and a moved
// server answers only with its new address so apps switch over without anyone typing it.
const root=process.cwd(),sdk=path.join(root,'.tools/dotnet/dotnet.exe'),dotnet=existsSync(sdk)?sdk:'dotnet';
const check=name=>console.log('PASS '+name);
async function port(){const s=net.createServer();await new Promise(r=>s.listen(0,'127.0.0.1',r));const p=s.address().port;await new Promise(r=>s.close(r));return p;}
const servers=[
 ['Server','School',p=>({School__DataDirectory:p,School__AdminPassword:'Synthetic-'+randomUUID()})],
 ['AnnouncementServer','Portal',p=>({Portal__DataDirectory:p,Portal__AdminPassword:'Synthetic-'+randomUUID(),Portal__BridgeKey:'Bridge-'+randomUUID()})]];
for(const [project,label,env] of servers){
 const data=path.join(process.env.YY_TEST_DATA_ROOT??path.join(root,'.test-data'),'school-move-'+randomUUID());mkdirSync(data,{recursive:true});
 const base='http://127.0.0.1:'+await port(),dll=path.join(root,'src/SchoolMessenger.'+project+'/bin/Debug/net10.0/SchoolMessenger.'+project+'.dll');
 const server=spawn(dotnet,[dll,'--urls',base],{cwd:path.dirname(dll),windowsHide:true,env:{...process.env,ASPNETCORE_ENVIRONMENT:'Development',...env(data)}});
 let logs='';server.stdout.on('data',b=>logs+=b);server.stderr.on('data',b=>logs+=b);
 try{
  for(let i=0;i<150;i++){try{if((await fetch(base+'/health')).ok)break;}catch{}await new Promise(r=>setTimeout(r,100));}
  const info=async()=>(await (await fetch(base+'/server-info')).json()).app;
  assert.deepEqual(await info(),{checkTime:'18:00',moving:null});
  check(label+': phones check for news at 18:00 unless the school sets another time');
  const settings=path.join(data,'school-app.json'),fingerprint='AB'.repeat(32),until=Date.now()+7*86_400_000;
  writeFileSync(settings,JSON.stringify({checkTime:'07:30',moving:{address:'https://new-server.example:18443/',fingerprint,until}}));
  assert.deepEqual(await info(),{checkTime:'07:30',moving:{address:'https://new-server.example:18443/',fingerprint,until}});
  const gone=await fetch(base+'/api/session');assert.equal(gone.status,410);
  assert.equal((await gone.json()).moving.address,'https://new-server.example:18443/');
  check(label+': a moved server shows the new address and certificate and refuses everything else');
  writeFileSync(settings,JSON.stringify({moving:{address:'http://plain.example/',fingerprint,until}}));
  assert.equal((await info()).moving,null);assert.equal((await fetch(base+'/api/session')).status,200);
  writeFileSync(settings,JSON.stringify({moving:{address:'https://new-server.example/',fingerprint,until:Date.now()-1}}));
  assert.equal((await info()).moving,null);assert.equal((await fetch(base+'/api/session')).status,200);
  rmSync(settings);assert.equal((await fetch(base+'/api/session')).status,200);
  check(label+': an expired, insecure or removed move notice puts the server back to normal');
 }catch(e){console.error(logs);throw e;}
 finally{server.kill();}
}
