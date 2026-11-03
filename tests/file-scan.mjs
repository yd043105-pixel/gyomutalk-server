import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import {existsSync,mkdirSync,renameSync,writeFileSync} from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import {writeDeploymentReviewFixture} from './deployment-review-fixture.mjs';

// Runs in Production because Development skips the scanner. A stand-in scanner plays each answer the real one can give.
const root=process.cwd(),sdk=path.join(root,'.tools/dotnet/dotnet.exe'),dotnet=existsSync(sdk)?sdk:'dotnet';
const work=path.resolve(process.env.YY_TEST_DATA_ROOT??'.test-data','file-scan-'+randomUUID());mkdirSync(work,{recursive:true});
const password='Synthetic-'+randomUUID(),check=name=>console.log('PASS '+name);
const listener=net.createServer();await new Promise(r=>listener.listen(0,'127.0.0.1',r));const port=listener.address().port;await new Promise(r=>listener.close(r));
const base='http://127.0.0.1:'+port,scanner=path.join(work,'scanner.cmd'),mode=path.join(work,'mode.txt');
writeFileSync(scanner,['@echo off','set /p mode=<"%~dp0mode.txt"','if "%mode%"=="clean" exit /b 0','if "%mode%"=="failed" (echo CmdTool: Failed with hr = 0x80004005. Check the log.& exit /b 2)','if "%mode%"=="threat" (echo Scanning found 1 threats.& exit /b 2)','if "%mode%"=="quarantine" (del /q "%~5"& exit /b 0)','exit /b 7',''].join('\r\n'));
const review=writeDeploymentReviewFixture(path.join(work,'review'),'office',base);
const dll=path.join(root,'src/SchoolMessenger.Server/bin/Debug/net10.0/SchoolMessenger.Server.dll');
const server=spawn(dotnet,[dll,'--urls',base],{cwd:path.dirname(dll),windowsHide:true,env:{...process.env,ASPNETCORE_ENVIRONMENT:'Production',Operations__SecurityReviewFile:review,Operations__SchoolId:'YY-SYNTHETIC-ONLY',School__DataDirectory:path.join(work,'data'),School__AdminPassword:password,School__ScannerPath:scanner,School__LoginLimitPerMinute:'300',Portal__TunnelOnly:'false',Announcements__Address:'',FeedbackMail__Enabled:'false',Operations__AlertMailEnabled:'false',DOTNET_CLI_TELEMETRY_OPTOUT:'1'}});
let logs='';server.stdout.on('data',b=>logs+=b);server.stderr.on('data',b=>logs+=b);
const png=Buffer.from([137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,1,0,0,0,1,8,6,0,0,0,31,21,196,137,0,0,0,10,73,68,65,84,120,156,99,0,1,0,0,5,0,1,13,10,45,180,0,0,0,0,73,69,78,68,174,66,96,130]);
let cookies='',csrf='';
async function call(route,method='GET',body){
 const headers={Cookie:cookies,'X-CSRF-TOKEN':csrf};if(body!==undefined&&!(body instanceof FormData))headers['Content-Type']='application/json';
 const response=await fetch(base+route,{method,headers,body:body===undefined||body instanceof FormData?body:JSON.stringify(body)});
 for(const c of response.headers.getSetCookie()){const p=c.split(';')[0],n=p.split('=')[0];cookies=[...cookies.split('; ').filter(x=>x&&!x.startsWith(n+'=')),p].join('; ');}
 const text=await response.text();let value=text;try{value=JSON.parse(text);}catch{}
 return {status:response.status,value};
}
async function upload(state){
 writeFileSync(mode,state);const form=new FormData();form.set('file',new Blob([png]),'capture.png');
 return call('/api/attachments','POST',form);
}
try{
 for(let i=0;i<150;i++){if(server.exitCode!==null)throw Error('server stopped');try{if((await fetch(base+'/health')).ok)break;}catch{}await new Promise(r=>setTimeout(r,100));}
 csrf=(await call('/api/session')).value.csrfToken;assert.equal((await call('/api/login','POST',{username:'admin',password,rememberLogin:false})).status,200);csrf=(await call('/api/session')).value.csrfToken;

 assert.equal((await upload('clean')).status,200);
 check('a clean scan accepts a captured image');
 assert.equal((await upload('failed')).status,200);
 check('when the scanner cannot run (Defender switched off beside another antivirus) the image is accepted');
 const threat=await upload('threat');assert.equal(threat.status,422);assert.match(threat.value.error,/올릴 수 없습니다/);
 check('a threat the scanner reports is refused');
 assert.equal((await upload('unknown')).status,422);
 check('an answer the program cannot read is refused rather than assumed safe');
 assert.equal((await upload('quarantine')).status,422);
 check('a file taken away during the scan is refused');
 renameSync(scanner,scanner+'.off');
 assert.equal((await upload('clean')).status,200);
 renameSync(scanner+'.off',scanner);
 check('a PC with no scanner program at all still accepts files');
 const fake=new FormData();fake.set('file',new Blob([Buffer.from('not an image')]),'capture.png');writeFileSync(mode,'clean');
 const mismatch=await call('/api/attachments','POST',fake);assert.equal(mismatch.status,400);assert.match(mismatch.value.error,/일치하지 않습니다/);
 check('a file whose content does not match its name is refused before scanning');
}catch(e){console.error(logs.slice(-3000));throw e;}
finally{server.kill();}
