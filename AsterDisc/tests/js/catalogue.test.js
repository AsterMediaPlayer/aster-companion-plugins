// The dashboard Menu Catalogue page: loading the public index, matching, install/update/uninstall, and hostile index content.
// Runs the real browser code in a simulated browser (jsdom) with a scripted catalogue and a scripted server.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const html=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Configuration/cataloguePage.html'),'utf8');
const script=html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];
let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e===undefined?'':e); };
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const BASE='https://peppy6582.github.io/jellydisc-menus/';
const ID1='0b311cdd-e7c2-4e27-b4bc-fb1e5acebfc8', ID2='24284908-2c95-4dc7-9bad-8284ff8188c5', ID3='3be8f617-e13f-4e71-9da1-3c43bd3fc319';
const sha=(c)=>c.repeat(64);
const entry=(id,over={})=>({menuId:id,revision:1,menuSchemaVersion:1,minPluginVersion:'0.1.0',title:'Inception',year:2010,match:{itemType:'Movie',providerIds:{Tmdb:'27205'}},
  author:{name:'x'},licence:'CC0-1.0',tags:['minimal'],description:'A plain menu.',url:`v1/menus/${id}/1.menu.json`,sha256:sha('a'),size:5,features:[],needsLocalArt:[],demo:true,page:`menus/${id}/`,...over});
const INDEX=(entries,extra={})=>({format:'jellydisc-catalogue',formatVersion:1,generated:'2026-10-02T00:00:00Z',menuSchemaVersions:[1],baseUrl:BASE,licence:'CC0-1.0',attribution:'x',entries,withdrawn:[],...extra});

