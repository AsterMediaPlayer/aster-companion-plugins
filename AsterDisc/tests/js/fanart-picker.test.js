// The Menu Editor's fanart.tv picker: the in-place JSON patch it uses (only the background property changes, formatting is kept)
// and the picker itself (listing, categories, thumbnails, applying, undo, every failure in words, hostile data).
// Runs the real browser code in a simulated browser (jsdom) with scripted server responses.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const html=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Configuration/editorPage.html'),'utf8');
const script=html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];
let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e===undefined?'':e); };
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const ITEM='4cf4efb9cb2c96b4ec157568bcccdf7f';
const MENU=(extra)=>`{
  "schemaVersion": 1,
  "menuId": "3f2b8c1e-6d4a-4e2b-9a71-0c5d2e8f1a44",
  "revision": 3,
  "meta": { "author": "x", "notes": "has { braces }, \\"quotes\\", and \\"background\\": text" },
  "match": { "itemType": "Movie", "providerIds": {"Tmdb": "603"} },
  "theme": { "id": "t" },${extra||''}
  "root": "main",
  "menus": {
    "main": {
      "title": "Main Menu",
      "entries": [ { "action": "playFeature", "label": "Play" } ]
    },
    "features": {
      "title": "Special Features",
      "background": { "source": "color", "color": "#112233" },
      "entries": [ { "action": "back", "label": "Back" } ]
    }
  }
}
`;
const DOC={Root:'main',Menus:{main:{Title:'Main Menu'},features:{Title:'Special Features'}}};
const img=(id,category,likes,lang)=>({Id:String(id),Category:category,Lang:lang===undefined?'en':lang,Likes:likes});
const LIST={Status:'Ok',Images:[...Array.from({length:30},(_,i)=>img(1000+i,'moviebackground',i)),img(5,'hdmovielogo',9),img(6,'hdmovielogo',2),img(7,'moviedisc',1)]};

