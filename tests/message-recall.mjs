import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {DatabaseSync} from 'node:sqlite';
import {existsSync} from 'node:fs';
import path from 'node:path';

export async function recallChecks({owner,recipient,observer,admin,data,address,check,start,stop,ready}){
 const send=(extra={})=>owner.request('/api/messages','POST',{clientId:randomUUID(),title:'회수 검사',body:'회수 가능한 문서입니다.',recipientIds:[recipient.user.id,observer.user.id],attachmentIds:[],all:false,...extra});
 const file=await owner.upload('회수검사.txt','회수된 첨부 내용');
 const packet={clientId:randomUUID(),attachmentIds:[file.id],body:'내일까지 자료를 제출해주세요.'};const message=await send(packet);
 await recipient.request('/api/messages/'+message.id+'/recall','POST',{},404);await admin.request('/api/messages/'+message.id+'/recall','POST',{},404);
 await owner.request('/api/messages/'+message.id+'/recall','POST');await owner.request('/api/messages/'+message.id+'/recall','POST');
 for(const c of [owner,recipient,observer])await c.request('/api/messages/'+message.id,'GET',undefined,404);
 assert.ok(!(await recipient.request('/api/messages')).some(m=>m.id===message.id));
 assert.ok(!(await recipient.request('/api/todos')).some(t=>t.sourceId===message.id));
 await recipient.request('/api/messages/'+message.id+'/read','POST',{},404);await recipient.request('/api/attachments/'+file.id+'/download','GET',undefined,404);
 assert.equal((await send(packet)).id,message.id); // A lost response must never resend a recalled message.
 assert.ok((await recipient.request('/api/messages/changes')).events.some(e=>e.kind==='MessageRecalled'&&e.resourceId===message.id));
 const db=new DatabaseSync(path.join(data,'school.db')); db.exec('PRAGMA busy_timeout=5000');assert.equal(db.prepare('SELECT Body FROM Messages WHERE Id=?').get(message.id).Body,'');db.close();
 await admin.request('/api/admin/maintenance','POST');assert.equal(existsSync(path.join(data,'files',file.id)),false);
 check('sender-only unread recall, retry tombstones, hidden content/files/task suggestions and durable recall synchronization');
 const read=await send();await recipient.request('/api/messages/'+read.id);
 assert.ok((await owner.request('/api/messages/'+read.id)).recipients.find(r=>r.id===recipient.user.id).readAt);
 await owner.request('/api/messages/'+read.id+'/recall','POST');
 for(const c of [recipient,observer]){await c.request('/api/messages/'+read.id,'GET',undefined,404);assert.ok(!(await c.request('/api/messages')).some(m=>m.id===read.id));assert.ok((await c.request('/api/messages/changes')).events.some(e=>e.kind==='MessageRecalled'&&e.resourceId===read.id));}
 const attachment=await owner.upload('열람.txt','파일 열람 검사');const download=await send({attachmentIds:[attachment.id]});await recipient.request('/api/attachments/'+attachment.id+'/download');await owner.request('/api/messages/'+download.id+'/recall','POST');await recipient.request('/api/attachments/'+attachment.id+'/download','GET',undefined,404);
 const allRead=await send();await recipient.request('/api/messages/'+allRead.id);await observer.request('/api/messages/'+allRead.id);await owner.request('/api/messages/'+allRead.id+'/recall','POST',{},409);
 const single=await send({recipientIds:[recipient.user.id]});await recipient.request('/api/messages/'+single.id);await owner.request('/api/messages/'+single.id+'/recall','POST',{},409);
 check('one remaining unread recipient permits recall even after another opens/downloads; read and unread recipients lose access; all-read and one-person read messages cannot be recalled');
 for(let i=0;i<12;i++){
  const race=await send();await observer.request('/api/messages/'+race.id);const responses=await Promise.all([fetch(address+'/api/messages/'+race.id,{headers:{Cookie:[...recipient.cookies].map(([k,v])=>k+'='+v).join('; ')}}),fetch(address+'/api/messages/'+race.id+'/recall',{method:'POST',headers:{Cookie:[...owner.cookies].map(([k,v])=>k+'='+v).join('; '),'X-CSRF-TOKEN':owner.csrf,'Content-Type':'application/json'},body:'{}'})]);
  assert.ok((responses[0].status===200&&responses[1].status===409)||(responses[0].status===404&&responses[1].status===200),responses.map(r=>r.status).join(','));await Promise.all(responses.map(r=>r.arrayBuffer()));
 }
 check('twelve concurrent last-unread-open/recall races have exactly one authorized outcome');
 const active=await send({body:'내일까지 확인서를 제출해주세요.',submissionDeadline:Date.now()+3*86400000});
 const taskId=await recipient.request('/api/todos','POST',{title:'확인서 제출',dueDate:null,status:'open',revision:0,sourceId:active.id});
 const task=(await recipient.request('/api/todos')).find(t=>t.id===taskId.id);
 await recipient.request('/api/todos/'+task.id,'PATCH',{title:task.title,dueDate:task.dueDate,status:'open',revision:task.revision});
 const collected=await recipient.upload('회수요청_제출.txt','이미 제출한 가상 자료');
 await recipient.request('/api/submission-requests/'+active.id+'/submit','POST',{clientId:randomUUID(),attachmentIds:[collected.id]});
 await recipient.request('/api/messages/'+active.id);
 await owner.request('/api/messages/'+active.id+'/recall','POST');
 for(const c of [owner,recipient,observer])await c.request('/api/submission-requests/'+active.id,'GET',undefined,404);
 await recipient.request('/api/attachments/'+collected.id+'/download','GET',undefined,404);
 assert.equal((await recipient.request('/api/todos')).find(t=>t.id===task.id).status,'open');
 const activityDb=new DatabaseSync(path.join(data,'school.db')); activityDb.exec('PRAGMA busy_timeout=5000');assert.equal(activityDb.prepare('SELECT Closed FROM SubmissionRequests WHERE MessageId=?').get(active.id).Closed,1);assert.equal(activityDb.prepare('SELECT COUNT(*) n FROM Submissions WHERE RequestId=?').get(active.id).n,1);activityDb.close();
 check('remaining unread recipient still allows recall after task confirmation/submission; registered tasks persist and collection access closes');
 const request=await send({submissionDeadline:Date.now()+3*86400000});
 const firstFile=await recipient.upload('초안.txt','회수할 초안');const firstPacket={clientId:randomUUID(),attachmentIds:[firstFile.id]};const first=await recipient.request('/api/submission-requests/'+request.id+'/submit','POST',firstPacket);
 const secondFile=await recipient.upload('수정.txt','회수할 수정본');const second=await recipient.request('/api/submission-requests/'+request.id+'/submit','POST',{clientId:randomUUID(),attachmentIds:[secondFile.id]});
 await recipient.request('/api/submission-requests/'+request.id+'/withdraw','POST',{submissionId:first.id},409);
 await observer.request('/api/submission-requests/'+request.id+'/withdraw','POST',{submissionId:second.id},409);await admin.request('/api/submission-requests/'+request.id+'/withdraw','POST',{submissionId:second.id},404);await owner.request('/api/submission-requests/'+request.id+'/withdraw','POST',{submissionId:second.id},404);
 await recipient.request('/api/submission-requests/'+request.id+'/withdraw','POST',{submissionId:second.id});await recipient.request('/api/submission-requests/'+request.id+'/withdraw','POST',{submissionId:second.id});
 const withdrawn=(await owner.request('/api/submission-requests/'+request.id)).targets.find(t=>t.id===recipient.user.id);assert.equal(withdrawn.submissionId,null);assert.equal(withdrawn.files.length,0);
 assert.equal((await recipient.request('/api/submission-requests')).find(r=>r.id===request.id).submitted,false);
 for(const old of [firstFile,secondFile])await owner.request('/api/attachments/'+old.id+'/download','GET',undefined,404);
 await admin.request('/api/admin/maintenance','POST');for(const old of [firstFile,secondFile])assert.equal(existsSync(path.join(data,'files',old.id)),false);
 await recipient.request('/api/submission-requests/'+request.id+'/submit','POST',firstPacket,409);
 await stop();start();await ready();assert.equal((await recipient.request('/api/submission-requests/'+request.id)).targets[0].submittedAt,null);
 const thirdFile=await recipient.upload('최종.txt','다시 제출한 최종본');const third=await recipient.request('/api/submission-requests/'+request.id+'/submit','POST',{clientId:randomUUID(),attachmentIds:[thirdFile.id]});
 await recipient.request('/api/submission-requests/'+request.id+'/withdraw','POST',{submissionId:second.id},409);assert.equal((await owner.request('/api/submission-requests/'+request.id)).targets.find(t=>t.id===recipient.user.id).submissionId,third.id);
 await owner.request('/api/submission-requests/'+request.id+'/close','POST');await recipient.request('/api/submission-requests/'+request.id+'/withdraw','POST',{submissionId:third.id},409);
 check('submit without a reply, withdraw own current version, no older-file revival, restart persistence, resubmission and closed/stale/foreign protection');
}
