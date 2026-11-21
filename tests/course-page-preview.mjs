// Serves the student page of the generated script with made-up students, to look at it in a browser:
//   node tests/course-page-preview.mjs [port]   then open the printed links (one fresh, one already answered, one closed).
import {randomBytes} from 'node:crypto';
import {readFileSync} from 'node:fs';
import {startScriptHost} from './course-script-host.mjs';
const host=await startScriptHost(Number(process.argv[2]??0));
host.load(readFileSync(new URL('../src/SchoolMessenger.Server/CourseScript.gs',import.meta.url),'utf8').replace('__SECRET__','preview').replace('__VERSION__','1'));
const b64=b=>Buffer.from(b).toString('base64url');
const groups=[{name:'선택군 1 (과학)',pick:2,courses:['물리학Ⅱ','화학Ⅱ','생명과학Ⅱ','지구과학Ⅱ','융합과학 탐구']},{name:'선택군 2 (사회)',pick:2,courses:['경제','정치와 법','사회·문화','세계사','윤리와 사상']},{name:'선택군 3 (교양)',pick:1,courses:['철학','논리학','심리학','진로와 직업']}];
const post=body=>fetch(host.url,{method:'POST',body:JSON.stringify({secret:'preview',...body})}).then(r=>r.json());
const links=[];
for(const [round,closes] of [['open',Date.now()+5*86400000],['closed',Date.now()-86400000]]){
 const students=[];
 for(const [n,name] of [[1,'가상일'],[2,'가상이']]){const token=b64(randomBytes(16));students.push(token);links.push([round+' '+name,`${host.base}/page?t=${token}#${b64(JSON.stringify({n:name,g:2,c:3,no:n}))}`,token]);}
 await post({action:'publish',round:{id:round,title:'2027학년도 3학년 선택과목 신청',closes,groups},students});
}
host.context.submit(links[1][2],[['물리학Ⅱ','화학Ⅱ'],['경제','세계사'],['심리학']]);
for(const [label,link] of links)console.log(label+'\t'+link);
