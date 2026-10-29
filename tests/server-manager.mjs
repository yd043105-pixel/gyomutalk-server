import assert from 'node:assert/strict';
import {spawn,spawnSync} from 'node:child_process';
import {mkdirSync,existsSync,symlinkSync,readFileSync,writeFileSync} from 'node:fs';
import path from 'node:path';
import net from 'node:net';
import {randomUUID} from 'node:crypto';
assert.equal(process.platform,'win32','Native Windows GUI verification requires Windows');
const root=process.cwd(),work=path.join(root,'.test-data','server-manager-'+randomUUID()),installed=path.join(work,'installed'),output=path.join(root,'artifacts','server-manager');
const dotnet=existsSync('.tools/dotnet/dotnet.exe')?path.resolve('.tools/dotnet/dotnet.exe'):'dotnet';
const env={...process.env,DOTNET_PROCESSOR_COUNT:'2',DOTNET_CLI_TELEMETRY_OPTOUT:'1',...(path.isAbsolute(dotnet)?{DOTNET_ROOT:path.dirname(dotnet)}:{})};
for(const port of [5080,5081])await new Promise((resolve,reject)=>{const server=net.createServer();server.once('error',()=>reject(Error('Test port already in use: '+port)));server.listen(port,'127.0.0.1',()=>server.close(resolve));});
if(!process.argv.includes('--no-build'))for(const project of ['Server','AnnouncementServer','Desktop']){const result=spawnSync(dotnet,['build','src/SchoolMessenger.'+project,'--no-restore','--nologo','-v','quiet'],{env,stdio:'inherit'});assert.equal(result.status,0,project+' build failed');}
mkdirSync(installed,{recursive:true});mkdirSync(output,{recursive:true});
for(const [folder,project,framework]of [['server','Server','net10.0'],['announcement-server','AnnouncementServer','net10.0'],['admin','Desktop','net10.0-windows']])symlinkSync(path.join(root,'src','SchoolMessenger.'+project,'bin','Debug',framework),path.join(installed,folder),'junction');
const app=path.join(installed,'admin','SchoolMessenger.exe');
async function run(extra=[]){await new Promise((resolve,reject)=>{const child=spawn(app,['--verify-server-manager',...extra],{env:{...env,YY_MANAGER_ROOT:installed,YY_MANAGER_STATE:path.join(work,'state'),YY_MANAGER_OUTPUT:output},windowsHide:true,stdio:'ignore'});const timeout=setTimeout(()=>reject(Error('Native manager check timed out; see '+output)),180000);child.once('error',e=>{clearTimeout(timeout);reject(e)});child.once('exit',code=>{clearTimeout(timeout);code===0?resolve():reject(Error('Native manager failed: '+(existsSync(path.join(output,'server-manager-error.txt'))?readFileSync(path.join(output,'server-manager-error.txt'),'utf8'):'exit '+code)))});});}
await run();const report=JSON.parse(readFileSync(path.join(output,'server-manager-checks.json'),'utf8'));for(const item of report.checks)console.log('PASS '+item);
await run(['--owner-exit']);
for(const port of [5080,5081]){let stopped=false;for(let n=0;n<60;n++){try{await fetch('http://127.0.0.1:'+port+'/health',{signal:AbortSignal.timeout(400)});}catch{stopped=true;break;}await new Promise(r=>setTimeout(r,250));}assert.ok(stopped,'A managed server survived its owner exit');}
report.checks.push('managed servers shut down when their manager process exits; no orphaned backend remains');writeFileSync(path.join(output,'server-manager-checks.json'),JSON.stringify(report,null,2));console.log(report.checks.length+' native server-manager checks passed.');
