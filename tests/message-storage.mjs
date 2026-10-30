import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {mkdirSync,existsSync} from 'node:fs';
import {DatabaseSync} from 'node:sqlite';
import net from 'node:net';
import path from 'node:path';

// Message bodies live on PCs: the server keeps a body only until the sender and every recipient saved it (max 30 days).
const root=process.cwd(),sdk=path.join(root,'.tools/dotnet/dotnet.exe'),dotnet=existsSync(sdk)?sdk:'dotnet';
const data=path.join(process.env.YY_TEST_DATA_ROOT??path.join(root,'.test-data'),'message-storage-'+randomUUID());mkdirSync(data,{recursive:true});
const password='Synthetic-'+randomUUID();
const check=name=>console.log('PASS '+name);
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
}
const base='http://127.0.0.1:'+await port(),dll=path.join(root,'src/SchoolMessenger.Server/bin/Debug/net10.0/SchoolMessenger.Server.dll');
const server=spawn(dotnet,[dll,'--urls',base],{cwd:path.dirname(dll),env:{...process.env,ASPNETCORE_ENVIRONMENT:'Development',School__DataDirectory:data,School__AdminPassword:password,School__LoginLimitPerMinute:'300'},windowsHide:true});
let logs='';server.stdout.on('data',b=>logs+=b);server.stderr.on('data',b=>logs+=b);
try{
 for(let i=0;;i++){try{if((await fetch(base+'/health')).ok)break;}catch{}if(i>150||server.exitCode!==null)throw Error('Server did not start\n'+logs);await new Promise(r=>setTimeout(r,100));}
 const sender=await new Client(base).login();
 const user=await sender.request('admin/users','POST',{username:'recipient',name:'가상 수신자',department:'가상부서',password,isAdmin:false,canBroadcast:false});
 const recipient=await new Client(base).login('recipient');
 const form=new FormData();form.append('file',new Blob(['가상 첨부']),'note.txt');const file=await sender.request('attachments','POST',form);
 const send=async body=>(await sender.request('messages','POST',{clientId:randomUUID(),title:'가상 제목',body,recipientIds:[user.id],attachmentIds:body==='본문 하나'?[file.id]:[],all:false})).id;
 const id=await send('본문 하나');

 const peek=await recipient.request('messages/'+id+'?peek=true');
 assert.equal(peek.message.body,'본문 하나');assert.equal(peek.recipients[0].readAt,null);
 check('a PC can fetch a new message to save it without marking it read');

 await recipient.request('messages/'+id+'/stored','POST');
 assert.equal((await sender.request('messages/'+id+'?peek=true')).message.body,'본문 하나');
 await sender.request('messages/'+id+'/stored','POST');
 const after=await recipient.request('messages/'+id);
 assert.equal(after.message.body,'');assert.equal(after.message.purged,true);assert.equal(after.message.title,'가상 제목');
 assert.equal((await recipient.request('messages?box=received'))[0].preview,'');
 check('the server drops the body only after the sender and every recipient saved it, keeping the title and receipts');

 assert.equal(after.attachments[0].id,file.id);assert.equal(after.attachments[0].expired,false);
 const download=await fetch(base+'/api/attachments/'+file.id+'/download',{headers:{Cookie:[...recipient.cookies].map(([k,v])=>k+'='+v).join('; ')}});
 assert.equal(download.status,200);assert.equal(await download.text(),'가상 첨부');
 check('attachments stay linked to the message and downloadable for 30 days after the body is gone');

 await new Client(base).request('messages/'+id+'/stored','POST',undefined,401);
 const outsider=await sender.request('admin/users','POST',{username:'outsider',name:'가상 외부인',department:'가상부서',password,isAdmin:false,canBroadcast:false});
 await (await new Client(base).login('outsider')).request('messages/'+id+'/stored','POST',undefined,404);
 check('only the sender or a recipient can confirm a saved copy');

 const unsaved=await send('저장 안 된 본문'),fresh=await send('최근 본문');
 const db=new DatabaseSync(path.join(data,'school.db'));db.exec('PRAGMA busy_timeout=30000');
 const old=Date.now()-31*86_400_000;
 db.prepare('UPDATE Messages SET CreatedAt=$old WHERE Id=$id').run({$old:old,$id:unsaved});
 await sender.request('admin/maintenance','POST');
 assert.equal((await sender.request('messages/'+unsaved+'?peek=true')).message.body,'저장 안 된 본문');
 check('messages from before the upgrade get the full 30-day grace period');
 db.exec('UPDATE MessageStorage SET Since=0');db.close();
 await sender.request('admin/maintenance','POST');
 assert.equal((await sender.request('messages/'+unsaved+'?peek=true')).message.purged,true);
 assert.equal((await sender.request('messages/'+fresh+'?peek=true')).message.body,'최근 본문');
 check('an unsaved body is dropped after 30 days, newer ones are kept');

 await send('담임 선생님께서는 11월 7일까지 학급별 인솔 명단을 제출해 주세요.');
 const todo=(await recipient.request('todos')).find(t=>t.title.includes('인솔 명단'));
 assert.equal(todo.title,'담임 선생님께서는 학급별 인솔 명단 제출');assert.match(todo.dueDate??'',/-11-07$/);
 check('a date in the middle of a request leaves no stray particle in the suggested task title');
}finally{if(server.exitCode===null){server.kill();await new Promise(r=>server.once('exit',r));}}
