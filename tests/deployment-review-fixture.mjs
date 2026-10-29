import {mkdirSync,writeFileSync} from 'node:fs';
import {createHash} from 'node:crypto';
import path from 'node:path';

// Synthetic evidence for isolated tests only. This helper is never shipped to a school.
export function writeDeploymentReviewFixture(directory,service,address){
 directory=path.resolve(directory);mkdirSync(directory,{recursive:true});
 const evidence='SYNTHETIC TEST ONLY — not a school approval, legal opinion, or security certificate.\n';
 writeFileSync(path.join(directory,'evidence.md'),evidence);
 const sha256=createHash('sha256').update(evidence).digest('hex');
 const documents=Object.fromEntries(['decision','overview','plan','requirements','network','security'].map(name=>[name,{path:'evidence.md',sha256}]));
 const review={schema:1,status:'approved',school:'Synthetic isolated test school',schoolId:'YY-SYNTHETIC-ONLY',service,basis:'on-premises',institutionType:'education-site',reviewAuthority:'Synthetic test only — no actual authority approval',reviewedAt:new Date(Date.now()-60000).toISOString(),expiresAt:new Date(Date.now()+86400000).toISOString(),listenUrls:[address],storageCountries:['KR'],managementCountries:['KR'],documents,externalServices:[]};
 const file=path.join(directory,'review.json');writeFileSync(file,JSON.stringify(review));return file;
}
