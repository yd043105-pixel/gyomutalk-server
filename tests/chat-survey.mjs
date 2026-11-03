import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {mkdirSync,existsSync,readFileSync} from 'node:fs';
import {DatabaseSync} from 'node:sqlite';
import net from 'node:net';
import path from 'node:path';

// Chat rooms a person renames or leaves, and surveys whose answers nobody can trace back to a person.
const root=process.cwd(),sdk=path.join(root,'.tools/dotnet/dotnet.exe'),dotnet=existsSync(sdk)?sdk:'dotnet';
const data=path.join(process.env.YY_TEST_DATA_ROOT??path.join(root,'.test-data'),'chat-survey-'+randomUUID());mkdirSync(data,{recursive:true});
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
 async login(username='admin'){this.csrf=(await this.request('session')).csrfToken;await this.request('login','POST',{username,password,rememberLogin:false});const s=await this.request('session');this.csrf=s.csrfToken;this.user=s.user;return this;}
}
const base='http://127.0.0.1:'+await port(),dll=path.join(root,'src/SchoolMessenger.Server/bin/Debug/net10.0/SchoolMessenger.Server.dll');
const server=spawn(dotnet,[dll,'--urls',base],{cwd:path.dirname(dll),env:{...process.env,ASPNETCORE_ENVIRONMENT:'Development',School__DataDirectory:data,School__AdminPassword:password,School__LoginLimitPerMinute:'300'},windowsHide:true});
let logs='';server.stdout.on('data',b=>logs+=b);server.stderr.on('data',b=>logs+=b);
try{
 for(let i=0;;i++){try{if((await fetch(base+'/health')).ok)break;}catch{}if(i>150||server.exitCode!==null)throw Error('Server did not start\n'+logs);await new Promise(r=>setTimeout(r,100));}
 const admin=await new Client(base).login();
 const person=async username=>{await admin.request('admin/users','POST',{username,name:'가상 '+username,department:'가상부서',password,isAdmin:false,canBroadcast:false});return new Client(base).login(username);};
 const [a,b,c]=await Promise.all(['teacher-a','teacher-b','teacher-c'].map(person));
 const say=(who,room,body)=>who.request('chats/'+room+'/messages','POST',{clientId:randomUUID(),body,attachmentIds:[]});
 const room=(who,id)=>who.request('chats').then(rooms=>rooms.find(r=>r.id===id));

 const group=(await a.request('chats','POST',{memberIds:[b.user.id,c.user.id],name:'가상 협의'})).id;
 await say(b,group,'첫 안내 내용입니다');
 const seen=await room(a,group);assert.equal(seen.preview,'첫 안내 내용입니다');assert.equal(seen.unread,1);assert.ok(seen.lastAt>0);
 check('the room list carries a preview, a time and an unread count');

 await a.request('chats/'+group+'/name','POST',{name:'내 이름표'});
 assert.equal((await room(a,group)).alias,'내 이름표');assert.equal((await room(b,group)).alias,'');assert.equal((await room(b,group)).name,'가상 협의');
 check('a new room name shows only on the screen of the person who chose it');
 await a.request('chats/'+group+'/name','POST',{name:'x'.repeat(101)},400);
 await a.request('chats/'+group+'/name','POST',{name:''});assert.equal((await room(a,group)).alias,'');
 check('a name stays within 100 characters and can be cleared again');

 await c.request('chats/'+group+'/leave','POST');
 assert.equal(await room(c,group),undefined);await c.request('chats/'+group+'/messages','GET',undefined,404);
 assert.ok(!(await room(a,group)).members.some(m=>m.id===c.user.id));
 await say(a,group,'나간 뒤의 메시지');assert.equal(await room(c,group),undefined);
 check('leaving a group is final: the room, its messages and new messages are gone for that person');
 await c.request('chats/'+group+'/leave','POST',undefined,404);
 await c.request('chats/'+group+'/name','POST',{name:'몰래'},404);
 check('someone who left can no longer act on the room');

 const direct=(await a.request('chats','POST',{memberIds:[b.user.id]})).id;
 await say(b,direct,'하나');await say(b,direct,'둘');
 await a.request('chats/'+direct+'/leave','POST');
 assert.equal(await room(a,direct),undefined);assert.ok(await room(b,direct));
 check('clearing a one-to-one room leaves only that person\'s list');
 await say(b,direct,'셋');
 const back=await room(a,direct);assert.ok(back);assert.equal(back.unread,1);
 check('a cleared one-to-one room returns with the next message, counting only what is new');
 await a.request('chats/'+direct+'/leave','POST');
 assert.equal((await a.request('chats','POST',{memberIds:[b.user.id]})).id,direct);assert.ok(await room(a,direct));
 check('starting the same one-to-one conversation again brings the room back');

 const relay=(await a.request('chats','POST',{memberIds:[b.user.id,c.user.id],name:'중간다리 시험'})).id;
 await say(a,relay,'SECRET-CHAT-BODY');
 const pending=await a.request('chats/'+relay+'/messages');assert.equal(pending.length,1);const seq=pending[0].sequence;
 await a.request('chats/'+relay+'/stored','POST',{sequence:seq});
 assert.deepEqual(await a.request('chats/'+relay+'/messages'),[]);assert.equal((await room(a,relay)).preview,'');
 assert.equal((await b.request('chats/'+relay+'/messages')).length,1);
 check('once my PC saved a chat message, the same account on another PC gets nothing, while others not yet saved still can');
 await b.request('chats/'+relay+'/stored','POST',{sequence:seq});
 await c.request('chats/'+relay+'/leave','POST');
 const chatDb=new DatabaseSync(path.join(data,'school.db'),{readOnly:true});
 assert.equal(chatDb.prepare('SELECT COUNT(*) n FROM Messages m JOIN ChatEntries e ON e.MessageId=m.Id WHERE e.RoomId=? AND (m.Body<>\'\' OR m.Title<>\'\')').get(relay).n,0);chatDb.close();
 assert.equal((await room(a,relay)).lastAt>0,true);
 check('a chat message leaves the server once every member saved it, and someone leaving does not hold it back');
 const other=(await a.request('chats','POST',{memberIds:[b.user.id,c.user.id],name:'다른 PC'})).id;await say(a,other,'나중 내용');
 await a.request('chats/'+other+'/stored','POST',{sequence:(await a.request('chats/'+other+'/messages'))[0].sequence});
 await say(b,other,'새 내용');
 assert.deepEqual((await a.request('chats/'+other+'/messages')).map(m=>m.body),['새 내용']);
 check('after saving, only messages new to this account are handed out');

 const survey=extra=>a.request('surveys','POST',{title:'가상 설문',description:'',deadline:Date.now()+86400000,targetIds:[a.user.id,b.user.id,c.user.id],all:false,questions:[{text:'가능한 시간',kind:'choice',required:true,options:['15시','16시']},{text:'의견',kind:'text',required:false,options:[]}],...extra});
 const named=(await survey({})).id;
 await b.request('surveys/'+named+'/answers','POST',{answers:['15시','이름 있는 의견']});
 await b.request('surveys/'+named+'/answers','POST',{answers:['16시','이름 있는 의견']});
 const open=await a.request('surveys/'+named+'/results');assert.equal(open.anonymous,false);assert.equal(open.responses[0].name,'가상 teacher-b');assert.equal(open.responses[0].answers[0],'16시');
 check('a named survey still shows who answered and allows a change before the deadline');

 const hidden=(await survey({anonymous:true})).id;
 const before=(await b.request('surveys')).find(s=>s.id===hidden);assert.equal(before.anonymous,true);assert.equal(before.answered,false);assert.equal(before.target,true);
 assert.equal((await b.request('surveys/'+hidden)).canAnswer,true);
 await b.request('surveys/'+hidden+'/answers','POST',{answers:['15시','SECRET-OPINION-1']});
 await c.request('surveys/'+hidden+'/answers','POST',{answers:['16시','SECRET-OPINION-2']});
 const after=await b.request('surveys/'+hidden);assert.equal(after.answered,true);assert.equal(after.canAnswer,false);assert.equal(after.answers,null);
 check('after answering, an anonymous survey hides even your own answer');
 await b.request('surveys/'+hidden+'/answers','POST',{answers:['16시','바꾼 답']},409);
 check('an anonymous answer cannot be changed or sent twice');
 await a.request('surveys/'+hidden+'/answers','POST',{answers:['','']},400);
 check('a required question is still enforced');
 assert.equal((await a.request('surveys')).find(s=>s.id===hidden).responseCount,2);
 const results=await a.request('surveys/'+hidden+'/results');assert.equal(results.anonymous,true);assert.equal(results.responses.length,2);
 for(const r of results.responses){assert.equal(r.name,'');assert.equal(r.department,'');assert.equal(r.updatedAt,0);assert.notEqual(r.id,b.user.id);assert.notEqual(r.id,c.user.id);}
 assert.deepEqual(results.responses.map(r=>r.answers[1]).sort(),['SECRET-OPINION-1','SECRET-OPINION-2']);
 check('the owner sees the answers with no name, department or time');
 await b.request('surveys/'+hidden+'/results','GET',undefined,404);
 check('only the owner sees the results');
 await b.request('surveys/'+hidden+'/ballots/stored','POST',{ids:results.responses.map(r=>r.id)},404);
 await a.request('surveys/'+hidden+'/ballots/stored','POST',{ids:results.responses.map(r=>r.id)});
 assert.deepEqual((await a.request('surveys/'+hidden+'/results')).responses,[]);
 assert.equal((await a.request('surveys')).find(s=>s.id===hidden).responseCount,2);
 check('anonymous answers move to the owner PC and leave the server, keeping only the count');

 const db=new DatabaseSync(path.join(data,'school.db'),{readOnly:true});
 const columns=name=>db.prepare('PRAGMA table_info('+name+')').all().map(x=>x.name);
 assert.deepEqual(columns('SurveyBallots').sort(),['Answers','Id','SurveyId']);assert.deepEqual(columns('SurveyVoters').sort(),['SurveyId','UserId']);
 assert.throws(()=>db.prepare('SELECT rowid FROM SurveyBallots').all());assert.throws(()=>db.prepare('SELECT rowid FROM SurveyVoters').all());
 assert.equal(db.prepare('SELECT COUNT(*) n FROM SurveyResponses WHERE SurveyId=?').get(hidden).n,0);
 assert.equal(db.prepare('SELECT COUNT(*) n FROM SurveyBallots WHERE SurveyId=?').get(hidden).n,0);
 db.close();
 check('the database keeps voters and answers apart, with no time, order or shared key to pair them');
 await admin.request('admin/maintenance','POST');
 const raw=readFileSync(path.join(data,'school.db')).toString('latin1');
 for(const word of ['SECRET-OPINION-1','SECRET-OPINION-2','SECRET-CHAT-BODY'])assert.ok(!raw.includes(word),word);
 check('dropped answers and chat cannot be recovered from the database file');
}catch(e){console.error(logs.slice(-3000));throw e;}
finally{server.kill();}
