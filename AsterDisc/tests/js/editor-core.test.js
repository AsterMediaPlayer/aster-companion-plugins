// The editor page on top of its edit core: how the script modules are loaded (and what happens if they cannot be), every programmatic edit
// being one undoable step, the revision bump touching only the real property, and nothing leaking between files.
// Runs the real browser code in a simulated browser (jsdom) with scripted server responses.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const html=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Configuration/editorPage.html'),'utf8');
const script=html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];
const mod=n=>fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/editor/'+n+'.js'),'utf8');
let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e===undefined?'':e); };
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const ITEM='4cf4efb9cb2c96b4ec157568bcccdf7f';
const MENU=(rev,notes)=>`{
  "schemaVersion": 1,
  "menuId": "3f2b8c1e-6d4a-4e2b-9a71-0c5d2e8f1a44",
  "revision": ${rev},
  "meta": { "notes": ${JSON.stringify(notes||'plain')} },
  "match": { "itemType": "Movie", "providerIds": {"Tmdb": "603"} },
  "root": "main",
  "menus": {
    "main": { "title": "Main", "entries": [ { "action": "playFeature", "label": "Play" }, { "action": "submenu", "label": "More", "menu": "more" } ] },
    "more": { "title": "More", "entries": [ { "action": "back", "label": "Back" } ] }
  }
}
`;
const DOC={Root:'main',Menus:{main:{Title:'Main'},more:{Title:'More'}}};
const LIST={Status:'Ok',Images:[{Id:'11',Category:'moviebackground',Lang:'en',Likes:3},{Id:'12',Category:'moviebackground',Lang:'en',Likes:2},{Id:'13',Category:'moviebackground',Lang:'en',Likes:1}]};

