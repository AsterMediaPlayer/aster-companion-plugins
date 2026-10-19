// The Menu Editor's file bar: New (title search), Duplicate, Delete, Backups and Restore. Runs the real page code in jsdom with scripted server responses.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const html=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Configuration/editorPage.html'),'utf8');
const script=html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];
let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e===undefined?'':e); };
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const TEXT=(rev)=>`{\n  "schemaVersion": 1,\n  "menuId": "3f2b8c1e-6d4a-4e2b-9a71-0c5d2e8f1a44",\n  "revision": ${rev||1},\n  "root": "main",\n  "menus": {"main": {"title": "Main", "entries": []}}\n}\n`;
const ok=(body)=>({status:200,body}); const err=(status,body)=>({status,body});
const $=(w,id)=>w.document.getElementById(id);

function boot(server, confirmAnswer){
  const dom=new JSDOM(html,{url:'https://s.example/web/index.html',runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window; w.console.warn=()=>{};
  w.__discMenusEditorDelay=10; w.__log=[]; w.__confirms=[]; w.confirm=(m)=>{ w.__confirms.push(m); return confirmAnswer!==false; };
  w.ApiClient={getUrl:(p,q)=>'https://s.example/'+p+(q&&Object.keys(q).length?'?'+Object.entries(q).map(([k,v])=>k+'='+encodeURIComponent(v)).join('&'):''),
    ajax:(req)=>{ const u=new URL(req.url); const e={method:req.type,path:u.pathname.replace(/^\//,''),query:Object.fromEntries(u.searchParams),body:req.data}; w.__log.push(e);
      return Promise.resolve(server(e,w)).then(r=>{ if(r.status>=400) return Promise.reject({status:r.status,json:()=>Promise.resolve(r.body)}); return r.body; }); }};
  w.__preview={show(){},update(){},goTo(){},setMuted(){}};
  w.document.getElementById('discEdFrame').contentWindow.DiscMenusPreview=w.__preview;
  w.__discMenusEditorModulesPreloaded = true;
  require('./editor-modules.js').forEach(n => w.eval(fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/editor/' + n + '.js'), 'utf8')));
  w.eval(script); return w;
}
const start=async(w)=>{ $(w,'DiscMenusEditorPage').dispatchEvent(new w.CustomEvent('pageshow')); await sleep(60); const fr=$(w,'discEdFrame'); fr.contentWindow.DiscMenusPreview=w.__preview; fr.dispatchEvent(new w.Event('load')); await sleep(60); };
const calls=(w,p,m)=>w.__log.filter(c=>c.path===p&&(!m||c.method===m));
const click=async(w,id)=>{ $(w,id).click(); await sleep(60); };
const files=(w)=>[...$(w,'discEdFile').options].map(o=>o.value);

// a tiny in-memory server
function fake(opts={}){
  const disk=new Map(Object.entries(opts.files||{'a.menu.json':TEXT(1)}));
  let ver=1; const versions=()=>'v'+ver;
  return (e)=>{
    const name=e.query.name;
    if(opts.over&&opts.over[e.method+' '+e.path]) return opts.over[e.method+' '+e.path](e,disk);
    switch(e.method+' '+e.path){
      case 'GET DiscMenus/Editor/Files': return ok([...disk.keys()].map(f=>({File:f,Version:versions(),State:'bound'})));
      case 'GET DiscMenus/Editor/File': return disk.has(name)?ok({File:name,Json:disk.get(name),Version:versions()}):err(404,{Error:'gone'});
      case 'GET DiscMenus/Editor/Preview':
      case 'POST DiscMenus/Editor/Preview': return ok({Document:{Root:'main',Menus:{main:{Title:'Main'}}},ParentItemId:null,Bound:false});
      case 'GET DiscMenus/Editor/Titles': return ok(opts.titles||[]);
      case 'POST DiscMenus/Editor/New': disk.set('new-one.menu.json',TEXT(1)); return ok({File:'new-one.menu.json',Version:versions()});
      case 'POST DiscMenus/Editor/Duplicate': disk.set(name.replace('.menu.json','-copy.menu.json'),TEXT(1)); return ok({File:name.replace('.menu.json','-copy.menu.json'),Version:versions()});
      case 'DELETE DiscMenus/Editor/File': disk.delete(name); return ok({Deleted:true});
      case 'GET DiscMenus/Editor/Backups': return ok(opts.backups||[]);
      case 'POST DiscMenus/Editor/Restore': disk.set(name,TEXT(5)); ver++; return ok({Version:versions()});
    }
    return err(500,null);
  };
}

(async()=>{
  console.log('--- buttons follow the open file');
  let w=boot(fake({files:{}})); await start(w);
  check($(w,'discEdDuplicate').disabled&&$(w,'discEdDelete').disabled&&$(w,'discEdBackups').disabled&&!$(w,'discEdNew').disabled,'with no menus only New is available');
  check(/New/.test($(w,'discEdStatus').textContent),'and it says to use New');
  w=boot(fake()); await start(w);
  check(!$(w,'discEdDuplicate').disabled&&!$(w,'discEdDelete').disabled&&!$(w,'discEdBackups').disabled,'with a menu open all are available');

  console.log('--- New');
  const titles=[{Id:'11111111111111111111111111111111',Name:'The <b>Matrix</b>',Year:1999,Type:'Movie',HasIds:true},{Id:'22222222222222222222222222222222',Name:'Show',Year:2001,Type:'Series',HasIds:true},{Id:'33333333333333333333333333333333',Name:'Idless',Year:2000,Type:'Movie',HasIds:false}];
  w=boot(fake({titles})); await start(w);
  await click(w,'discEdNew'); check(!$(w,'discEdNewPanel').hidden,'New opens its panel');
  $(w,'discEdNewQuery').value='m'; $(w,'discEdNewQuery').dispatchEvent(new w.Event('input')); await sleep(60);
  check(calls(w,'DiscMenus/Editor/Titles').length===0&&/two letters/.test($(w,'discEdNewMsg').textContent),'one letter does not search');
  $(w,'discEdNewQuery').value='matrix'; $(w,'discEdNewQuery').dispatchEvent(new w.Event('input')); await sleep(80);
  check(calls(w,'DiscMenus/Editor/Titles').length===1&&calls(w,'DiscMenus/Editor/Titles')[0].query.q==='matrix','typing searches after a pause');
  const rows=[...w.document.querySelectorAll('#discEdNewList .discEdRow')];
  check(rows.length===3&&!w.document.querySelector('#discEdNewList b'),'results are listed as text, never markup');
  check(rows[0].querySelectorAll('button').length===2&&rows[1].querySelectorAll('button').length===1,'movies offer empty and starter; series only empty');
  check([...rows[2].querySelectorAll('button')].every(b=>b.disabled)&&/can't be matched/.test(rows[2].textContent),'a title without ids can\'t be chosen and says why');
  rows[0].querySelectorAll('button')[0].click(); await sleep(120);
  const made=calls(w,'DiscMenus/Editor/New');
  check(made.length===1&&made[0].query.item==='11111111111111111111111111111111'&&made[0].query.kind==='blank','Empty menu asks for a blank menu for that title');
  check($(w,'discEdFile').value==='new-one.menu.json'&&$(w,'discEdNewPanel').hidden&&/Created new-one/.test($(w,'discEdStatus').textContent),'the new menu is opened and the panel closes');
  check(files(w).includes('new-one.menu.json'),'and it is in the file list');

  w=boot(fake({titles})); await start(w); await click(w,'discEdNew');
  $(w,'discEdNewQuery').value='matrix'; $(w,'discEdNewQuery').dispatchEvent(new w.Event('keydown',{bubbles:true})); 
  const ev=new w.KeyboardEvent('keydown',{key:'Enter',bubbles:true}); $(w,'discEdNewQuery').dispatchEvent(ev); await sleep(60);
  [...w.document.querySelectorAll('#discEdNewList .discEdRow')][0].querySelectorAll('button')[1].click(); await sleep(100);
  check(calls(w,'DiscMenus/Editor/New')[0].query.kind==='draft','Starter asks for a draft; Enter searches at once');

  w=boot(fake({titles,over:{'POST DiscMenus/Editor/New':()=>err(400,{Error:'No usable id.'})}})); await start(w); await click(w,'discEdNew');
  $(w,'discEdNewQuery').value='matrix'; $(w,'discEdNewQuery').dispatchEvent(new w.Event('input')); await sleep(80);
  [...w.document.querySelectorAll('#discEdNewList .discEdRow')][0].querySelectorAll('button')[0].click(); await sleep(80);
  check($(w,'discEdNewMsg').textContent==='No usable id.'&&!$(w,'discEdNewPanel').hidden,'a refusal is shown in the panel, which stays open');

  w=boot(fake({titles:[]})); await start(w); await click(w,'discEdNew');
  $(w,'discEdNewQuery').value='zzz'; $(w,'discEdNewQuery').dispatchEvent(new w.Event('input')); await sleep(80);
  check(/No movie or series/.test($(w,'discEdNewMsg').textContent),'no matches says so');

  console.log('--- New does not throw away unsaved edits');
  w=boot(fake(),false); await start(w);
  $(w,'discEdText').value+=' '; $(w,'discEdText').dispatchEvent(new w.Event('input'));
  await click(w,'discEdNew'); check($(w,'discEdNewPanel').hidden&&w.__confirms.length===1,'declining the discard prompt keeps the editor as it is');
  await click(w,'discEdDuplicate'); check(calls(w,'DiscMenus/Editor/Duplicate').length===0,'and Duplicate does nothing');

  console.log('--- Duplicate');
  w=boot(fake()); await start(w); await click(w,'discEdDuplicate'); await sleep(80);
  check(calls(w,'DiscMenus/Editor/Duplicate')[0].query.name==='a.menu.json'&&$(w,'discEdFile').value==='a-copy.menu.json'&&/new id/.test($(w,'discEdStatus').textContent),'the copy is opened');
  w=boot(fake({over:{'POST DiscMenus/Editor/Duplicate':()=>err(422,{Errors:[{Message:'broken menu'}]})}})); await start(w); await click(w,'discEdDuplicate');
  check($(w,'discEdStatus').textContent==='broken menu'&&$(w,'discEdFile').value==='a.menu.json','a refusal is shown and nothing else changes');

  console.log('--- Delete');
  w=boot(fake({files:{'a.menu.json':TEXT(1),'b.menu.json':TEXT(1)}}),false); await start(w);
  await click(w,'discEdDelete'); check(calls(w,'DiscMenus/Editor/File','DELETE').length===0&&/a\.menu\.json/.test(w.__confirms[0]),'declining the confirmation deletes nothing');
  w=boot(fake({files:{'a.menu.json':TEXT(1),'b.menu.json':TEXT(1)}})); await start(w);
  await click(w,'discEdDelete'); await sleep(80);
  check(calls(w,'DiscMenus/Editor/File','DELETE').length===1&&!files(w).includes('a.menu.json')&&$(w,'discEdFile').value==='b.menu.json','deleting removes it and opens the next menu');
  w=boot(fake()); await start(w); await click(w,'discEdDelete'); await sleep(80);
  check($(w,'discEdText').disabled&&$(w,'discEdText').value===''&&$(w,'discEdDelete').disabled,'deleting the last menu leaves an empty, disabled editor');
  w=boot(fake({over:{'DELETE DiscMenus/Editor/File':()=>err(400,{Error:'Remove it on the Menu Catalogue page.'})}})); await start(w); await click(w,'discEdDelete');
  check(/Menu Catalogue/.test($(w,'discEdStatus').textContent)&&$(w,'discEdText').value!=='','a refusal keeps the menu open');

  console.log('--- Backups and Restore');
  const backups=[{Stamp:'20260102030405678',Size:2048,Revision:4,ModifiedUtc:'x'},{Stamp:'20260101000000000',Size:100,Revision:null,ModifiedUtc:'x'}];
  w=boot(fake({backups})); await start(w); await click(w,'discEdBackups');
  let brows=[...w.document.querySelectorAll('#discEdBackupList .discEdRow')];
  check(!$(w,'discEdBackupPanel').hidden&&brows.length===2&&/revision 4/.test(brows[0].textContent)&&/2 KB/.test(brows[0].textContent)&&!/revision/.test(brows[1].textContent),'backups are listed newest first with revision and size');
  brows[0].querySelector('button').click(); await sleep(100);
  const rc=calls(w,'DiscMenus/Editor/Restore')[0];
  check(rc.query.name==='a.menu.json'&&rc.query.backup==='20260102030405678'&&rc.query.version==='v1','Restore sends the backup and the version the editor has');
  check($(w,'discEdBackupPanel').hidden&&/"revision": 5/.test($(w,'discEdText').value)&&/Restored/.test($(w,'discEdStatus').textContent),'the restored text is loaded');
  w=boot(fake({backups:[]})); await start(w); await click(w,'discEdBackups');
  check(/No earlier versions/.test($(w,'discEdBackupMsg').textContent),'no backups says so');
  w=boot(fake({backups,over:{'POST DiscMenus/Editor/Restore':()=>err(409,{Error:'changed'})}})); await start(w); await click(w,'discEdBackups');
  w.document.querySelector('#discEdBackupList button').click(); await sleep(80);
  check(/changed on the server/.test($(w,'discEdBackupMsg').textContent)&&!$(w,'discEdBackupPanel').hidden,'a conflict asks you to reload');
  w=boot(fake({backups}),false); await start(w); await click(w,'discEdBackups');
  $(w,'discEdText').value+=' '; $(w,'discEdText').dispatchEvent(new w.Event('input'));
  w.document.querySelector('#discEdBackupList button').click(); await sleep(60);
  check(calls(w,'DiscMenus/Editor/Restore').length===0,'Restore asks before discarding unsaved edits');

  console.log('failures: '+fail); process.exit(fail?1:0);
})();
