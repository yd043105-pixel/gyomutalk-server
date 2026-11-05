import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {DatabaseSync} from 'node:sqlite';
import path from 'node:path';

export async function scheduledChecks({Client,admin,owner,recipient,observer,data,check,start,stop,ready}){
 const pause=ms=>new Promise(r=>setTimeout(r,ms));
 const db=()=>{const connection=new DatabaseSync(path.join(data,'school.db'));connection.exec('PRAGMA busy_timeout=5000');return connection;};
 const make=(extra={})=>({clientId:randomUUID(),title:'예약 점검',body:'가상 예약 전송 시운행입니다.',recipientIds:[recipient.user.id],attachmentIds:[],all:false,scheduledAt:Date.now()+3600000,...extra});
 async function waitFor(id,state){for(let i=0;i<150;i++){const rows=await owner.request('/api/messages/scheduled');const row=rows.find(r=>r.id===id);if(row?.state===state)return row;await pause(100);}throw Error('Schedule did not become '+state);}
 function force(id){const d=db();d.prepare('UPDATE ScheduledMessages SET DeliverAt=? WHERE Id=?').run(Date.now()-1,id);d.close();}
 const input=make(),saved=await owner.request('/api/messages','POST',input);
 assert.equal(saved.scheduled,true);assert.equal((await owner.request('/api/messages','POST',input)).id,saved.id);
 await owner.request('/api/messages','POST',{...input,body:'다른 내용'},409);
 await owner.request('/api/messages','POST',{...input,scheduledAt:null},409);
 assert.ok(!(await owner.request('/api/messages?box=sent')).some(m=>m.title===input.title));
 assert.equal((await observer.request('/api/messages/scheduled')).length,0);
 await observer.request('/api/messages/scheduled/'+saved.id,'DELETE',undefined,409);
 await owner.request('/api/messages/scheduled/'+saved.id,'DELETE');assert.equal((await owner.request('/api/messages/scheduled')).find(r=>r.id===saved.id).state,'cancelled');
 check('scheduled messages remain private before delivery; identical retries deduplicate, changed/immediate reuse rejected and only owner can cancel');
 await owner.request('/api/messages','POST',make({scheduledAt:Date.now()-1}),400);
 await owner.request('/api/messages','POST',make({all:true,recipientIds:[]}),403);
 const own=await owner.request('/api/messages','POST',{...make(),scheduledAt:null,title:'나에게 보내기 검증',recipientIds:[owner.user.id]});
 assert.ok((await owner.request('/api/messages')).some(m=>m.id===own.id));assert.ok(!(await observer.request('/api/messages')).some(m=>m.id===own.id));
 check('past schedule and unauthorized broadcast rejected; self message is delivered only to its sender');
 const attached=await owner.upload('예약자료.txt','가상 예약 첨부');const packet=make({title:'재시작 예약 점검',attachmentIds:[attached.id]});
 await owner.request('/api/messages','POST',packet,400); // The surrounding expiry test deliberately uses an eight-second file lifetime.
 let fixture=db();const originalExpiry=Date.now()+2*3600000;fixture.prepare('UPDATE Attachments SET ExpiresAt=? WHERE Id=?').run(originalExpiry,attached.id);fixture.close();
 const queued=await owner.request('/api/messages','POST',packet);
 await owner.request('/api/attachments/'+attached.id,'DELETE',undefined,404);
 await owner.request('/api/messages','POST',{...packet,clientId:randomUUID(),scheduledAt:null},400);
 await owner.request('/api/messages','POST',{...packet,clientId:randomUUID()},400);
 await stop();let d=db();d.prepare('UPDATE ScheduledMessages SET DeliverAt=? WHERE Id=?').run(Date.now()-1,queued.id);d.prepare('UPDATE Attachments SET UploadedAt=? WHERE Id=?').run(Date.now()-2*86400000,attached.id);d.close();start({School__RetentionSeconds:'7200'});await ready();
 const delivered=await waitFor(queued.id,'delivered');
 assert.ok((await recipient.request('/api/messages')).some(m=>m.id===delivered.messageId));assert.equal(await recipient.request('/api/attachments/'+attached.id+'/download'),'가상 예약 첨부');
 d=db();assert.equal(d.prepare('SELECT COUNT(*) n FROM Messages WHERE SenderId=? AND ClientId=?').get(owner.user.id,packet.clientId).n,1);assert.equal(d.prepare('SELECT COUNT(*) n FROM ScheduledAttachments WHERE ScheduleId=?').get(queued.id).n,0);assert.equal(d.prepare('SELECT ExpiresAt FROM Attachments WHERE Id=?').get(attached.id).ExpiresAt,originalExpiry);d.close();
 assert.equal((await owner.request('/api/messages','POST',packet)).id,queued.id);
 check('reserved attachment cannot be deleted, reused or orphan-cleaned; server restart delivers once without sender PC, preserving original expiry');
 const roleQueue=await owner.request('/api/messages','POST',make({title:'권한 변경 예약'}));d=db();d.prepare('UPDATE Users SET Active=0 WHERE Id=?').run(owner.user.id);d.close();force(roleQueue.id);
 await pause(1800);d=db();assert.equal(d.prepare('SELECT State FROM ScheduledMessages WHERE Id=?').get(roleQueue.id).State,'failed');d.prepare('UPDATE Users SET Active=1 WHERE Id=?').run(owner.user.id);d.close();
 await owner.login('work.owner');
 check('sender deactivation stops queued delivery instead of preserving former authority');
 const initialQueue=await owner.request('/api/messages','POST',make({title:'초기 비밀번호 계정 예약'}));
 d=db();d.prepare('INSERT OR IGNORE INTO StaffProfiles(UserId,NeedsActivation,ExpiresAt) VALUES(?,0,0)').run(owner.user.id);d.prepare('UPDATE StaffProfiles SET SharedPassword=1 WHERE UserId=?').run(owner.user.id);d.close();force(initialQueue.id);
 let blockedState;for(let n=0;n<150;n++){d=db();blockedState=d.prepare('SELECT State FROM ScheduledMessages WHERE Id=?').get(initialQueue.id).State;d.close();if(blockedState==='failed')break;await pause(100);}assert.equal(blockedState,'failed');
 d=db();d.prepare('UPDATE StaffProfiles SET SharedPassword=0 WHERE UserId=?').run(owner.user.id);d.close();
 check('background scheduled delivery also rejects an account still using the initial password');
 const time=Date.now()+1600,short=make({title:'예약 응답 재시도',scheduledAt:time});const shortQueue=await owner.request('/api/messages','POST',short);await waitFor(shortQueue.id,'delivered');assert.equal((await owner.request('/api/messages','POST',short)).id,shortQueue.id);
 check('retry after scheduled time and actual delivery still returns original reservation instead of sending twice');
 const collect=make({title:'예약 제출 요청',submissionDeadline:Date.now()+86400000});const collection=await owner.request('/api/messages','POST',collect);
 assert.ok(!(await owner.request('/api/submission-requests')).some(r=>r.title===collect.title));force(collection.id);const sentCollection=await waitFor(collection.id,'delivered');
 assert.equal((await owner.request('/api/submission-requests/'+sentCollection.messageId)).targets.length,1);
 check('scheduled file collection creates its submission request only when the message is actually delivered');
 await stop();start();await ready();
}
