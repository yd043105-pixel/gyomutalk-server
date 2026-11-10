import assert from 'node:assert/strict';
import {RemoteChannel} from './features.mjs';

export async function presenceChecks({Client,admin,bob,carol,check,password}) {
 const channels=[];const get=async id=>(await admin.request('/api/users')).find(p=>p.id===id);
 // The transport's DELETE response can precede OnDisconnectedAsync. Observe the
 // public state until cleanup finishes, with a deadline that still fails leaks.
 const disconnected=async()=>{const until=Date.now()+5000;let user;do{user=await get(bob.user.id);if(!user.online&&!user.away)return user;await new Promise(r=>setTimeout(r,50));}while(Date.now()<until);assert.equal(user.online,false);assert.equal(user.away,false);return user;};
 try {
  let user=await get(bob.user.id);assert.equal(user.online,false);assert.equal(user.away,false);
  const one=await new RemoteChannel(bob,'/hub').connect();channels.push(one);
  await one.event('PresenceChanged');
  user=await get(bob.user.id);assert.equal(user.online,true);assert.equal(user.away,false);
  const changed=one.event('PresenceChanged');assert.equal(await one.invoke('SetAway',true),true);await changed;
  user=await get(bob.user.id);assert.equal(user.online,true);assert.equal(user.away,true);
  assert.equal((await get(carol.user.id)).online,false);
  check('Presence distinguishes offline, online and away; authenticated changes affect only the calling account');
  one.events=[];assert.equal(await one.invoke('SetAway',true),true);assert.equal(one.events.filter(e=>e.target==='PresenceChanged').length,0);
  assert.equal(await one.invoke('SetAway',false),true);
  check('Repeated states do not broadcast; rapid changes cannot flood all connected users');
  const two=await new RemoteChannel(bob,'/hub').connect();channels.push(two);
  await two.event('PresenceChanged');
  assert.equal((await get(bob.user.id)).away,false);
  await two.invoke('SetAway',true);assert.equal((await get(bob.user.id)).away,true);
  await two.close();assert.equal((await get(bob.user.id)).away,true);
  await new Promise(r=>setTimeout(r,3100));assert.equal(await one.invoke('SetAway',false),false);
  assert.equal((await get(bob.user.id)).away,false);
  check('Any active connection keeps a user online; all connected devices must be away, and resumed input restores online');
  await one.close();user=await disconnected();
  const third=await new RemoteChannel(bob,'/hub').connect();channels.push(third);await third.event('PresenceChanged');assert.equal((await get(bob.user.id)).away,false);await third.close();await disconnected();
  check('Disconnect removes away state; a new connection does not inherit an old away flag');
  await bob.request('/api/presence/mobile','POST',{},404);
  await bob.request('/api/presence/mobile/leave','POST',{},404);
  assert.equal((await get(bob.user.id)).mobile,false);
  check('Retired phone presence endpoints are absent and PC roster mobile flags stay false');
  // One computer at a time. A saved sign-in on another computer stops working while the account is connected elsewhere.
  const lab=new Client();lab.computer='lab-pc';await lab.login(bob.user.username);await lab.request('/api/users');
  const desk=new Client();desk.computer='desk-pc';await desk.login(bob.user.username);
  const live=await new RemoteChannel(desk,'/hub').connect();channels.push(live);await live.event('PresenceChanged');
  await lab.request('/api/users','GET',undefined,401);
  const other=new Client();other.computer='lab-pc';other.csrf=(await other.request('/api/session')).csrfToken;
  assert.ok((await other.request('/api/login','POST',{username:bob.user.username,password},409)).error.includes('다른 컴퓨터'));
  const same=new Client();same.computer='desk-pc';await same.login(bob.user.username);await same.request('/api/users');
  const server=new Client();await server.login(bob.user.username);await server.request('/api/users');
  await live.close();await disconnected();
  await other.login(bob.user.username);await other.request('/api/users');
  check('A second computer cannot sign in or keep a saved sign-in while the account is connected elsewhere; it can once that computer disconnects');
 } finally {for(const channel of channels.reverse())if(!channel.closed)await channel.close();}
}
