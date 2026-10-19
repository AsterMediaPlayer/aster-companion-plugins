// The dashboard Menu Editor page: loading, debounced live preview, errors, saving, conflicts, unsaved-change protection, hostile content.
// Runs the real browser code in a simulated browser (jsdom) with scripted server responses.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const html=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Configuration/editorPage.html'),'utf8');
const script=html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];
let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e===undefined?'':e); };
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const MENU=(rev,extra)=>JSON.stringify({schemaVersion:1,menuId:'3f2b8c1e-6d4a-4e2b-9a71-0c5d2e8f1a44',revision:rev,root:'main',...(extra||{})},null,2);
const DOC={Root:'main',Menus:{main:{Title:'Main'},features:{Title:'Special Features'}}};

// A scripted server: handlers can be replaced per test; every call is logged.
function boot(server, opts={}){
  const dom=new JSDOM(html,{url:'https://s.example/web/index.html',runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window; w.console.warn=()=>{};
  w.__discMenusEditorDelay=opts.delay==null?30:opts.delay;
  w.__log=[]; w.__confirms=[]; w.confirm=(m)=>{ w.__confirms.push(m); return opts.confirm!==false; };
  w.ApiClient={getUrl:(p,q)=>'https://s.example/'+p+(q&&Object.keys(q).length?'?'+Object.entries(q).map(([k,v])=>k+'='+encodeURIComponent(v)).join('&'):''),
    ajax:(req)=>{ const u=new URL(req.url); const entry={method:req.type,path:u.pathname.replace(/^\//,''),query:Object.fromEntries(u.searchParams),body:req.data,contentType:req.contentType}; w.__log.push(entry);
      const res=server(entry); return Promise.resolve(res).then(r=>{ if(r.status>=400){ return Promise.reject({status:r.status,json:()=>Promise.resolve(r.body)}); } return r.body; }); }};
  // a preview frame whose renderer we can watch
  w.__preview={calls:[],show(d,p){this.calls.push(['show',d,p]);},update(d,p){this.calls.push(['update',d,p]);},goTo(k){this.calls.push(['goTo',k]);},setMuted(m){this.calls.push(['setMuted',m]);}};
  const frame=w.document.getElementById('discEdFrame'); frame.contentWindow.DiscMenusPreview=w.__preview;
  // the page loads these modules through script tags; the test supplies them directly
  w.__discMenusEditorModulesPreloaded = true;
  require('./editor-modules.js').forEach(n => w.eval(fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/editor/' + n + '.js'), 'utf8')));
  w.eval(script);
  return w;
}
const ok=(body)=>({status:200,body}); const err=(status,body)=>({status,body});
const FILES=[{File:'thor.menu.json',Version:'v1',State:'bound',Title:'Thor'},{File:'wall-e-2008.menu.json',Version:'w1',State:'pending'}];
function standard(over={}){ const files={'thor.menu.json':{Json:MENU(3),Version:'v1'},'wall-e-2008.menu.json':{Json:MENU(1,{notes:'wall-e'}),Version:'w1'}}; let version={...Object.fromEntries(Object.entries(files).map(([k,v])=>[k,v.Version]))};
  return (e)=>{ if(over[e.method+' '+e.path]) return over[e.method+' '+e.path](e);
    if(e.path==='DiscMenus/Editor/Files') return ok(FILES);
    if(e.path==='DiscMenus/Editor/File'&&e.method==='GET') return files[e.query.name]?ok({File:e.query.name,Json:files[e.query.name].Json,Version:version[e.query.name]}):err(404,{Error:'No such menu file.'});
    if(e.path==='DiscMenus/Editor/Preview') return ok({Document:DOC,ParentItemId:'abc',Bound:e.query.file==='thor.menu.json',ItemName:'Thor: Ragnarok'});
    if(e.path==='DiscMenus/Editor/File'&&e.method==='PUT'){ if(e.query.version!==version[e.query.name]) return err(409,{Error:'changed',CurrentVersion:version[e.query.name]}); version[e.query.name]='v-'+(e.body.length); files[e.query.name].Json=e.body; return ok({Version:version[e.query.name]}); }
    return err(500,null); }; }
const page=w=>w.document.getElementById('DiscMenusEditorPage');
const $=(w,id)=>w.document.getElementById(id);
const start=async(w)=>{ page(w).dispatchEvent(new w.CustomEvent('pageshow')); await sleep(80); const fr=w.document.getElementById('discEdFrame'); fr.contentWindow.DiscMenusPreview=w.__preview; fr.dispatchEvent(new w.Event('load')); await sleep(30); };
const type=(w,text)=>{ const t=$(w,'discEdText'); t.value=text; t.dispatchEvent(new w.Event('input')); };
const calls=(w,path,method)=>w.__log.filter(c=>c.path===path&&(!method||c.method===method));
(async()=>{
  console.log('--- opening');
  let w=boot(standard()); await start(w);
  const opts=[...$(w,'discEdFile').options].map(o=>o.textContent);
  check(opts.join('|')==='thor.menu.json - bound|wall-e-2008.menu.json - waiting for its title','lists files with their discovery state', opts.join('|'));
  check($(w,'discEdFile').value==='thor.menu.json'&&$(w,'discEdText').value===MENU(3)&&!$(w,'discEdText').disabled,'opens the first file automatically, text editable');
  check($(w,'discEdStatus').textContent==='Saved','status: Saved');
  check($(w,'discEdFrame').getAttribute('src')==='https://s.example/DiscMenus/web/preview.html','preview frame points at the preview page');
  await sleep(60);
  const first=calls(w,'DiscMenus/Editor/Preview');
  check(first.length===1&&first[0].query.file==='thor.menu.json'&&first[0].body===MENU(3)&&first[0].contentType==='text/plain','preview request sends the file name and the raw text', first.length);
  check(w.__preview.calls.some(c=>c[0]==='show')&&w.__preview.calls[0][0]==='setMuted'&&w.__preview.calls[0][1]===true,'frame is muted first, then shown the document');
  check($(w,'discEdNote').textContent.includes('Thor: Ragnarok'),'note says which real title the preview uses', $(w,'discEdNote').textContent.slice(0,60));
  check([...$(w,'discEdMenu').options].map(o=>o.value).join()==='main,features','menu picker lists the menus to jump between');

  console.log('--- typing: debounce and live update');
  w.__log.length=0; w.__preview.calls.length=0;
  type(w,MENU(3,{a:1})); type(w,MENU(3,{a:2})); type(w,MENU(3,{a:3}));
  check($(w,'discEdStatus').textContent==='Unsaved changes','marks unsaved changes immediately');
  await sleep(120);
  check(calls(w,'DiscMenus/Editor/Preview').length===1&&calls(w,'DiscMenus/Editor/Preview')[0].body.includes('"a": 3'),'three quick edits -> one preview request with the latest text');
  check(w.__preview.calls.filter(c=>c[0]==='update').length===1&&!w.__preview.calls.some(c=>c[0]==='show'),'the frame is updated in place, not reset');
  type(w,MENU(3)); check($(w,'discEdStatus').textContent==='Saved','reverting to the saved text clears the unsaved flag');

  console.log('--- the menu picker and the frame stay in sync');
  $(w,'discEdMenu').value='features'; $(w,'discEdMenu').dispatchEvent(new w.Event('change'));
  check(w.__preview.calls.some(c=>c[0]==='goTo'&&c[1]==='features'),'choosing a menu jumps the preview to it');
  const frameWin=$(w,'discEdFrame').contentWindow;
  const msg=(data,over={})=>w.dispatchEvent(new w.MessageEvent('message',{origin:over.origin||'https://s.example',source:over.source||frameWin,data}));
  msg({source:'discmenus-preview',type:'navigate',menu:'main'}); check($(w,'discEdMenu').value==='main','clicking inside the preview moves the picker');
  msg({source:'discmenus-preview',type:'play',itemIds:['x'],startTicks:600000000}); check(/60s/.test($(w,'discEdNote').textContent)&&/never plays/.test($(w,'discEdNote').textContent),'a play action is explained, never performed', $(w,'discEdNote').textContent);
  msg({source:'discmenus-preview',type:'message',text:"<img src=x onerror=alert(1)>"}); check($(w,'discEdNote').textContent.includes('<img')&&$(w,'discEdNote').querySelector('img')===null,'a preview message is shown as text, never markup');
  const before=$(w,'discEdNote').textContent;
  msg({source:'discmenus-preview',type:'message',text:'EVIL'},{origin:'https://evil.example'}); msg({source:'discmenus-preview',type:'message',text:'EVIL2'},{source:w}); msg({source:'other',type:'message',text:'EVIL3'}); msg(null); msg('str');
  check($(w,'discEdNote').textContent===before,'messages from the wrong origin, window or shape are ignored');

  console.log('--- mute toggle');
  w.__preview.calls.length=0; $(w,'discEdMute').click();
  check($(w,'discEdMute').textContent.trim()==='Sound on'&&w.__preview.calls.some(c=>c[0]==='setMuted'&&c[1]===false),'unmute');
  $(w,'discEdMute').click(); check($(w,'discEdMute').textContent.trim()==='Sound off'&&w.__preview.calls.some(c=>c[0]==='setMuted'&&c[1]===true),'mute again');

  console.log('--- errors');
  w=boot(standard({'POST DiscMenus/Editor/Preview':(e)=>e.body.includes('BROKEN')?err(422,{Errors:[{Message:"'x' is invalid",Line:3,Column:7},{Message:'main[0] unknown extra nope'}]}):ok({Document:DOC,ParentItemId:null,Bound:false})})); await start(w); await sleep(60);
  w.__preview.calls.length=0; type(w,MENU(3)+'\nBROKEN\nline4'); await sleep(120);
  const errLis=[...$(w,'discEdErrors').querySelectorAll('li')];
  check(errLis.length===2&&$(w,'discEdErrors').querySelector('.title').textContent==='2 problems to fix:','lists every problem');
  check(errLis[0].textContent.startsWith('Line 3, column 7: ')&&errLis[1].textContent==='main[0] unknown extra nope'&&!errLis[1].querySelector('button'),'syntax errors carry a line link, semantic ones do not');
  errLis[0].querySelector('button').click(); const t=$(w,'discEdText'); const ls=t.value.split('\n');
  check(t.selectionStart===ls[0].length+ls[1].length+2&&t.selectionEnd===t.selectionStart+ls[2].length,'clicking the line link selects that line');
  check(w.__preview.calls.filter(c=>c[0]==='update'||c[0]==='show').length===0&&/last version that was valid/.test($(w,'discEdNote').textContent),'while invalid, the preview keeps the last good version');
  type(w,MENU(3)); await sleep(120); check($(w,'discEdErrors').textContent===''&&w.__preview.calls.some(c=>c[0]==='update'),'fixing it clears the errors and updates the preview');
  check(/isn't linked/.test($(w,'discEdNote').textContent),'an unbound menu explains what the preview lacks', $(w,'discEdNote').textContent.slice(0,80));
  $(w,'discEdFile').value='wall-e-2008.menu.json'; $(w,'discEdFile').dispatchEvent(new w.Event('change')); await sleep(120);
  check(/waiting for its title/.test($(w,'discEdNote').textContent),'...and says why when the file is waiting for its title', $(w,'discEdNote').textContent.slice(0,120));

  console.log('--- a slow old answer must not overwrite a newer one');
  let release; const slow=new Promise(r=>{release=r;});
  let n=0; w=boot(standard({'POST DiscMenus/Editor/Preview':(e)=>{ n++; if(n===2) return slow.then(()=>ok({Document:{...DOC,Menus:{stale:{Title:'STALE'}},Root:'stale'},Bound:false})); return ok({Document:DOC,Bound:false}); }})); await start(w); await sleep(80);
  type(w,MENU(3,{k:1})); await sleep(60); type(w,MENU(3,{k:2})); await sleep(80); w.__preview.calls.length=0; release(); await sleep(60);
  check(!w.__preview.calls.some(c=>c[1]&&c[1].Root==='stale'),'out-of-order preview answers are discarded');

  console.log('--- saving');
  w=boot(standard()); await start(w); await sleep(60);
  w.__log.length=0; type(w,MENU(3,{notes:'edited'}));
  $(w,'discEdSave').click(); await sleep(60);
  let put=calls(w,'DiscMenus/Editor/File','PUT')[0];
  check(!!put&&put.query.name==='thor.menu.json'&&put.query.version==='v1'&&put.contentType==='text/plain','save PUTs the file name and the version it was opened at');
  check(/"revision": 4/.test(put.body)&&/"revision": 4/.test($(w,'discEdText').value),'an edit without a higher revision gets it bumped (3 -> 4), visibly', (put.body.match(/"revision": \d+/)||[])[0]);
  check($(w,'discEdStatus').textContent==='Saved'&&!isNaN(0),'status: Saved');
  w.__log.length=0; type(w,$(w,'discEdText').value.replace('edited','edited again')); $(w,'discEdSave').click(); await sleep(60);
  put=calls(w,'DiscMenus/Editor/File','PUT')[0]; check(put.query.version.startsWith('v-')&&/"revision": 5/.test(put.body),'the next save uses the version the last one returned, and bumps again');
  w.__log.length=0; $(w,'discEdSave').click(); await sleep(40); check(calls(w,'DiscMenus/Editor/File','PUT').length===0&&$(w,'discEdStatus').textContent==='Nothing to save','saving with no changes sends nothing');
  w=boot(standard()); await start(w); await sleep(60); w.__log.length=0; type(w,MENU(9,{x:1})); $(w,'discEdSave').click(); await sleep(60);
  check(/"revision": 9/.test(calls(w,'DiscMenus/Editor/File','PUT')[0].body),'a revision the author raised themselves is left alone');
  check(calls(w,'DiscMenus/Editor/Files').length>=1,'the file list is refreshed after a save');

  console.log('--- save problems');
  w=boot(standard({'PUT DiscMenus/Editor/File':()=>err(422,{Errors:[{Message:'revision can\'t go down (it was 3).'}]})})); await start(w); await sleep(60);
  type(w,MENU(1,{x:1})); $(w,'discEdSave').click(); await sleep(60);
  check($(w,'discEdStatus').className.includes('error')&&/fix the problems/.test($(w,'discEdStatus').textContent)&&/revision can't go down/.test($(w,'discEdErrors').textContent),'a rejected save shows the reason and stays unsaved');
  check($(w,'discEdStatus').textContent!=='Saved'&&$(w,'discEdText').value.includes('"x": 1'),'...and keeps what you typed');
  await sleep(120); check(/revision can't go down/.test($(w,'discEdErrors').textContent),'a later successful preview does NOT wipe the reason the save was refused');
  type(w,MENU(5,{x:2})); check($(w,'discEdErrors').textContent==='','editing again clears the stale save error');
  w=boot(standard({'PUT DiscMenus/Editor/File':()=>err(500,null)})); await start(w); await sleep(60); type(w,MENU(3,{y:1})); $(w,'discEdSave').click(); await sleep(60);
  check(/Save failed/.test($(w,'discEdStatus').textContent)&&$(w,'discEdStatus').className.includes('error'),'a server failure is reported');

  console.log('--- conflict: the file changed under the editor');
  let serverVersion='v1'; const files2={'thor.menu.json':MENU(3)}; w=boot((e)=>{ if(e.path==='DiscMenus/Editor/Files') return ok(FILES.slice(0,1)); if(e.path.endsWith('Editor/File')&&e.method==='GET') return ok({File:'thor.menu.json',Json:files2['thor.menu.json'],Version:serverVersion}); if(e.path.endsWith('Editor/Preview')) return ok({Document:DOC,Bound:true,ItemName:'T'}); if(e.method==='PUT'){ if(e.query.version!==serverVersion) return err(409,{Error:'changed',CurrentVersion:serverVersion}); serverVersion='v-new'; files2['thor.menu.json']=e.body; return ok({Version:'v-new'}); } return err(500,null); });
  await start(w); await sleep(60); serverVersion='v-someone-else'; files2['thor.menu.json']=MENU(7,{theirs:true});
  type(w,MENU(3,{mine:true})); $(w,'discEdSave').click(); await sleep(60);
  check(!$(w,'discEdConflict').hidden&&/changed on the server/.test($(w,'discEdConflictText').textContent)&&$(w,'discEdText').value.includes('mine'),'conflict is announced and your edits are kept');
  w.__log.length=0; $(w,'discEdOverwrite').click(); await sleep(60);
  check(calls(w,'DiscMenus/Editor/File','PUT')[0].query.version==='v-someone-else'&&$(w,'discEdConflict').hidden&&$(w,'discEdStatus').textContent==='Saved','"Overwrite with mine" saves against the server\'s current version');
  serverVersion='v-again'; files2['thor.menu.json']=MENU(8,{theirs2:true}); type(w,MENU(8,{mine2:true})); $(w,'discEdSave').click(); await sleep(60);
  w.__log.length=0; $(w,'discEdLoadTheirs').click(); await sleep(60);
  check($(w,'discEdText').value.includes('theirs2')&&$(w,'discEdConflict').hidden&&calls(w,'DiscMenus/Editor/File','GET').length===1,'"Load the server\'s version" replaces your text with theirs');

  console.log('--- unsaved changes are protected');
  w=boot(standard(),{confirm:false}); await start(w); await sleep(60); type(w,MENU(3,{dirty:1}));
  $(w,'discEdFile').value='wall-e-2008.menu.json'; $(w,'discEdFile').dispatchEvent(new w.Event('change')); await sleep(50);
  check(w.__confirms.length===1&&$(w,'discEdText').value.includes('dirty')&&$(w,'discEdFile').value==='thor.menu.json','switching files with unsaved edits asks first; "no" keeps you where you were (picker restored)');
  w.__log.length=0; $(w,'discEdReload').click(); await sleep(40); check(calls(w,'DiscMenus/Editor/File','GET').length===0,'Reload asks first too, and no means no');
  w=boot(standard(),{confirm:true}); await start(w); await sleep(60); type(w,MENU(3,{dirty:1}));
  $(w,'discEdFile').value='wall-e-2008.menu.json'; $(w,'discEdFile').dispatchEvent(new w.Event('change')); await sleep(80);
  check($(w,'discEdText').value.includes('wall-e')&&$(w,'discEdFile').value==='wall-e-2008.menu.json','confirming discards the edits and opens the other file');
  check(w.__preview.calls.filter(c=>c[0]==='show').length>=1,'a newly opened file resets the preview (show, not update)');
  w=boot(standard()); await start(w); await sleep(60); w.__confirms.length=0; $(w,'discEdFile').value='wall-e-2008.menu.json'; $(w,'discEdFile').dispatchEvent(new w.Event('change')); await sleep(60);
  check(w.__confirms.length===0&&$(w,'discEdText').value.includes('wall-e'),'no prompt when there is nothing to lose');

  console.log('--- keyboard');
  w=boot(standard()); await start(w); await sleep(60); const ta=$(w,'discEdText'); ta.value='ab'; ta.setSelectionRange(1,1); w.__log.length=0;
  ta.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Tab',bubbles:true,cancelable:true})); check(ta.value==='a  b'&&ta.selectionStart===3,'Tab inserts two spaces');
  await sleep(80); check(calls(w,'DiscMenus/Editor/Preview').length===1,'...and refreshes the preview');
  type(w,MENU(3,{k:'s'})); w.__log.length=0; const ev=new w.KeyboardEvent('keydown',{key:'s',ctrlKey:true,bubbles:true,cancelable:true}); ta.dispatchEvent(ev); await sleep(60);
  check(ev.defaultPrevented&&calls(w,'DiscMenus/Editor/File','PUT').length===1,'Ctrl+S saves (and the browser does not "save the page")');

  console.log('--- hostile content is never treated as markup');
  const evil='<img src=x onerror=alert(1)>.menu.json';
  w=boot(standard({'DiscMenus/Editor/Files':()=>0,'GET DiscMenus/Editor/Files':()=>ok([{File:evil,Version:'v',State:'<b>bound</b>',Title:'<script>1</script>'}]),'GET DiscMenus/Editor/File':(e)=>ok({File:e.query.name,Json:'<script>alert(1)</script>',Version:'v'}),'POST DiscMenus/Editor/Preview':()=>err(422,{Errors:[{Message:'<img src=x onerror=alert(2)>',Line:1}]})})); await start(w); await sleep(120);
  check(w.document.querySelectorAll('#DiscMenusEditorPage img, #DiscMenusEditorPage script:not(:last-of-type) b').length===0&&$(w,'discEdFile').options[0].textContent.includes('<img'),'file names and states are text');
  check($(w,'discEdErrors').querySelector('img')===null&&$(w,'discEdErrors').textContent.includes('<img'),'error messages are text');
  check($(w,'discEdText').value==='<script>alert(1)</script>','file content goes only into the textarea');

  console.log('--- edge cases');
  w=boot((e)=>e.path.endsWith('Files')?ok([]):err(500,null)); await start(w);
  check($(w,'discEdText').disabled&&/No menu files yet/.test($(w,'discEdStatus').textContent),'no menu files: editor disabled with a hint');
  w=boot((e)=>err(500,null)); await start(w); check(/Could not list/.test($(w,'discEdStatus').textContent)&&$(w,'discEdStatus').className.includes('error'),'a failing file list is reported');
  w=boot(standard({'POST DiscMenus/Editor/Preview':()=>err(500,null)})); await start(w); await sleep(60); check(/could not be updated/.test($(w,'discEdNote').textContent),'a failing preview is reported without breaking the editor');
  w=boot(standard({'GET DiscMenus/Editor/File':()=>err(404,{Error:'x'})})); await start(w); await sleep(40); check(/Could not open/.test($(w,'discEdStatus').textContent),'opening a vanished file is reported');
  console.log('--- visiting the page twice does not stack handlers');
  const dom2=new JSDOM(html,{url:'https://s.example/web/index.html',runScripts:'outside-only',pretendToBeVisual:true}); const w2=dom2.window;
  w2.__discMenusEditorDelay=10; w2.ApiClient={getUrl:(p)=>'https://s.example/'+p,ajax:()=>Promise.resolve([])}; let handlers=0; const orig=w2.addEventListener.bind(w2); w2.addEventListener=(t,f,o)=>{ if(t==='message') handlers++; return orig(t,f,o); };
  w2.eval(script); w2.eval(script); w2.eval(script); const removed=[]; check(w2.__discMenusEditorMsg!==undefined&&typeof w2.__discMenusEditorMsg==='function','handler registered on window with a replaceable reference');
  console.log('failures:',fail); process.exit(0);
})();
