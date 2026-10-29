'use strict';
window.TimetableUI=(()=>{
 let active,epoch=0;
 const node=(tag,text)=>{const element=document.createElement(tag);if(text!==undefined)element.textContent=text;return element;};
 function reset(){epoch++;active?.close();active?.remove();active=null;}
 async function open(request,teacher){
  reset();const ticket=epoch;const setup=await request('timetable/setup');if(ticket!==epoch)return;
  const dialog=node('dialog');dialog.className='school-timetable';active=dialog;const heading=node('h2',teacher?'시간표 · 내 수업':'우리 학급 시간표');
  const close=node('button','닫기');close.type='button';close.onclick=reset;
  const controls=node('div'),date=node('input');date.type='date';date.value=new Date(Date.now()+9*3600000).toISOString().slice(0,10);date.setAttribute('aria-label','조회 날짜');
  const scope=node('select');scope.setAttribute('aria-label','조회할 시간표');
  if(teacher){const option=node('option','내 수업');option.value='mine';scope.append(option);}
  for(const room of setup.classes){const option=node('option',room.name);option.value=room.id;scope.append(option);}
  const error=node('p');error.setAttribute('role','status');const content=node('div'),notices=node('div');let busy=false;
  const day=node('button','선택한 날짜 보기'),week=node('button','선택한 주 보기');day.type=week.type='button';
  controls.append(date,scope,day,week);dialog.append(close,heading,node('p',teacher?'교사 계정에 연결된 수업 또는 학급을 조회하세요.':'학생별 수강 목록 등록 전에는 학급 시간표를 보여줍니다.'),controls,error,notices,content);document.body.append(dialog);dialog.addEventListener('close',()=>{dialog.replaceChildren();dialog.remove();if(active===dialog)active=null;});dialog.showModal();
  async function show(weekly=false){
   if(busy||ticket!==epoch)return false;busy=true;day.disabled=week.disabled=true;error.textContent='';content.replaceChildren();
   try{
    if(!date.value||!scope.value)throw Error('날짜와 학급을 선택하세요.');const first=new Date(date.value+'T00:00:00Z');if(weekly)first.setUTCDate(first.getUTCDate()-(first.getUTCDay()+6)%7);let revision;const days=[];
    for(let i=0;i<(weekly?7:1);i++){const current=new Date(first);current.setUTCDate(current.getUTCDate()+i);const value=await request('timetable/day?date='+current.toISOString().slice(0,10)+(scope.value==='mine'?'&mine=true':'&classId='+encodeURIComponent(scope.value)));if(ticket!==epoch)return false;if(revision!==undefined&&revision!==value.revision)throw Error('조회 중 시간표가 변경되었습니다. 다시 조회하세요.');revision=value.revision;days.push(value);}
    for(const value of days){content.append(node('h3',value.date));if(value.publishedAt)content.append(node('small','최종 게시 '+new Date(value.publishedAt).toLocaleString('ko-KR',{timeZone:'Asia/Seoul'})+' · 버전 '+value.revision));if(value.missingClasses.length)content.append(node('p','아직 시간표가 등록되지 않은 학급이 있습니다.'));if(!value.slots.length)content.append(node('p','등록된 수업이 없습니다.'));
     for(const slot of value.slots){const row=node('p',slot.period+'교시 · '+slot.className+' · '+(slot.subject?slot.subject+' · '+slot.teacherName:'수업 없음'));if(slot.changed){row.className='timetable-changed';row.append(node('small','변경 전: '+(slot.beforeSubject?slot.beforeSubject+' · '+slot.beforeTeacher:'수업 없음')));}content.append(row);}
     if(value.slots.length)content.append(node('small',value.slots.some(s=>s.source==='daily')?'일자별 시간표 적용':'기초시간표 적용'));
    }return true;
   }catch(e){content.replaceChildren();if(ticket===epoch)error.textContent=e.message;return false;}
   finally{busy=false;day.disabled=week.disabled=false;}
  }
  day.onclick=()=>show();week.onclick=()=>show(true);
  try{for(const notice of await request('timetable/notices'))if(!notice.readAt){const button=node('button','● '+notice.title+' · '+notice.date);button.type='button';button.onclick=async()=>{if(busy)return;date.value=notice.date;if([...scope.options].some(o=>o.value===notice.classIds[0]))scope.value=notice.classIds[0];if(await show())try{await request('timetable/notices/'+notice.revision+'/read','POST');if(ticket===epoch)button.remove();}catch(e){error.textContent=e.message;}};notices.append(button);}}catch(e){if(ticket===epoch)error.textContent=e.message;}
  if(ticket===epoch)await show();
 }
 return{open,reset};
})();
