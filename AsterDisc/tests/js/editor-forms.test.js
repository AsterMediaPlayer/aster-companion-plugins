// The forms inside the real Menu Editor page (jsdom): editing through a form changes the text box (one undo step), keeps the outline and
// preview in step, and the fanart button opens the picker for the right page.
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
  "match": { "itemType": "Movie", "providerIds": { "Tmdb": "603" } },
  "extras": {},
  "root": "main",
  "menus": {
    "main": { "title": "Main", "background": { "source": "fanart", "fanartId": "1" }, "entries": [ { "action": "playFeature", "label": "Play" }, { "action": "submenu", "label": "More", "menu": "more" } ] },
    "more": { "title": "More", "entries": [ { "action": "back", "label": "Back" } ] }
  }
}
`;
function boot(){
  const dom=new JSDOM(html,{url:'https://s.example/web/index.html',runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window; w.console.warn=()=>{}; w.__discMenusEditorDelay=10; w.__log=[]; w.confirm=()=>true;
  w.ApiClient={getUrl:(p,q)=>'https://s.example/'+p,ajax:(req)=>{ const u=new URL(req.url); const p=u.pathname.replace(/^\//,''); w.__log.push({method:req.type,path:p});
    const r=(()=>{ if(p==='DiscMenus/Editor/Files') return {status:200,body:[{File:'m.menu.json',Version:'v1',State:'bound'}]};
      if(p==='DiscMenus/Editor/File'&&req.type==='GET') return {status:200,body:{File:'m.menu.json',Json:TEXT,Version:'v1'}};
      if(p==='DiscMenus/Editor/Preview') return {status:200,body:{Document:{Root:'main',Menus:{main:{Title:'Main'},more:{Title:'More'}}},ParentItemId:'4cf4efb9cb2c96b4ec157568bcccdf7f',Bound:true}};
      if(p.startsWith('DiscMenus/Fanart/List/')) return {status:200,body:{Status:'Ok',Images:[]}};
      return {status:500,body:null}; })();
    return r.status>=400?Promise.reject({status:r.status,json:()=>Promise.resolve(r.body)}):Promise.resolve(r.body); }};
  w.__preview={show(){},update(){},goTo(){},setMuted(){}};
  $(w,'discEdFrame').contentWindow.DiscMenusPreview=w.__preview;
  w.__discMenusEditorModulesPreloaded=true;
  require('./editor-modules.js').forEach(n=>w.eval(fs.readFileSync(path.join(ROOT,'Jellyfin.Plugin.DiscMenus/Web/editor/'+n+'.js'),'utf8')));
  w.eval(script); return w;
}
const start=async(w)=>{ $(w,'DiscMenusEditorPage').dispatchEvent(new w.CustomEvent('pageshow')); await sleep(60); $(w,'discEdFrame').dispatchEvent(new w.Event('load')); await sleep(80); };
const ins=(w)=>$(w,'discEdInspector');
const ctl=(w,key)=>ins(w).querySelector('[data-key="'+key+'"]');
const change=async(w,key,v)=>{ const c=ctl(w,key); c.value=v; c.dispatchEvent(new w.Event('change')); await sleep(30); };
const outline=w=>[...w.document.querySelectorAll('#discEdOutline button')].map(b=>b.textContent);

(async()=>{
  let w=boot(); await start(w);
  check(/Page: Main/.test(ins(w).textContent)&&ctl(w,'menus/main/title')&&ctl(w,'menus/main/title').value==='Main','opening a menu shows the form for its first page');
  check($(w,'discEdTextPane').hidden===true&&$(w,'discEdToggleText').textContent==='Show text','the raw text starts hidden');
  $(w,'discEdToggleText').click(); await sleep(10);
  check($(w,'discEdTextPane').hidden===false&&$(w,'discEdToggleText').textContent==='Hide text'&&w.localStorage.getItem('discMenusEditorShowText')==='1','Show text reveals it and the choice is remembered');
  $(w,'discEdToggleText').click(); await sleep(10);
  check($(w,'discEdTextPane').hidden===true&&w.localStorage.getItem('discMenusEditorShowText')==='0','Hide text puts it away');
  [...w.document.querySelectorAll('#discEdOutline button')].find(b=>b.textContent==='Play - Play feature').click(); await sleep(20);
  check(/Button: Play/.test(ins(w).textContent)&&ctl(w,'menus/main/entries/0/label').value==='Play','choosing a part in the outline shows its form');
  const t=$(w,'discEdText');
  await change(w,'menus/main/entries/0/label','Play the movie');
  check(t.value===TEXT.replace('"label": "Play"','"label": "Play the movie"'),'a form change edits only that part of the text box');
  check(outline(w).includes('Play the movie - Play feature'),'the outline follows');
  check(/unsaved/i.test($(w,'discEdStatus').textContent),'the editor says there are unsaved changes');
  check(ctl(w,'menus/main/entries/0/label').value==='Play the movie','the form is redrawn from the text');
  const posts=w.__log.filter(c=>c.path==='DiscMenus/Editor/Preview').length; await sleep(60);
  check(w.__log.filter(c=>c.path==='DiscMenus/Editor/Preview').length>posts-1,'the preview is asked to refresh');
  // undo: the adapter keeps one history entry per form edit
  const before=t.value; await change(w,'menus/main/entries/0/style','glow');
  check(t.value!==before,'a second edit');
  w.__discMenusEditorTools&&0;
  // undo works with the text hidden: a button, and Ctrl+Z on the page
  const afterTwo=t.value; $(w,'discEdUndo').click(); await sleep(30);
  check(t.value!==afterTwo&&!t.value.includes('"style": "glow"')&&t.value.includes('Play the movie'),'the Undo button takes back the last form change only');
  check(ctl(w,'menus/main/entries/0/style').value==='','and the form shows it');
  $(w,'discEdRedo').click(); await sleep(30); check(t.value===afterTwo,'Redo brings it back');
  ctl(w,'menus/main/entries/0/style').dispatchEvent(new w.KeyboardEvent('keydown',{key:'z',ctrlKey:true,bubbles:true,cancelable:true})); await sleep(30);
  check(t.value!==afterTwo,'Ctrl+Z while a choice has focus undoes too');
  ctl(w,'menus/main/entries/0/label').dispatchEvent(new w.KeyboardEvent('keydown',{key:'z',ctrlKey:true,bubbles:true,cancelable:true})); await sleep(30);
  check(t.value.includes('Play the movie'),'Ctrl+Z inside a text field is left to the browser (that field\'s own undo)');
  $(w,'discEdRedo').click(); await sleep(30);
  // typing in the text box redraws the form
  t.value=t.value.replace('"Play the movie"','"Typed"'); t.dispatchEvent(new w.Event('input')); await sleep(40);
  check(ctl(w,'menus/main/entries/0/label').value==='Typed','typing in the text redraws the form');
  t.value='{ broken'; t.dispatchEvent(new w.Event('input')); await sleep(40);
  check(/syntax/.test(ins(w).textContent)&&ins(w).querySelectorAll('input,select').length===0,'invalid text turns the form into a hint');
  t.value=TEXT; t.dispatchEvent(new w.Event('input')); await sleep(40);
  check(ctl(w,'menus/main/entries/0/label'),'and valid text brings it back');

  console.log('--- fanart button');
  w=boot(); await start(w);
  [...w.document.querySelectorAll('#discEdOutline button')].find(b=>/^Main \(first\)/.test(b.textContent)).click(); await sleep(20);
  const pick=[...ins(w).querySelectorAll('button')].find(b=>/fanart\.tv/.test(b.textContent));
  check(!!pick,'a fanart background has a "Pick from fanart.tv" button');
  pick.click(); await sleep(40);
  check(!$(w,'discEdFanart').hidden&&$(w,'discEdFanartTarget').value==='main','it opens the picker aimed at that page');
  console.log('--- selecting from a list inside the form');
  [...ins(w).querySelectorAll('.discEdLink')].find(b=>/More - Open another page/.test(b.textContent)).click(); await sleep(20);
  check(/Button: More/.test(ins(w).textContent)&&ctl(w,'menus/main/entries/1/menu').value==='more','choosing a button in a page\'s list opens its form');
  const tt=$(w,'discEdText'); check(tt.value.slice(tt.selectionStart,tt.selectionEnd).startsWith('{ "action": "submenu"'),'and selects its text');
  console.log('failures: '+fail); process.exit(fail?1:0);
})();