// boot(catalogueFetch, server): catalogueFetch(url) -> {status, body(text)}; server(entry) -> {status, body}
function boot(fetchHandler, server, opts={}){
  const dom=new JSDOM(html,{url:'https://s.example/web/index.html',runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window; w.console.warn=()=>{};
  w.__log=[]; w.__fetches=[]; w.__confirms=[]; w.confirm=(m)=>{ w.__confirms.push(m); return opts.confirm!==false; };
  w.TextEncoder=TextEncoder;
  w.fetch=(url,init)=>{ w.__fetches.push({url,init}); const r=fetchHandler(url); return Promise.resolve({ok:r.status>=200&&r.status<300,status:r.status,json:()=>Promise.resolve(JSON.parse(r.body)),text:()=>Promise.resolve(r.body)}); };
  w.ApiClient={getUrl:(p,q)=>'https://s.example/'+p+(q&&Object.keys(q).length?'?'+Object.entries(q).map(([k,v])=>k+'='+encodeURIComponent(v)).join('&'):''),
    ajax:(req)=>{ const u=new URL(req.url); const e={method:req.type,path:u.pathname.replace(/^\//,''),query:Object.fromEntries(u.searchParams),body:req.data,contentType:req.contentType}; w.__log.push(e);
      return Promise.resolve(server(e)).then(r=>{ if(r.status>=400){ return Promise.reject({status:r.status,json:()=>Promise.resolve(r.body)}); } return r.body; }); }};
  w.eval(script);
  return w;
}
const ok=(body)=>({status:200,body}); const err=(status,body)=>({status,body});
const state=(over={})=>({installed:[],version:'0.1.0',matches:{},...over});
function server(s, over={}){
  return (e)=>{ const k=e.method+' '+e.path; if(over[k]) return over[k](e,s);
    if(k==='GET DiscMenus/Catalogue/Installed') return ok({PluginVersion:s.version,Installed:s.installed});
    if(k==='POST DiscMenus/Catalogue/Match') return ok(JSON.parse(e.body).map(q=>s.matches[q.menuId]||{MenuId:q.menuId,State:'none',Titles:[]}));
    return err(500,null); };
}
const page=w=>w.document.getElementById('DiscMenusCataloguePage');
const $=(w,id)=>w.document.getElementById(id);
const cards=w=>[...w.document.querySelectorAll('.discCatCard')];
const start=async(w)=>{ page(w).dispatchEvent(new w.CustomEvent('pageshow')); await sleep(60); };
const calls=(w,p,m)=>w.__log.filter(c=>c.path.startsWith(p)&&(!m||c.method===m));
const bound=(id,name='Inception',year=2010)=>({MenuId:id,State:'bound',Titles:[{Id:'x',Name:name,Year:year}]});
const btn=(c,label)=>[...c.querySelectorAll('button')].find(b=>b.textContent.trim()===label||b.textContent.trim().startsWith(label));
const index=(entries,extra)=>(u)=>u===BASE+'v1/index.json'?{status:200,body:JSON.stringify(INDEX(entries,extra))}:{status:404,body:'nf'};

(async()=>{
  console.log('--- loading and matching');
  let s=state({matches:{[ID1]:bound(ID1)}});
  let w=boot(index([entry(ID1),entry(ID2,{title:'Spirited Away',year:2001,match:{itemType:'Movie',providerIds:{Tmdb:'129'}}})]),server(s)); await start(w);
  check(w.__fetches[0].url===BASE+'v1/index.json'&&w.__fetches[0].init.credentials==='omit'&&w.__fetches[0].init.referrerPolicy==='no-referrer','the index is fetched from the pinned address, without cookies or a referrer');
  const match=calls(w,'DiscMenus/Catalogue/Match')[0];
  const sent=JSON.parse(match.body);
  check(sent.length===2&&Object.keys(sent[0]).sort().join()==='match,menuId'&&sent[0].match.providerIds.Tmdb==='27205','only ids and the entry type go to the server for matching (the library is never sent anywhere)', JSON.stringify(sent[0]));
  check(cards(w).length===1&&cards(w)[0].textContent.includes('Inception')&&cards(w)[0].textContent.includes('In your library: Inception (2010).'),'by default only titles in the library are listed');
  $(w,'discCatOnlyMine').checked=false; $(w,'discCatOnlyMine').dispatchEvent(new w.Event('change'));
  check(cards(w).length===2&&cards(w)[0].textContent.includes('Inception')&&cards(w)[1].textContent.includes('Not in your library.'),'unticking shows everything, library matches first');
  $(w,'discCatSearch').value='spirit'; $(w,'discCatSearch').dispatchEvent(new w.Event('input'));
  check(cards(w).length===1&&cards(w)[0].textContent.includes('Spirited Away'),'search narrows the list');
  $(w,'discCatSearch').value='minimal'; $(w,'discCatSearch').dispatchEvent(new w.Event('input'));
  check(cards(w).length===2,'search covers tags');

  console.log('--- ambiguous and art notices');
  s=state({matches:{[ID1]:{MenuId:ID1,State:'ambiguous',Titles:[{Name:'A',Year:1999},{Name:'B',Year:null}]}}});
  w=boot(index([entry(ID1,{needsLocalArt:['background.webp'],demo:false})]),server(s)); await start(w);
  check(/Several titles.*A \(1999\), B\./.test(cards(w)[0].textContent),'an ambiguous match says so instead of guessing');
  check(cards(w)[0].textContent.includes('bring your own art')&&cards(w)[0].textContent.includes('assets/'+ID1+'/')&&cards(w)[0].textContent.includes('background.webp'),'a menu that needs art says which files and where');

  console.log('--- a hostile index');
  const evil=`<img src=x onerror=alert(1)>`;
  const idx=INDEX([
    entry(ID1,{title:evil,description:`<script>alert(2)</script>`,tags:[evil,5],year:'2010'}),
    entry(ID2,{url:'https://evil.example/x.menu.json'}), entry(ID3,{url:'../../../etc/passwd'}),
    entry('not-an-id'), entry(ID2,{sha256:'short'}), entry(ID2,{size:999999999}), entry(ID2,{revision:0}), entry(ID2,{match:{itemType:'Weird',providerIds:{}}}), null, 'str', 5,
  ],{baseUrl:'https://evil.example/',withdrawn:[{menuId:'x',reason:'nope'}]});
  s=state({matches:{}}); w=boot(()=>({status:200,body:JSON.stringify(idx)}),server(s)); await start(w);
  $(w,'discCatOnlyMine').checked=false; $(w,'discCatOnlyMine').dispatchEvent(new w.Event('change'));
  check(cards(w).length===1,'entries with an unexpected shape are dropped, not displayed', cards(w).length);
  check(cards(w)[0].textContent.includes(evil)&&w.document.querySelector('img')===null&&w.document.querySelector('.discCatCard script')===null,'index text is shown as text, never markup');
  check(calls(w,'DiscMenus/Catalogue/Match')[0]&&JSON.parse(calls(w,'DiscMenus/Catalogue/Match')[0].body).length===1,'only well-formed entries are matched');
  check(!/year/.test(cards(w)[0].querySelector('h3').innerHTML.replace('class="year"','')),'a non-integer year is ignored');

  console.log('--- wrong format and network failure');
  w=boot(()=>({status:200,body:JSON.stringify({format:'other',formatVersion:1,entries:[]})}),server(state())); await start(w);
  check(/format this plugin version does not understand/.test($(w,'discCatStatus').textContent)&&$(w,'discCatStatus').className.includes('error'),'an unknown index format is reported');
  w=boot(()=>({status:500,body:'x'}),server(state())); await start(w);
  check(/answered 500/.test($(w,'discCatStatus').textContent),'a failing catalogue is reported');
  w=boot(index([entry(ID1)]),server(state(),{'POST DiscMenus/Catalogue/Match':()=>err(500,null)})); await start(w);
  check(/Could not match/.test($(w,'discCatStatus').textContent),'a failing match is reported');

  console.log('--- installing');
  const MENU_BODY='12345';
  s=state({matches:{[ID1]:bound(ID1)}});
  w=boot((u)=>u===BASE+'v1/index.json'?{status:200,body:JSON.stringify(INDEX([entry(ID1,{sha256:sha('b'),size:5})],{baseUrl:'https://evil.example/'}))}:{status:200,body:MENU_BODY},
    server(s,{'POST DiscMenus/Catalogue/Install':(e)=>{ s.installed=[{MenuId:ID1,Revision:1,Sha256:sha('b'),EditedLocally:false,File:'catalogue/x'}]; return ok({Action:'installed',Revision:1}); }}));
  await start(w);
  check(!!btn(cards(w)[0],'Install')&&!btn(cards(w)[0],'Uninstall'),'an uninstalled menu offers Install');
  btn(cards(w)[0],'Install').click(); await sleep(60);
  const dl=w.__fetches.find(f=>f.url.includes('/menus/'));
  check(dl&&dl.url===BASE+`v1/menus/${ID1}/1.menu.json`,'the file comes from the pinned catalogue, whatever the index says its base is', dl&&dl.url);
  const post=calls(w,'DiscMenus/Catalogue/Install','POST')[0];
  check(post&&post.query.sha256===sha('b')&&post.body===MENU_BODY&&post.contentType==='text/plain','the file is posted with the index\'s fingerprint');
  check(cards(w)[0].textContent.includes('Installed.')&&cards(w)[0].textContent.includes('Installed (revision 1).')&&!!btn(cards(w)[0],'Uninstall')&&!btn(cards(w)[0],'Install'),'after installing: confirmed, and Uninstall is offered');

  console.log('--- install failures');
  s=state({matches:{[ID1]:bound(ID1)}});
  w=boot(index([entry(ID1,{size:3})]),server(s)); await start(w); // body is not JSON, size stub below
  w=boot((u)=>u.includes('index.json')?{status:200,body:JSON.stringify(INDEX([entry(ID1,{size:3})]))}:{status:200,body:'12345'},server(s)); await start(w);
  btn(cards(w)[0],'Install').click(); await sleep(60);
  check(calls(w,'DiscMenus/Catalogue/Install','POST').length===0&&/not the size/.test(cards(w)[0].textContent),'a file that is not the listed size is never sent to the server');
  check(!btn(cards(w)[0],'Install').disabled,'the button comes back after a failure');
  w=boot((u)=>u.includes('index.json')?{status:200,body:JSON.stringify(INDEX([entry(ID1,{size:5})]))}:{status:404,body:'x'},server(s)); await start(w);
  btn(cards(w)[0],'Install').click(); await sleep(60);
  check(/answered 404/.test(cards(w)[0].textContent),'a missing file is reported');
  const body5=(u)=>u.includes('index.json')?{status:200,body:JSON.stringify(INDEX([entry(ID1,{size:5})]))}:{status:200,body:'12345'};
  w=boot(body5,server(s,{'POST DiscMenus/Catalogue/Install':()=>err(409,{Error:'A menu with this id is already in your menus folder (mine.menu.json).'})})); await start(w);
  btn(cards(w)[0],'Install').click(); await sleep(60);
  check(/already in your menus folder/.test(cards(w)[0].textContent)&&cards(w)[0].querySelector('.discCatMsg.error'),'a refusal from the server is shown');
  w=boot(body5,server(s,{'POST DiscMenus/Catalogue/Install':()=>err(422,{Errors:[{Message:'Label too long.'},{Message:'Bad colour.'}]})})); await start(w);
  btn(cards(w)[0],'Install').click(); await sleep(60);
  check(/Label too long\. Bad colour\./.test(cards(w)[0].textContent),'validation errors are listed');
  w=boot(body5,server(s,{'POST DiscMenus/Catalogue/Install':()=>err(422,{Error:'The file doesn\'t match the fingerprint the catalogue listed.'})})); await start(w);
  btn(cards(w)[0],'Install').click(); await sleep(60);
  check(/fingerprint/.test(cards(w)[0].textContent),'a fingerprint mismatch is shown');

  console.log('--- updates, edits and uninstalling');
  s=state({matches:{[ID1]:bound(ID1)},installed:[{MenuId:ID1,Revision:1,Sha256:sha('a'),EditedLocally:false}]});
  w=boot(index([entry(ID1,{revision:2,url:`v1/menus/${ID1}/2.menu.json`})]),server(s)); await start(w);
  check(!!btn(cards(w)[0],'Update to revision 2')&&!!btn(cards(w)[0],'Uninstall'),'a newer revision offers an update');
  s=state({matches:{[ID1]:bound(ID1)},installed:[{MenuId:ID1,Revision:1,Sha256:sha('a'),EditedLocally:true}]});
  w=boot(index([entry(ID1,{revision:2,url:`v1/menus/${ID1}/2.menu.json`})]),server(s)); await start(w);
  check(!btn(cards(w)[0],'Update')&&/edited on this server/.test(cards(w)[0].textContent)&&!!btn(cards(w)[0],'Uninstall'),'an edited menu is not offered an update that would overwrite it');
  s=state({matches:{[ID1]:bound(ID1)},installed:[{MenuId:ID1,Revision:1,Sha256:sha('a'),EditedLocally:false}]});
  w=boot(index([entry(ID1)]),server(s,{['DELETE DiscMenus/Catalogue/'+ID1]:()=>{ s.installed=[]; return ok({Action:'removed'}); }}),{confirm:false}); await start(w);
  btn(cards(w)[0],'Uninstall').click(); await sleep(40);
  check(calls(w,'DiscMenus/Catalogue/'+ID1,'DELETE').length===0&&w.__confirms.length===1&&/backup/.test(w.__confirms[0]),'declining the confirmation deletes nothing');
  check(!btn(cards(w)[0],'Uninstall').disabled,'and the button comes back');
  w=boot(index([entry(ID1)]),server(s,{['DELETE DiscMenus/Catalogue/'+ID1]:()=>{ s.installed=[]; return ok({Action:'removed'}); }})); await start(w);
  btn(cards(w)[0],'Uninstall').click(); await sleep(60);
  check(calls(w,'DiscMenus/Catalogue/'+ID1,'DELETE').length===1&&!!btn(cards(w)[0],'Install')&&cards(w)[0].textContent.includes('Uninstalled.'),'confirming uninstalls and the card goes back to Install');

  console.log('--- plugin version and withdrawn menus');
  s=state({version:'0.1.0',matches:{[ID1]:bound(ID1)}});
  w=boot(index([entry(ID1,{minPluginVersion:'0.2.0'})]),server(s)); await start(w);
  check(!btn(cards(w)[0],'Install')&&/Needs Disc Menus 0\.2\.0 or later \(this is 0\.1\.0\)/.test(cards(w)[0].textContent),'a menu that needs a newer plugin is not installable');
  s=state({version:'0.10.0',matches:{[ID1]:bound(ID1)}});
  w=boot(index([entry(ID1,{minPluginVersion:'0.9.0'})]),server(s)); await start(w);
  check(!!btn(cards(w)[0],'Install'),'versions compare numerically (0.10.0 is newer than 0.9.0)');
  s=state({matches:{},installed:[{MenuId:ID3,Revision:1,Sha256:sha('a'),EditedLocally:false}]});
  w=boot(index([entry(ID1)],{withdrawn:[{menuId:ID3,date:'2026-10-02',reason:'Taken down at the rights holder\'s request.'}]}),server(s)); await start(w);
  check(cards(w).some(c=>c.textContent.includes('was withdrawn')&&c.textContent.includes('rights holder')&&!!btn(c,'Uninstall')),'an installed menu that was withdrawn is flagged, with a way to uninstall');

  console.log('--- titles without special features');
  const titles=(...ex)=>({MenuId:ID1,State:ex.length>1?'ambiguous':'bound',Titles:ex.map((n,i)=>({Id:'t'+i,Name:'T'+i,Year:2000,Extras:n}))});
  const warns=(w)=>cards(w).some(c=>/No special features were found/.test(c.textContent));
  const extrasCase=async(entryOver,match)=>{ const w=boot(index([entry(ID1,entryOver)]),server(state({matches:{[ID1]:match}}))); await start(w); return w; };
  w=await extrasCase({},titles(0)); check(warns(w)&&/extras folder|"extras" folder/.test(cards(w)[0].textContent),'a matched title with no extras is flagged, and the card says how to fix it');
  w=await extrasCase({extras:5},titles(0)); check(warns(w),'flagged when the menu lists extras');
  w=await extrasCase({extras:0},titles(0)); check(!warns(w),'not flagged when the menu lists no extras (nothing is lost)');
  w=await extrasCase({extras:5},titles(3)); check(!warns(w),'not flagged when the title has extras');
  w=await extrasCase({},titles(0,0)); check(warns(w),'an ambiguous match is flagged only if none of the candidates has extras');
  w=await extrasCase({},titles(0,2)); check(!warns(w),'an ambiguous match where one candidate has extras is not flagged');
  w=await extrasCase({},{MenuId:ID1,State:'none',Titles:[]}); $(w,'discCatOnlyMine').checked=false; $(w,'discCatOnlyMine').dispatchEvent(new w.Event('change')); check(!warns(w),'a title that is not in the library is not flagged');
  w=await extrasCase({},{MenuId:ID1,State:'bound',Titles:[{Id:'x',Name:'T',Year:2000}]}); check(!warns(w),'an older server that does not report extras counts is not flagged');
  w=boot(body5,server(state({matches:{[ID1]:titles(0)}}),{'POST DiscMenus/Catalogue/Install':()=>ok({Action:'installed',Revision:1})})); await start(w);
  btn(cards(w)[0],'Install').click(); await sleep(60);
  check(/Installed\./.test(cards(w)[0].textContent)&&/no special features in your library yet/.test(cards(w)[0].querySelector('.discCatMsg').textContent),'installing such a menu confirms it and repeats the note');

  console.log('failures:',fail); process.exit(fail?1:0);
})();
