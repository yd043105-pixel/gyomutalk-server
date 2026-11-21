// Serves the student page of the generated script with made-up students, to look at it in a browser:
//   node tests/course-page-preview.mjs [port]   then open the printed links (one fresh, one whose course was closed after answering, two past the deadline).
import {randomBytes} from 'node:crypto';
import {readFileSync} from 'node:fs';
import {startScriptHost} from './course-script-host.mjs';
const host=await startScriptHost(Number(process.argv[2]??0));
host.load(readFileSync(new URL('../src/SchoolMessenger.Server/CourseScript.gs',import.meta.url),'utf8').replace('__SECRET__','preview').replace('__VERSION__','1'));
const b64=b=>Buffer.from(b).toString('base64url');
const groups=[{name:'선택군 1 (과학)',pick:2,courses:['물리학','화학','역학과 에너지','화학 반응의 세계','융합과학 탐구']},{name:'선택군 2 (사회)',pick:2,courses:['경제','정치','사회와 문화','세계사','윤리와 사상']},{name:'선택군 3 (교양)',pick:1,courses:['인간과 철학','논리와 사고','인간과 심리','진로와 직업']}];
const K={'물리학':['과학','일반선택'],'화학':['과학','일반선택'],'역학과 에너지':['과학','진로선택'],'화학 반응의 세계':['과학','진로선택'],'융합과학 탐구':['과학','융합선택'],'경제':['사회','진로선택'],'정치':['사회','진로선택'],'사회와 문화':['사회','일반선택'],'세계사':['사회','일반선택'],'윤리와 사상':['사회','진로선택'],'인간과 철학':['교양','진로선택'],'논리와 사고':['교양','진로선택'],'인간과 심리':['교양','진로선택'],'진로와 직업':['교양','일반선택']};groups.forEach(g=>g.kinds=Object.fromEntries(g.courses.filter(c=>K[c]).map(c=>[c,K[c]])));
const post=body=>fetch(host.url,{method:'POST',body:JSON.stringify({secret:'preview',...body})}).then(r=>r.json());
const links=[];
for(const [round,closes] of [['open',Date.now()+5*86400000],['closed',Date.now()-86400000]]){
 const students=[];
 for(const [n,name] of [[1,'가상일'],[2,'가상이']]){const token=b64(randomBytes(16));students.push(token);links.push([round+' '+name,`${host.base}/page?t=${token}#${b64(JSON.stringify({n:name,g:2,c:3,no:n}))}`,token]);}
 await post({action:'publish',round:{id:round,title:'2027학년도 3학년 선택과목 신청',closes,groups},students});
}
host.context.submit(links[1][2],[['물리학','화학'],['경제','세계사'],['인간과 심리']]);
// The school then closes 물리학: the student who had it sees the notice and picks again.
await post({action:'publish',round:{id:'open',title:'2027학년도 3학년 선택과목 신청',closes:Date.now()+5*86400000,groups:groups.map((g,i)=>i===0?{...g,closed:['물리학']}:g)},students:links.filter(l=>l[0].startsWith('open')).map(l=>l[2])});
for(const [label,link] of links)console.log(label+'\t'+link);