function boot(server){
  const dom=new JSDOM(html,{url:'https://s.example/web/index.html',runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window; w.console.warn=()=>{};
  w.__discMenusEditorDelay=20; w.__log=[]; w.confirm=()=>true;
  w.ApiClient={getUrl:(p,q)=>'https://s.example/'+p+(q&&Object.keys(q).length?'?'+Object.entries(q).map(([k,v])=>k+'='+encodeURIComponent(v)).join('&'):''),
    ajax:(req)=>{ const u=new URL(req.url); const e={method:req.type,path:u.pathname.replace(/^\//,''),query:Object.fromEntries(u.searchParams),body:req.data}; w.__log.push(e);
      return Promise.resolve(server(e,w)).then(r=>{ if(r.status>=400) return Promise.reject({status:r.status,json:()=>Promise.resolve(r.body)}); return r.body; }); }};
  w.__preview={calls:[],show(d,p){this.calls.push(['show']);},update(d,p){this.calls.push(['update']);},goTo(){},setMuted(){}};
  w.document.getElementById('discEdFrame').contentWindow.DiscMenusPreview=w.__preview;
  // the page loads these modules through script tags; the test supplies them directly
  w.__discMenusEditorModulesPreloaded = true;
  require('./editor-modules.js').forEach(n => w.eval(fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/editor/' + n + '.js'), 'utf8')));
  w.eval(script); return w;
}
const ok=(body)=>({status:200,body}); const err=(status,body)=>({status,body});
function standard(opts={}){
  let text=opts.text||MENU(); let version='v1';
  return (e,w)=>{
    if(opts.over&&opts.over[e.method+' '+e.path]) return opts.over[e.method+' '+e.path](e,w);
    if(e.path==='DiscMenus/Editor/Files') return ok([{File:'m.menu.json',Version:version,State:'bound',Title:'Matrix'}]);
    if(e.path==='DiscMenus/Editor/File'&&e.method==='GET') return ok({File:'m.menu.json',Json:text,Version:version});
    if(e.path==='DiscMenus/Editor/Preview') return ok({Document:DOC,ParentItemId:opts.unbound?null:ITEM,Bound:!opts.unbound,ItemName:'The Matrix'});
    if(e.path==='DiscMenus/Editor/File'&&e.method==='PUT'){ text=e.body; version='v2'; return ok({Version:'v2'}); }
    if(e.path.startsWith('DiscMenus/Fanart/List/')) return ok(opts.list||LIST);
    return err(500,null); };
}
const $=(w,id)=>w.document.getElementById(id);
const start=async(w)=>{ $(w,'DiscMusicDummy'); w.document.getElementById('DiscMenusEditorPage').dispatchEvent(new w.CustomEvent('pageshow')); await sleep(80); const fr=$(w,'discEdFrame'); fr.contentWindow.DiscMenusPreview=w.__preview; fr.dispatchEvent(new w.Event('load')); await sleep(80); };
const calls=(w,p)=>w.__log.filter(c=>c.path.startsWith(p));
const cells=w=>[...w.document.querySelectorAll('.discEdFanartCell')];
const msg=w=>$(w,'discEdFanartMsg').textContent;
const open=async(w)=>{ $(w,'discEdFanartOpen').click(); await sleep(60); };
const parse=t=>JSON.parse(t);

(async()=>{
  const probe=boot(standard()); const T=()=>probe.__discMenusEditorTools;
  const set=(text,key,id='777',dim=0.5)=>T().setBackground(text,key,{fanartId:id,dim});
  console.log('--- the in-place patch');
  const base=MENU();
  let out=set(base,'features');
  const idx=base.indexOf('"background": { "source": "color", "color": "#112233" }');
  check(out===base.slice(0,idx)+'"background": { "source": "fanart", "fanartId": "777", "dim": 0.5 }'+base.slice(idx+'"background": { "source": "color", "color": "#112233" }'.length),'an existing background is replaced and every other character is untouched');
  out=set(base,'');
  check(out.includes('  "match": { "itemType": "Movie", "providerIds": {"Tmdb": "603"} },\n  "background": { "source": "fanart", "fanartId": "777", "dim": 0.5 },\n  "theme"'),'a missing top-level background goes after "match", at the same indent', out.slice(250,520));
  check(parse(out).background.fanartId==='777'&&parse(out).menus.features.background.source==='color','the page background that already existed is not touched when setting the default');
  out=set(base,'main');
  check(out.includes('      "title": "Main Menu",\n      "background": { "source": "fanart", "fanartId": "777", "dim": 0.5 },\n      "entries"'),'a page without one gets it after its title, at the same indent', out.slice(700,900));
  check(JSON.stringify(parse(out).menus.main.entries)===JSON.stringify(parse(base).menus.main.entries),'the page\'s other properties are unchanged');
  const compact='{"menuId":"a","match":{"itemType":"Movie"},"root":"main","menus":{"main":{"title":"M","entries":[]}}}';
  out=set(compact,''); check(parse(out).background.fanartId==='777'&&out.startsWith('{"menuId":"a","match":{"itemType":"Movie"}, "background":'),'one-line JSON stays one line', out.slice(0,120));
  out=set(compact,'main'); check(parse(out).menus.main.background.source==='fanart','one-line page insert');
  const crlf=base.replace(/\n/g,'\r\n'); out=set(crlf,'');
  check(!/[^\r]\n/.test(out)&&parse(out).background.fanartId==='777','CRLF files keep CRLF line endings');
  const hostileText=MENU().replace('"x"','"\\u00e9 \\"background\\": {\\" ]]}}"');
  out=set(hostileText,''); check(parse(out).meta.author===parse(hostileText).meta.author&&parse(out).background.source==='fanart','braces, quotes, unicode and the word "background" inside strings do not confuse it');
  const empty='{"match":{},"root":"m","menus":{"m":{}}}'; out=set(empty,'m'); check(parse(out).menus.m.background.fanartId==='777','an empty page object is filled');
  const nested='{"match":{},"root":"m","menus":{"m":{"title":"T","background":{"source":"color","color":"#000000"},"entries":[]}}}';
  out=set(nested,''); check(parse(out).background.fanartId==='777'&&parse(out).menus.m.background.source==='color','a page\'s own background is never mistaken for the default');
  out=set(set(base,'features','1'),'features','2'); check(parse(out).menus.features.background.fanartId==='2'&&(out.match(/"fanart"/g)||[]).length===1,'picking again replaces, never duplicates');
  const noMatch='{"root":"m","menus":{"m":{"title":"T"}}}'; check(parse(set(noMatch,'')).background.fanartId==='777','without "match" it still inserts (after the last property)');
  for(const [bad,why] of [['{ nope','invalid JSON'],['[1,2]','an array'],['"text"','a string'],['{"a":1} extra','trailing text'],['{"a":1,}','a trailing comma'],['','nothing'],['{"a":[1,2,}','a broken array'],['{"a":"unterminated}','an unterminated string']]){
    let msgText=''; try{ set(bad,''); }catch(e){ msgText=e.message; }
    check(msgText!=='','refuses '+why, msgText.slice(0,60)); }
  let m1=''; try{ set('{"match":{}}','main'); }catch(e){ m1=e.message; } check(/no "menus"/.test(m1),'says when there is no menus section', m1);
  let m2=''; try{ set(base,'nope'); }catch(e){ m2=e.message; } check(/no menu "nope"/.test(m2),'says when the page does not exist', m2);
  let m3=''; try{ set('['.repeat(100)+']'.repeat(100),''); }catch(e){ m3=e.message; } check(m3!=='','absurdly deep nesting is refused, not a crash', m3.slice(0,50));
  let bad=0; for(const key of ['','main','features']) for(const id of ['1','123456789012']) for(const dim of [0,0.25,1]){ try{ const o=set(base,key,id,dim); const p=parse(o); const bg=key?p.menus[key].background:p.background; if(bg.fanartId!==id||bg.dim!==dim||bg.source!=='fanart') bad++; }catch(e){ bad++; } }
  check(bad===0,'every target, id and dim combination yields valid JSON with exactly what was asked');

  console.log('--- opening the picker');
  let w=boot(standard({unbound:true})); await start(w); await open(w);
  check(/Link this menu to a library title first/.test(msg(w))&&calls(w,'DiscMenus/Fanart/List').length===0&&cells(w).length===0,'a menu not linked to a title asks you to link it, and asks fanart.tv nothing');
  w=boot(standard()); await start(w); await open(w);
  const list=calls(w,'DiscMenus/Fanart/List');
  check(list.length===1&&list[0].path==='DiscMenus/Fanart/List/'+ITEM&&list[0].method==='GET','it asks the server for the bound title\'s pictures');
  const cats=[...$(w,'discEdFanartCat').options].map(o=>o.textContent);
  check(cats.join('|')==='Backgrounds (30)|Discs (1)|Logos (2)','categories are friendly, counted, with Backgrounds first', cats.join('|'));
  check(cells(w).length===12,'twelve pictures to begin with');
  const first=cells(w)[0].querySelector('img');
  check(first.src==='https://s.example/DiscMenus/Fanart/'+ITEM+'/1029'&&first.loading==='lazy'&&first.alt==='','thumbnails come from this server, best liked first, loaded lazily', first.src);
  check(cells(w)[0].textContent.includes('♥ 29')&&cells(w)[0].textContent.includes('en'),'each shows its language and likes');
  check(!$(w,'discEdFanartMore').hidden,'there is a Show more button'); $(w,'discEdFanartMore').click();
  check(cells(w).length===24,'Show more adds twelve'); $(w,'discEdFanartMore').click(); check(cells(w).length===30&&$(w,'discEdFanartMore').hidden,'and hides itself when everything is shown');
  $(w,'discEdFanartCat').value='hdmovielogo'; $(w,'discEdFanartCat').dispatchEvent(new w.Event('change'));
  check(cells(w).length===2&&cells(w)[0].querySelector('img').src.endsWith('/5'),'choosing another category shows its pictures');
  const targets=[...$(w,'discEdFanartTarget').options].map(o=>o.value+'='+o.textContent);
  check(targets.join('|')==="=Every page (the menu's default)|main=Only main - Main Menu|features=Only features - Special Features",'targets are every page, then each page by name', targets.join('|'));
  $(w,'discEdFanartClose').click(); check($(w,'discEdFanart').hidden,'Close hides the panel'); await open(w);
  check(calls(w,'DiscMenus/Fanart/List').length===1&&cells(w).length>0,'reopening does not ask again for the same title');

  console.log('--- every way fanart.tv can fail is explained');
  for(const [status,re] of [['NoKey',/No fanart.tv key/],['BadKey',/rejected/],['NotFound',/no pictures/],['RateLimited',/slow down/],['Unavailable',/could not be reached/],['Strange',/does not understand/]]){
    w=boot(standard({list:{Status:status,Images:[]}})); await start(w); await open(w);
    check(re.test(msg(w))&&cells(w).length===0,'status '+status+': '+msg(w).slice(0,50)); }
  w=boot(standard({over:{['GET DiscMenus/Fanart/List/'+ITEM]:()=>err(404,{Error:'That item has no TMDB id.'})}})); await start(w); await open(w); check(msg(w)==='That item has no TMDB id.','a server explanation is shown as it is');
  w=boot(standard({over:{['GET DiscMenus/Fanart/List/'+ITEM]:()=>err(500,null)}})); await start(w); await open(w); check(/could not be listed/.test(msg(w)),'a failing call is reported');
  w=boot(standard({list:{Status:'Ok',Images:[]}})); await start(w); await open(w); check(/no pictures/.test(msg(w)),'an empty listing says so');

  console.log('--- hostile data from the listing');
  w=boot(standard({list:{Status:'Ok',Images:[img('1; DROP','moviebackground',1),img('12ab','moviebackground',1),img('1234567890123','moviebackground',1),{Id:5,Category:'moviebackground'},{Category:'x'},null,'str',
    img(42,'moviebackground',3,'<img src=x onerror=alert(1)>'),img(43,'<b>logos</b>',1)]}})); await start(w); await open(w);
  check(cells(w).length===1&&cells(w)[0].querySelector('img').src.endsWith('/42'),'entries with unsafe ids are dropped, not shown');
  check(w.document.querySelectorAll('#discEdFanartGrid img').length===1&&!w.document.querySelector('#discEdFanartGrid b')&&cells(w)[0].textContent.includes('<img src=x'),'a hostile language is shown as text, never markup');
  check([...$(w,'discEdFanartCat').options].some(o=>o.textContent.includes('<b>logos</b>'))&&!w.document.querySelector('#discEdFanartCat b'),'a hostile category name is shown as text');

  console.log('--- using a picture');
  w=boot(standard()); await start(w); await open(w);
  const original=$(w,'discEdText').value; w.__log.length=0; w.__preview.calls.length=0;
  cells(w)[0].click(); await sleep(120);
  let t=$(w,'discEdText').value;
  check(parse(t).background.fanartId==='1029'&&parse(t).background.dim===0.5&&parse(t).background.source==='fanart','clicking a picture sets the default background (dim 0.5)', t.slice(250,330));
  check(t.replace(/\n  "background": \{[^\n]*\},/,'')===original,'and changes nothing else in the file');
  check($(w,'discEdStatus').textContent==='Unsaved changes','the editor shows unsaved changes');
  check(calls(w,'DiscMenus/Editor/Preview').length===1,'the live preview is refreshed');
  check(/Used picture 1029 for every page\. Save to keep it\./.test(msg(w))&&!!$(w,'discEdFanartMsg').querySelector('button'),'the message says what happened and offers Undo');
  check(cells(w)[0].className.includes('picked'),'the chosen picture is highlighted');
  $(w,'discEdFanartMsg').querySelector('button').click(); await sleep(60);
  check($(w,'discEdText').value===original&&$(w,'discEdStatus').textContent==='Saved'&&/Undone/.test(msg(w)),'Undo restores the exact original text');
  $(w,'discEdFanartTarget').value='features'; $(w,'discEdFanartDim').value='0.3'; cells(w)[1].click(); await sleep(60);
  t=$(w,'discEdText').value;
  check(parse(t).menus.features.background.fanartId==='1028'&&parse(t).menus.features.background.dim===0.3&&parse(t).background===undefined&&/for the "features" page/.test(msg(w)),'a single page can be chosen, with its own darkening');
  for(const [typed,want] of [['5',1],['-2',0],['',0.5],['abc',0.5],['0.333',0.33]]){
    w=boot(standard()); await start(w); await open(w); $(w,'discEdFanartDim').value=typed; cells(w)[0].click(); await sleep(40);
    check(parse($(w,'discEdText').value).background.dim===want,'darkening "'+typed+'" becomes '+want, parse($(w,'discEdText').value).background.dim); }
  w=boot(standard()); await start(w); await open(w);
  $(w,'discEdText').value='{ "broken": '; $(w,'discEdText').dispatchEvent(new w.Event('input')); await sleep(40);
  cells(w)[0].click(); await sleep(40);
  check($(w,'discEdText').value==='{ "broken": '&&/JSON has an error/.test(msg(w)),'with invalid JSON in the editor it refuses and leaves the text alone');
  w=boot(standard()); await start(w); await open(w); cells(w)[0].click(); await sleep(60);
  $(w,'discEdSave').click(); await sleep(120);
  const put=calls(w,'DiscMenus/Editor/File').filter(c=>c.method==='PUT')[0];
  check(!!put&&parse(put.body).background.fanartId==='1029'&&$(w,'discEdStatus').textContent==='Saved','saving then writes the picked background');
  check(w.__log.every(c=>!/api[_-]?key/i.test(JSON.stringify(c))),'no key is ever involved in the editor\'s calls');

  console.log('failures:',fail); process.exit(fail?1:0);
})();
