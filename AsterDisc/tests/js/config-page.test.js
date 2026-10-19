// The dashboard Disc Menus settings page: the fanart.tv key is write-only (never shown again, kept when the field is left empty,
// removed only on request), and the key test reports each outcome in words.
// Runs the real browser code in a simulated browser (jsdom) with scripted server responses.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const html=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Configuration/configPage.html'),'utf8');
const script=html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];
let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e===undefined?'':e); };
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const SECRET='0123456789abcdef0123456789abcdef';

function boot(saved, server, opts={}){
  const dom=new JSDOM(html,{url:'https://s.example/web/index.html',runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window; w.console.warn=()=>{};
  w.__saved=Object.assign({MenusPath:'',FanartApiKey:''},saved); w.__log=[]; w.__confirms=[];
  w.confirm=(m)=>{ w.__confirms.push(m); return opts.confirm!==false; };
  w.Dashboard={showLoadingMsg(){},hideLoadingMsg(){},alert(){},processPluginConfigurationUpdateResult(){}};
  w.ApiClient={
    getUrl:(p,q)=>'https://s.example/'+p,
    getJSON:()=>Promise.resolve([]),
    getPluginConfiguration:()=>Promise.resolve(JSON.parse(JSON.stringify(w.__saved))),
    updatePluginConfiguration:(id,c)=>{ w.__log.push({update:JSON.parse(JSON.stringify(c))}); w.__saved=JSON.parse(JSON.stringify(c)); return Promise.resolve({}); },
    ajax:(req)=>{ const path=new URL(req.url).pathname.replace(/^\//,''); w.__log.push({method:req.type,path}); const r=server(path,req.type,w); return r.status>=400?Promise.reject({status:r.status}):Promise.resolve(r.body); }
  };
  w.eval(script); return w;
}
const page=w=>w.document.getElementById('DiscMenusConfigPage');
const $=(w,id)=>w.document.getElementById(id);
const show=async(w)=>{ page(w).dispatchEvent(new w.CustomEvent('pageshow')); await sleep(60); };
const server=(over={})=>(p,m,w)=>{ if(over[m+' '+p]) return over[m+' '+p](w);
  if(p==='DiscMenus/Fanart/Status') return {status:200,body:{Configured:!!w.__saved.FanartApiKey}};
  if(p==='DiscMenus/Status'||p==='DiscMenus') return {status:200,body:[]};
  return {status:200,body:[]}; };
const text=w=>$(w,'FanartKeyState').textContent;

(async()=>{
  console.log('--- the key is write-only');
  let w=boot({FanartApiKey:SECRET},server()); await show(w);
  check($(w,'FanartApiKey').value==='','a saved key is never put in the field');
  check(!w.document.documentElement.outerHTML.includes(SECRET)&&!w.document.body.textContent.includes(SECRET),'the key appears nowhere in the page');
  check(text(w)==='A key is saved.','the page says a key is saved');
  check($(w,'FanartApiKey').type==='password'&&$(w,'FanartApiKey').getAttribute('autocomplete')==='off','the field is a password field with autocomplete off');
  w=boot({},server()); await show(w); check(text(w)==='No key is saved.','and says when there is none');
  w=boot({},server({'GET DiscMenus/Fanart/Status':()=>({status:500})})); await show(w); check(text(w)==='','a status failure shows nothing rather than guessing');

  console.log('--- saving');
  w=boot({FanartApiKey:SECRET,MenusPath:'/m'},server()); await show(w);
  $(w,'MenusPath').value='/menus2'; $(w,'DiscMenusConfigForm').dispatchEvent(new w.Event('submit',{cancelable:true})); await sleep(60);
  check(w.__log.find(l=>l.update).update.FanartApiKey===SECRET&&w.__saved.MenusPath==='/menus2','leaving the field empty keeps the saved key');
  w=boot({FanartApiKey:SECRET},server()); await show(w);
  $(w,'FanartApiKey').value='  NEWKEY  '; $(w,'DiscMenusConfigForm').dispatchEvent(new w.Event('submit',{cancelable:true})); await sleep(60);
  check(w.__saved.FanartApiKey==='NEWKEY','typing a key replaces it (whitespace trimmed)');
  check($(w,'FanartApiKey').value==='','the field is emptied after saving');
  w=boot({},server()); await show(w);
  $(w,'FanartApiKey').value='first'; $(w,'DiscMenusConfigForm').dispatchEvent(new w.Event('submit',{cancelable:true})); await sleep(60);
  check(w.__saved.FanartApiKey==='first'&&text(w)==='A key is saved.','a first key is saved and the state updates');
  w=boot({FanartApiKey:SECRET},server()); await show(w);
  $(w,'FanartApiKey').value='   '; $(w,'DiscMenusConfigForm').dispatchEvent(new w.Event('submit',{cancelable:true})); await sleep(60);
  check(w.__saved.FanartApiKey===SECRET,'a field of only spaces does not erase the key');

  console.log('--- removing');
  w=boot({FanartApiKey:SECRET},server(),{confirm:false}); await show(w);
  $(w,'FanartClearButton').click(); await sleep(60);
  check(w.__saved.FanartApiKey===SECRET&&w.__confirms.length===1,'declining the confirmation keeps the key');
  w=boot({FanartApiKey:SECRET},server()); await show(w);
  $(w,'FanartClearButton').click(); await sleep(60);
  check(w.__saved.FanartApiKey===''&&text(w)==='No key is saved.'&&/plain dark/.test(w.__confirms[0]),'confirming removes the key, and the confirmation says what happens');

  console.log('--- testing the key');
  const outcomes={Ok:/works/,NoKey:/No key/,BadKey:/rejected/,RateLimited:/slow down/,NotFound:/nothing/,Unavailable:/could not be reached/};
  for(const [status,re] of Object.entries(outcomes)){
    w=boot({FanartApiKey:SECRET},server({'POST DiscMenus/Fanart/Test':()=>({status:200,body:{Status:status}})})); await show(w);
    $(w,'FanartTestButton').click(); await sleep(60);
    check(re.test($(w,'FanartTestResult').textContent),'test result: '+status, $(w,'FanartTestResult').textContent);
  }
  w=boot({FanartApiKey:SECRET},server({'POST DiscMenus/Fanart/Test':()=>({status:200,body:{Status:'<img src=x onerror=alert(1)>'}})})); await show(w);
  $(w,'FanartTestButton').click(); await sleep(60);
  check(/does not understand/.test($(w,'FanartTestResult').textContent)&&!w.document.querySelector('#FanartTestResult img'),'an unknown status is shown as plain text, never markup');
  w=boot({FanartApiKey:SECRET},server({'POST DiscMenus/Fanart/Test':()=>({status:500})})); await show(w);
  $(w,'FanartTestButton').click(); await sleep(60);
  check(/could not be run/.test($(w,'FanartTestResult').textContent),'a failed test call is reported');
  check(w.__log.every(l=>!JSON.stringify(l).includes(SECRET)||l.update),'the key is only ever sent in the settings update, never in a test or status call');

  console.log('failures:',fail); process.exit(fail?1:0);
})();
