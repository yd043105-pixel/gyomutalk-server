// A local stand-in for Google Apps Script: runs the CourseScript.gs the school pastes, with Sheets kept in memory.
// POST /exec answers with a redirect to the result like Google does; /page serves the student page with google.script.* wired to /run.
import http from 'node:http';
import vm from 'node:vm';

export async function startScriptHost(port=0){
 const books=new Map(),props=new Map(),results=new Map();
 const sheet=name=>{let rows=[];const api={
  getName:()=>name,getLastRow:()=>rows.length,getLastColumn:()=>rows.reduce((m,r)=>Math.max(m,r.length),0),clearContents(){rows=[];},
  getRange(a,b,c,d){
   if(typeof a==='string'){return{createTextFinder:t=>{const f={matchCase:()=>f,matchEntireCell:()=>f,findNext:()=>{const i=rows.findIndex(r=>r[0]===t);return i<0?null:{getRow:()=>i+1};}};return f;}};}
   return{setNumberFormat(){},getValues:()=>rows.slice(a-1,a-1+c).map(r=>Array.from({length:d},(_,i)=>r[i]??'')),
    setValues(v){v.forEach((r,i)=>{rows[a-1+i]=r.map(String);});}};
  },_rows:()=>rows};return api;};
 const book=id=>{const sheets=new Map();const b={getId:()=>id,getSheetByName:n=>sheets.get(n)??null,insertSheet(n){sheets.set(n,sheet(n));},_sheets:sheets};books.set(id,b);return b;};
 const context=vm.createContext({
  JSON,Date,Math,Array,Object,String,Number,
  HtmlService:{createHtmlOutput:html=>{const o={html,setTitle:()=>o,addMetaTag:()=>o};return o;}},
  ContentService:{MimeType:{JSON:'json'},createTextOutput:text=>({text,setMimeType(){return this;}})},
  PropertiesService:{getScriptProperties:()=>({getProperty:k=>props.get(k)??null,setProperty:(k,v)=>props.set(k,v)})},
  SpreadsheetApp:{create:()=>book('book-'+books.size),openById:id=>{if(!books.has(id))throw Error('gone');return books.get(id);}},
  LockService:{getScriptLock:()=>({waitLock(){},tryLock:()=>true,releaseLock(){}})},
 });
 const read=req=>new Promise(r=>{let s='';req.on('data',b=>s+=b);req.on('end',()=>r(s));});
 let calls=0;
 const server=http.createServer(async(req,res)=>{
  const url=new URL(req.url,'http://x');
  if(req.method==='POST'&&url.pathname==='/exec'){calls++;const out=context.doPost({postData:{contents:await read(req)}});const id=String(results.size);results.set(id,out.text);res.writeHead(302,{Location:'/echo?id='+id});res.end();return;}
  if(req.method==='GET'&&url.pathname==='/echo'){res.writeHead(200,{'Content-Type':'application/json'});res.end(results.get(url.searchParams.get('id')));return;}
  if(req.method==='POST'&&url.pathname==='/run'){const {name,args}=JSON.parse(await read(req));res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify(context[name](...args)??null));return;}
  if(url.pathname==='/page'){
   // google.script.url.getLocation reads this page's own query and fragment, as it does inside Google's frame.
   // Google adds the viewport tag from addMetaTag to its outer page; here it goes straight into the page.
   const shim=`<meta name="viewport" content="width=device-width, initial-scale=1"><script>window.google={script:{url:{getLocation(cb){const q=new URLSearchParams(location.search);cb({parameter:Object.fromEntries(q),hash:location.hash.slice(1)});}},get run(){let ok=()=>{},bad=()=>{};const r=new Proxy({},{get(_,k){if(k==='withSuccessHandler')return f=>{ok=f;return r;};if(k==='withFailureHandler')return f=>{bad=f;return r;};return(...args)=>fetch('/run',{method:'POST',body:JSON.stringify({name:k,args})}).then(x=>x.json()).then(ok,bad);}});return r;}}};</script>`;
   res.writeHead(200,{'Content-Type':'text/html; charset=utf-8'});res.end(context.doGet().html.replace('<head>','<head>'+shim));return;}
  res.writeHead(404);res.end();
 });
 await new Promise(r=>server.listen(port,'127.0.0.1',r));
 const base='http://127.0.0.1:'+server.address().port;
 return{base,url:base+'/exec',context,load:code=>vm.runInContext(code,context),calls:()=>calls,rows:name=>[...books.values()][0]?._sheets.get(name)?._rows()??[],close:()=>new Promise(r=>server.close(r))};
}