function boot(server,opts={}){
  const dom=new JSDOM(html,{url:'https://s.example/web/index.html',runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window; w.console.warn=()=>{};
  w.__discMenusEditorDelay=20; w.__log=[]; w.confirm=()=>true;
  w.ApiClient={getUrl:(p,q)=>'https://s.example/'+p+(q&&Object.keys(q).length?'?'+Object.entries(q).map(([k,v])=>k+'='+encodeURIComponent(v)).join('&'):''),
    ajax:(req)=>{ const u=new URL(req.url); const e={method:req.type,path:u.pathname.replace(/^\//,''),query:Object.fromEntries(u.searchParams),body:req.data}; w.__log.push(e);
      return Promise.resolve(server(e,w)).then(r=>{ if(r.status>=400) return Promise.reject({status:r.status,json:()=>Promise.resolve(r.body)}); return r.body; }); }};
  w.__preview={calls:[],show(){},update(){},goTo(){},setMuted(){}};
  w.document.getElementById('discEdFrame').contentWindow.DiscMenusPreview=w.__preview;
  if(opts.preload!==false){ w.__discMenusEditorModulesPreloaded=true; require('./editor-modules.js').forEach(n=>w.eval(mod(n))); }
  w.eval(script); return w;
}
const ok=(body)=>({status:200,body}); const err=(status,body)=>({status,body});
function server(files,over={}){
  const versions={}; const saved={};
  return (e,w)=>{ const k=e.method+' '+e.path; if(over[k]) return over[k](e,w);
    if(e.path==='DiscMenus/Editor/Files') return ok(Object.keys(files).map(n=>({File:n,Version:'v-'+n,State:'bound',Title:n})));
    if(e.path==='DiscMenus/Editor/File'&&e.method==='GET') return files[e.query.name]!==undefined?ok({File:e.query.name,Json:files[e.query.name],Version:'v-'+e.query.name}):err(404,{Error:'no'});
    if(e.path==='DiscMenus/Editor/Preview') return ok({Document:DOC,ParentItemId:ITEM,Bound:true,ItemName:'The Matrix'});
    if(e.path==='DiscMenus/Editor/File'&&e.method==='PUT'){ saved[e.query.name]=e.body; return ok({Version:'v2'}); }
    if(e.path.startsWith('DiscMenus/Fanart/List/')) return ok(LIST);
    return err(500,null); };
}
const $=(w,id)=>w.document.getElementById(id);
const page=w=>w.document.getElementById('DiscMenusEditorPage');
const start=async(w)=>{ page(w).dispatchEvent(new w.CustomEvent('pageshow')); await sleep(100); const fr=$(w,'discEdFrame'); fr.contentWindow.DiscMenusPreview=w.__preview; fr.dispatchEvent(new w.Event('load')); await sleep(60); };
const calls=(w,p,m)=>w.__log.filter(c=>c.path.startsWith(p)&&(!m||c.method===m));
const cells=w=>[...w.document.querySelectorAll('.discEdFanartCell')];
const type=(w,text)=>{ const t=$(w,'discEdText'); t.value=text; t.dispatchEvent(new w.Event('input')); };
const pick=async(w,i)=>{ cells(w)[i].click(); await sleep(60); };

(async()=>{
  console.log('--- loading the script modules');
  let w=boot(server({'m.menu.json':MENU(3)}),{preload:false}); page(w).dispatchEvent(new w.CustomEvent('pageshow')); await sleep(40);
  const tags=[...w.document.querySelectorAll('#DiscMenusEditorPage script[src]')];
  check(tags.length===require('./editor-modules.js').length&&require('./editor-modules.js').every((n,i)=>tags[i].src.startsWith('https://s.example/DiscMenus/web/editor/'+n+'.js?t=')),'the page adds a script tag per module, inside its own element, pointing at the plugin route', tags.map(t=>t.src).join());
  check(calls(w,'DiscMenus/Editor/Files').length===0,'nothing else is asked of the server until the modules are there');
  require('./editor-modules.js').forEach(n=>w.eval(mod(n))); tags.forEach(t=>t.dispatchEvent(new w.Event('load'))); await sleep(120);
  check(calls(w,'DiscMenus/Editor/Files').length===1&&$(w,'discEdText').value===MENU(3),'once they load, the editor opens the first file as usual');
  w=boot(server({'m.menu.json':MENU(3)}),{preload:false}); page(w).dispatchEvent(new w.CustomEvent('pageshow')); await sleep(40);
  [...w.document.querySelectorAll('#DiscMenusEditorPage script[src]')][1].dispatchEvent(new w.Event('error')); await sleep(60);
  check(/could not load its "text-adapter" module/.test($(w,'discEdStatus').textContent)&&$(w,'discEdStatus').className.includes('error')&&calls(w,'DiscMenus/Editor/Files').length===0,'if a module cannot be loaded the status says which, and the editor does not half-start', $(w,'discEdStatus').textContent);
  w=boot(server({'m.menu.json':MENU(3)}),{preload:false}); page(w).dispatchEvent(new w.CustomEvent('pageshow')); await sleep(40);
  [...w.document.querySelectorAll('#DiscMenusEditorPage script[src]')].forEach(t=>t.dispatchEvent(new w.Event('load'))); await sleep(60);
  check(/not usable/.test($(w,'discEdStatus').textContent),'a module that loads but does nothing is reported too', $(w,'discEdStatus').textContent);

  console.log('--- the revision bump');
  const tricky=MENU(3,'was "revision": 1 once');
  w=boot(server({'m.menu.json':tricky})); await start(w);
  type(w,$(w,'discEdText').value.replace('"title": "Main"','"title": "Main!"')); await sleep(40);
  $(w,'discEdSave').click(); await sleep(150);
  let put=calls(w,'DiscMenus/Editor/File','PUT')[0];
  check(!!put&&JSON.parse(put.body).revision===4&&JSON.parse(put.body).meta.notes==='was "revision": 1 once','saving raises the revision property, not text that merely looks like it', put&&put.body.slice(0,200));
  check(put.body===tricky.replace('"title": "Main"','"title": "Main!"').replace('"revision": 3','"revision": 4'),'and changes nothing else in the file');
  w=boot(server({'m.menu.json':MENU(3)})); await start(w);
  type(w,$(w,'discEdText').value.replace('"revision": 3','"revision": 9').replace('"title": "Main"','"title": "M2"')); await sleep(40);
  $(w,'discEdSave').click(); await sleep(150); put=calls(w,'DiscMenus/Editor/File','PUT')[0];
  check(JSON.parse(put.body).revision===9,'an author who raised the revision themselves is left alone');

  console.log('--- Tab and undo');
  w=boot(server({'m.menu.json':MENU(3)})); await start(w);
  const ta=$(w,'discEdText'); ta.focus(); ta.setSelectionRange(10,10); const before=ta.value;
  const ev=new w.KeyboardEvent('keydown',{key:'Tab',bubbles:true,cancelable:true}); ta.dispatchEvent(ev); await sleep(30);
  check(ev.defaultPrevented&&ta.value===before.slice(0,10)+'  '+before.slice(10)&&ta.selectionStart===12,'Tab inserts two spaces and puts the caret after them');
  check($(w,'discEdStatus').textContent==='Unsaved changes','and the editor knows the text changed');
  w=boot(server({'m.menu.json':MENU(3)})); await start(w); $(w,'discEdFanartOpen').click(); await sleep(80);
  const original=$(w,'discEdText').value;
  await pick(w,0); const afterOne=$(w,'discEdText').value;
  $(w,'discEdFanartTarget').value='more'; await pick(w,1); const afterTwo=$(w,'discEdText').value;
  check(JSON.parse(afterTwo).background.fanartId==='11'&&JSON.parse(afterTwo).menus.more.background.fanartId==='12','two picks, two backgrounds');
  $(w,'discEdFanartMsg').querySelector('button').click(); await sleep(60);
  check($(w,'discEdText').value===afterOne,'Undo takes back only the last pick, not both');
  check(/Undone/.test($(w,'discEdFanartMsg').textContent)&&!$(w,'discEdFanartMsg').querySelector('button'),'and says so');
  await pick(w,2); $(w,'discEdFanartMsg').querySelector('button').click(); await sleep(60);
  check($(w,'discEdText').value===afterOne,'picking and undoing again works the same way');

  console.log('--- nothing leaks between files');
  w=boot(server({'a.menu.json':MENU(3),'b.menu.json':MENU(5)})); await start(w); $(w,'discEdFanartOpen').click(); await sleep(80); await pick(w,0);
  const undoLink=$(w,'discEdFanartMsg').querySelector('button');
  $(w,'discEdFile').value='b.menu.json'; $(w,'discEdFile').dispatchEvent(new w.Event('change')); await sleep(120);
  const bText=$(w,'discEdText').value; check(bText===MENU(5),'the other file opened');
  undoLink.click(); await sleep(40);
  check($(w,'discEdText').value===bText,'an Undo link left over from the first file cannot touch the second');

  console.log('--- the picker and repeated keys / bad JSON');
  w=boot(server({'m.menu.json':MENU(3)})); await start(w); $(w,'discEdFanartOpen').click(); await sleep(80);
  const dup=MENU(3).replace('"root": "main",','"root": "main",\n  "root": "more",'); type(w,dup); await sleep(40);
  await pick(w,0);
  check($(w,'discEdText').value===dup&&/appears more than once/.test($(w,'discEdFanartMsg').textContent),'a repeated key is refused with an explanation, and the text is untouched', $(w,'discEdFanartMsg').textContent);
  type(w,'{ "broken": '); await sleep(30); await pick(w,0);
  check($(w,'discEdText').value==='{ "broken": '&&/JSON has an error/.test($(w,'discEdFanartMsg').textContent),'broken JSON is refused too');

  console.log('failures:',fail); process.exit(fail?1:0);
})();
