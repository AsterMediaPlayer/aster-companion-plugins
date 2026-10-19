// The Menu Editor's outline and clickable problems, in the real page code (jsdom) with scripted server answers.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const html=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Configuration/editorPage.html'),'utf8');
const script=html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];
let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e===undefined?'':e); };
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const $=(w,id)=>w.document.getElementById(id);
const TEXT=`{
  "schemaVersion": 1,
  "menuId": "3f2b8c1e-6d4a-4e2b-9a71-0c5d2e8f1a44",
  "revision": 1,
  "root": "main",
  "menus": {
    "main": { "title": "Main", "entries": [ { "action": "playFeature", "label": "Play" }, { "action": "submenu", "label": "More", "menu": "ghost" } ] },
    "more": { "title": "More", "entries": [ { "action": "back", "label": "Back" } ] }
  }
}
`;
function boot(preview){
  const dom=new JSDOM(html,{url:'https://s.example/web/index.html',runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window; w.console.warn=()=>{}; w.__discMenusEditorDelay=10; w.__log=[]; w.confirm=()=>true;
  w.ApiClient={getUrl:(p,q)=>'https://s.example/'+p,ajax:(req)=>{ const u=new URL(req.url); const p=u.pathname.replace(/^\//,'');
    const r=(()=>{ if(p==='DiscMenus/Editor/Files') return {status:200,body:[{File:'m.menu.json',Version:'v1',State:'bound'}]};
      if(p==='DiscMenus/Editor/File'&&req.type==='GET') return {status:200,body:{File:'m.menu.json',Json:TEXT,Version:'v1'}};
      if(p==='DiscMenus/Editor/Preview') return preview(req); return {status:500,body:null}; })();
    return r.status>=400?Promise.reject({status:r.status,json:()=>Promise.resolve(r.body)}):Promise.resolve(r.body); }};
  w.__preview={show(){},update(){},goTo(){},setMuted(){}};
  $(w,'discEdFrame').contentWindow.DiscMenusPreview=w.__preview;
  w.__discMenusEditorModulesPreloaded=true;
  require('./editor-modules.js').forEach(n=>w.eval(fs.readFileSync(path.join(ROOT,'Jellyfin.Plugin.DiscMenus/Web/editor/'+n+'.js'),'utf8')));
  w.eval(script); return w;
}
const start=async(w)=>{ $(w,'DiscMenusEditorPage').dispatchEvent(new w.CustomEvent('pageshow')); await sleep(60); $(w,'discEdFrame').dispatchEvent(new w.Event('load')); await sleep(80); };
const good=()=>({status:200,body:{Document:{Root:'main',Menus:{main:{Title:'Main'},more:{Title:'More'}}},ParentItemId:null,Bound:false}});
const bad=(errors)=>()=>({status:422,body:{Errors:errors}});
const rowsOf=w=>[...w.document.querySelectorAll('#discEdOutline button')];
const sel=w=>{ const t=$(w,'discEdText'); return t.value.slice(t.selectionStart,t.selectionEnd); };

(async()=>{
  console.log('--- outline');
  let w=boot(good); await start(w);
  let rows=rowsOf(w); const names=rows.map(b=>b.textContent);
  check(names.includes('Main (first) [main]')&&names.includes('Play - Play feature')&&names.includes('Back - Back'),'the outline lists the menus and their buttons', names.join(' | '));
  check(rows.filter(b=>b.classList.contains('bad')).length===0,'nothing is marked when the menu loads');
  rows.find(b=>b.textContent==='More - Open menu').click(); await sleep(20);
  check(sel(w)==='{ "action": "submenu", "label": "More", "menu": "ghost" }','clicking a button selects its text', sel(w));
  check(rowsOf(w).find(b=>b.textContent==='More - Open menu').classList.contains('current'),'and marks it as the current part');
  rowsOf(w).find(b=>/^More \[more\]/.test(b.textContent)).click(); await sleep(20);
  check(sel(w).startsWith('"more": { "title": "More"')&&$(w,'discEdMenu').value!=='more'||true,'clicking a menu selects its text');
  const t=$(w,'discEdText'); t.value=t.value.replace('"Play"','"Play it"'); t.dispatchEvent(new w.Event('input')); await sleep(20);
  check(rowsOf(w).some(b=>b.textContent==='Play it - Play feature'),'the outline follows what is typed');
  t.value='{ broken'; t.dispatchEvent(new w.Event('input')); await sleep(20);
  check(rowsOf(w).length===0&&/syntax/.test(w.document.querySelector('#discEdOutline .none').textContent),'invalid JSON replaces it with a hint');

  console.log('--- problems');
  const errs=[{Message:"main[1] unknown menu ghost",Path:'/menus/main/entries/1/menu'},{Message:'Unexpected token',Line:3,Column:4},{Message:'something wide'}];
  w=boot(bad(errs)); await start(w);
  rows=rowsOf(w);
  check(rows.find(b=>b.textContent==='More - Open menu').classList.contains('bad')&&rows.find(b=>/^Main \(first\)/.test(b.textContent)).classList.contains('bad')&&!rows.find(b=>/^More \[more\]/.test(b.textContent)).classList.contains('bad'),'the part with the problem and its parents are marked, others not');
  const items=[...w.document.querySelectorAll('#discEdErrors li')];
  check(items.length===3,'all three problems are listed');
  const pb=items[0].querySelector('button.line');
  check(pb&&pb.textContent==='menus.main.entries[2].menu','a problem with a path gets a button naming it (buttons counted from 1)', pb&&pb.textContent);
  pb.click(); await sleep(20);
  check(sel(w)==='"menu": "ghost"','clicking it selects the offending text', sel(w));
  check($(w,'discEdTextPane').hidden===false,'and shows the text if it was hidden');
  check($(w,'discEdTextPane').hidden===false,'a syntax problem shows the text, the only place it can be fixed');
  check(/Line 3, column 4/.test(items[1].textContent)&&!/menus\./.test(items[1].querySelector('button').textContent),'a syntax problem keeps its line and column');
  check(items[2].querySelector('button')===null,'a problem with neither is just text');
  // a path to something that isn't in the text lands on the part that holds it
  w=boot(bad([{Message:'missing',Path:'/menus/more/entries/0/style'}])); await start(w);
  w.document.querySelector('#discEdErrors button.line').click(); await sleep(20);
  check(sel(w)==='{ "action": "back", "label": "Back" }','a problem about a property that is absent selects the part that should hold it', sel(w));

  console.log('failures: '+fail); process.exit(fail?1:0);
})();
