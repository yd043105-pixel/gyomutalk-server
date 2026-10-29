import assert from 'node:assert/strict';
import {spawn,spawnSync} from 'node:child_process';
import {existsSync,mkdirSync,readFileSync,writeFileSync,symlinkSync} from 'node:fs';
import {randomUUID} from 'node:crypto';
import path from 'node:path';
import net from 'node:net';
import {writeDeploymentReviewFixture} from './deployment-review-fixture.mjs';

const root=process.cwd(),work=path.resolve(process.env.YY_TEST_DATA_ROOT??'.test-data','deployment-review-'+randomUUID());mkdirSync(work,{recursive:true});
const sdk=path.resolve('.tools/dotnet/dotnet.exe'),dotnet=existsSync(sdk)?sdk:'dotnet',checks=[],base='https://127.0.0.1:18443';
const env={...process.env,ASPNETCORE_ENVIRONMENT:'Production',Operations__SchoolId:'YY-SYNTHETIC-ONLY',DOTNET_PROCESSOR_COUNT:'2',DOTNET_CLI_TELEMETRY_OPTOUT:'1',Portal__TunnelOnly:'false',Announcements__Address:'',FeedbackMail__Enabled:'false',Operations__AlertMailEnabled:'false'};
const check=name=>{checks.push(name);console.log('PASS '+name);};
let serial=0;
for(const [project,service]of [['Server','office'],['AnnouncementServer','announcements']]){
 const dll=path.join(root,'src','SchoolMessenger.'+project,'bin/Debug/net10.0','SchoolMessenger.'+project+'.dll');
 function probe(name,edit,extra={},code=null,afterWrite=null){
  const directory=path.join(work,service+'-'+serial++),file=writeDeploymentReviewFixture(directory,service,base),record=JSON.parse(readFileSync(file,'utf8'));edit?.(record,directory);writeFileSync(file,JSON.stringify(record));afterWrite?.(file,directory);
  const data=path.join(directory,'must-not-create-data');
  const result=spawnSync(dotnet,[dll,'--deployment-review-check','--urls',base],{cwd:directory,env:{...env,Operations__SecurityReviewFile:file,School__DataDirectory:data,Portal__DataDirectory:data,...extra},windowsHide:true,encoding:'utf8',timeout:15000});
  const output=(result.stdout??'')+(result.stderr??'');assert.equal(result.error,undefined,output);
  if(code){assert.notEqual(result.status,0,name);assert.ok(output.includes(code),name+' '+output);}
  else{assert.equal(result.status,0,output);const summary=JSON.parse(result.stdout.trim());assert.equal(summary.state,'school-record-checked');assert.equal(summary.certificationVerified,false);assert.equal(summary.service,service);assert.ok(!result.stdout.includes('evidence.md'));}
  assert.ok(!existsSync(data),'Review checks must finish before the data store is opened');check(service+': '+name);
 }
 probe('missing review blocks Production before creating a database',null,{Operations__SecurityReviewFile:''},'review-required');
 probe('valid local review records are accepted without claiming certification');
 probe('read-only checks cannot run a configured restore',null,{'backend-restore':path.join(work,'not-an-actual-snapshot')});
 probe('draft cannot enable Production',r=>r.status='draft',{},'review-not-approved-for-service');
 probe('another service cannot reuse this record',r=>r.service=service==='office'?'announcements':'office',{},'review-not-approved-for-service');
 probe('another school cannot reuse the same record',null,{Operations__SchoolId:'OTHER-SCHOOL'},'review-school-mismatch');
 probe('expiry is enforced',r=>r.expiresAt=new Date(Date.now()-1).toISOString(),{},'review-date-invalid');
 probe('future review is not accepted',r=>r.reviewedAt=new Date(Date.now()+3600000).toISOString(),{},'review-date-invalid');
 probe('missing network submission is rejected',r=>delete r.documents.network,{},'review-document-missing');
 probe('changed evidence is rejected',r=>r.documents.decision.sha256='0'.repeat(64),{},'review-evidence-changed');
 probe('changed listener is rejected',r=>r.listenUrls=['https://127.0.0.1:19443'],{},'review-listeners-changed');
 probe('duplicate status fields are rejected',null,{},'review-duplicate-field',file=>writeFileSync(file,readFileSync(file,'utf8').replace('"status":"approved"','"status":"draft","STATUS":"approved"')));
 probe('unknown processing location is rejected',r=>r.storageCountries=['unknown'],{},'review-location-unconfirmed');
 probe('invented country codes are rejected',r=>r.managementCountries=['ZZ'],{},'review-location-unconfirmed');
 probe('private cloud needs supporting documents',r=>r.basis='private-cloud-41-2',{},'review-document-missing');
 probe('private-cloud domestic-location condition is enforced',r=>{r.basis='private-cloud-41-2';r.storageCountries=['US'];},{},'domestic-location-required');
 probe('education exception needs an applicability record',r=>r.basis='school-education-41-7',{},'review-document-missing');
 probe('exception is limited to the recorded education context',r=>{r.basis='school-education-41-7';r.institutionType='school-administration';},{},'education-applicability-required');
 const schoolException=r=>{r.basis='school-education-41-7';r.documents={'decision':r.documents.decision,'security':r.documents.security,'education-applicability':r.documents.decision};};
 probe('school education accepts local approval, applicability and safeguards without general agency submissions',schoolException);
 probe('school education still requires the school decision',r=>{schoolException(r);delete r.documents.decision;},{},'review-document-missing');
 probe('school education still requires local security measures',r=>{schoolException(r);delete r.documents.security;},{},'review-document-missing');
 probe('school education cannot bypass evidence integrity',r=>{schoolException(r);r.documents['education-applicability'].sha256='0'.repeat(64);},{},'review-evidence-changed');
 const mail=(r,purpose='error-mail')=>{r.basis='school-education-41-7';r.documents['education-applicability']=r.documents.decision;r.externalServices=[{purpose,host:'smtp.gmail.com',port:587,recipient:'synthetic@example.invalid',provider:'Synthetic cloud test',kind:'third-party-cloud',processingCountries:['US'],evidenceId:'security'}];};
 probe('cloud processing cannot be hidden under an on-premises basis',r=>{mail(r);r.basis='on-premises';},{},'cloud-review-basis-required');
 probe('reviewed cloud connection is only a record check',r=>mail(r));
 probe('unreviewed SMTP recipient is blocked',r=>mail(r),{FeedbackMail__Enabled:'true',FeedbackMail__Host:'smtp.gmail.com',FeedbackMail__Recipient:'other@example.invalid'},'review-destination-not-approved');
 probe('recipient case is not broadened for possibly case-sensitive mailboxes',r=>mail(r),{FeedbackMail__Enabled:'true',FeedbackMail__Host:'smtp.gmail.com',FeedbackMail__Recipient:'Synthetic@example.invalid'},'review-destination-not-approved');
 probe('unreviewed SMTP host is blocked',r=>mail(r),{FeedbackMail__Enabled:'true',FeedbackMail__Host:'smtp.other.invalid',FeedbackMail__Recipient:'synthetic@example.invalid'},'review-destination-not-approved');
 probe('operations alerts require their own reviewed purpose',r=>mail(r),{Operations__AlertMailEnabled:'true',Operations__AlertMailHost:'smtp.gmail.com',Operations__AlertMailRecipient:'synthetic@example.invalid'},'review-destination-not-approved');
 probe('reviewed operations alert is accepted',r=>mail(r,'operations-mail'),{Operations__AlertMailEnabled:'true',Operations__AlertMailHost:'smtp.gmail.com',Operations__AlertMailRecipient:'synthetic@example.invalid'});
 probe('unreviewed bridge destination is blocked',null,{Announcements__Address:'https://other-school.invalid'},'review-destination-not-approved');
 const cloud=r=>{r.basis='private-cloud-41-2';for(const name of ['domestic-location','public-separation','provider-eligibility','cloud-contract','csap-consideration','national-guidelines'])r.documents[name]=r.documents.security;};
 probe('private-cloud evidence and domestic declarations are checked',cloud);
 probe('CSAP consideration evidence cannot be omitted',r=>{cloud(r);delete r.documents['csap-consideration'];},{},'review-document-missing');
 probe('private-cloud external processing must also be domestic',r=>{mail(r);cloud(r);},{},'domestic-processing-required');
 if(service==='announcements'){
  probe('tunnel cannot use an unreviewed public hostname',null,{Portal__TunnelOnly:'true',Portal__PublicHost:'school.example.invalid',Portal__AllowedHosts:'school.example.invalid,localhost,127.0.0.1'},'review-destination-not-approved');
  probe('tunnel host must match the configured host allowlist',null,{Portal__TunnelOnly:'true',Portal__PublicHost:'school.example.invalid',Portal__AllowedHosts:'other.example.invalid'},'review-tunnel-host-required');
 }
 if(process.platform==='win32')probe('linked evidence is rejected',(r,directory)=>{const linked=path.join(directory,'linked');symlinkSync(directory,linked,'junction');r.documents.decision.path='linked/evidence.md';},{},'review-linked-file');
 const socket=net.createServer();await new Promise(resolve=>socket.listen(0,'127.0.0.1',resolve));const address='http://127.0.0.1:'+socket.address().port;await new Promise(resolve=>socket.close(resolve));
 const directory=path.join(work,service+'-api'),reviewFile=writeDeploymentReviewFixture(directory,service,address),password='Synthetic-'+randomUUID();let output='';
 const child=spawn(dotnet,[dll,'--urls',address],{cwd:directory,env:{...env,Operations__SecurityReviewFile:reviewFile,School__DataDirectory:path.join(directory,'data'),Portal__DataDirectory:path.join(directory,'data'),School__AdminPassword:password,Portal__AdminPassword:password,Portal__BridgeKey:'Synthetic-'+randomUUID()},windowsHide:true});child.stdout.on('data',b=>output+=b);child.stderr.on('data',b=>output+=b);
 try{
  let ready=false;for(let i=0;i<150;i++){if(child.exitCode!==null)throw Error(output);try{if((await fetch(address+'/health',{signal:AbortSignal.timeout(1000)})).ok){ready=true;break;}}catch{}await new Promise(r=>setTimeout(r,100));}assert.ok(ready,output);
  assert.equal((await fetch(address+'/api/admin/deployment-review')).status,401);
  const cookies=new Map();let csrf;
  async function request(route,body,expected=200){const headers={Cookie:[...cookies].map(([k,v])=>k+'='+v).join('; ')};if(body){headers['Content-Type']='application/json';headers['X-CSRF-TOKEN']=csrf;}const response=await fetch(address+'/api/'+route,{method:body?'POST':'GET',headers,body:body?JSON.stringify(body):undefined});for(const value of response.headers.getSetCookie()){const part=value.split(';')[0],at=part.indexOf('=');cookies.set(part.slice(0,at),part.slice(at+1));}assert.equal(response.status,expected,route);const text=await response.text();return text?JSON.parse(text):null;}
  csrf=(await request('session')).csrfToken;await request('login',{username:'admin',password});csrf=(await request('session')).csrfToken;
  const summary=await request('admin/deployment-review');assert.equal(summary.state,'school-record-checked');assert.equal(summary.certificationVerified,false);assert.ok(!JSON.stringify(summary).includes(reviewFile)&&!JSON.stringify(summary).includes('evidence.md'));
  if(service==='office'){await request('admin/users',{username:'synthetic.teacher',name:'Synthetic teacher',department:'Test',password,isAdmin:false,canBroadcast:false});await request('logout',{});csrf=(await request('session')).csrfToken;await request('login',{username:'synthetic.teacher',password});await request('admin/deployment-review',undefined,403);}
  check(service+': live Production endpoint requires administrator authority and omits evidence paths, hashes and email recipients');
 }finally{if(child.exitCode===null){child.kill();await new Promise(r=>child.once('exit',r));}}
}
mkdirSync('artifacts',{recursive:true});writeFileSync('artifacts/deployment-review-checks.json',JSON.stringify({checkedAt:new Date().toISOString(),version:'1.0.0',scope:'synthetic local evidence and startup controls; not legal or CSAP certification',checks},null,2));console.log(checks.length+' deployment-review checks passed.');
