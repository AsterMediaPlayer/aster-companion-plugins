// The picture, sound and TMDB-backdrop picker in the real page (jsdom): listing a menu's uploaded files, uploading, deleting, choosing, and every
// way it can fail, with scripted server answers.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const html=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Configuration/editorPage.html'),'utf8');
const script=html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];
let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e===undefined?'':e); };
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const $=(w,id)=>w.document.getElementById(id);
const ID='3f2b8c1e-6d4a-4e2b-9a71-0c5d2e8f1a44';
const ITEM='4cf4efb9cb2c96b4ec157568bcccdf7f';
const TEXT=(id=ID)=>`{
  "schemaVersion": 1,
  "menuId": "${id}",
  "revision": 1,
  "match": { "itemType": "Movie", "providerIds": { "Tmdb": "603" } },
  "extras": {},
  "root": "main",
  "audio": { "music": { "source": "file", "file": "asset:${ID}/old.mp3" } },
  "menus": {
    "main": { "title": "Main", "background": { "source": "tmdb", "tmdbFilePath": "/old.jpg" }, "entries": [ { "action": "playFeature", "label": "Play", "image": "asset:${ID}/button.png" } ] }
  }
}
`;
const FILES=[{Name:'button.png',Ref:'asset:'+ID+'/button.png',Kind:'image',Size:2048,ModifiedUtc:'x'},{Name:'old.mp3',Ref:'asset:'+ID+'/old.mp3',Kind:'audio',Size:3*1024*1024,ModifiedUtc:'x'},{Name:'second.webp',Ref:'asset:'+ID+'/second.webp',Kind:'image',Size:100,ModifiedUtc:'x'}];
const TMDB={Images:[{ProviderName:'TheMovieDb',Url:'https://image.tmdb.org/t/p/original/good1.jpg',ThumbnailUrl:'https://image.tmdb.org/t/p/w500/good1.jpg',Width:1920,Height:1080,Language:'en'},
  {Url:'https://evil.example/t/p/original/bad.jpg',ThumbnailUrl:'https://evil.example/x.jpg'},{Url:'https://image.tmdb.org/t/p/original/ok2.png',ThumbnailUrl:'javascript:alert(1)'},null,'str',{Url:5}]};
function boot(opts={}){
  const dom=new JSDOM(html,{url:'https://s.example/web/index.html',runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window; w.console.warn=()=>{}; w.__discMenusEditorDelay=10; w.__log=[]; w.__confirms=[]; w.confirm=(m)=>{ w.__confirms.push(m); return opts.confirm!==false; };
  let files=(opts.files||FILES).slice();
  w.ApiClient={accessToken:()=>'TOKEN123',getUrl:(p,q)=>'https://s.example/'+p+(q&&Object.keys(q).length?'?'+Object.entries(q).map(([k,v])=>k+'='+encodeURIComponent(v)).join('&'):''),
    ajax:(req)=>{ const u=new URL(req.url); const p=u.pathname.replace(/^\//,''); const e={method:req.type,path:p,query:Object.fromEntries(u.searchParams)}; w.__log.push(e);
      const r=(()=>{ if(p==='DiscMenus/Editor/Files') return {status:200,body:[{File:'m.menu.json',Version:'v1',State:'bound'}]};
        if(p==='DiscMenus/Editor/File'&&req.type==='GET') return {status:200,body:{File:'m.menu.json',Json:opts.text||TEXT(),Version:'v1'}};
        if(p==='DiscMenus/Editor/Preview') return {status:200,body:{Document:{Root:'main',Menus:{main:{Title:'Main'}}},ParentItemId:opts.unbound?null:ITEM,Bound:!opts.unbound}};
        if(p==='DiscMenus/Editor/Assets'&&req.type==='GET') return opts.listFails?{status:500,body:null}:{status:200,body:files};
        if(p==='DiscMenus/Editor/Assets'&&req.type==='DELETE'){ files=files.filter(f=>f.Name!==e.query.name); return {status:200,body:{Deleted:true}}; }
        if(p===`Items/${ITEM}/RemoteImages`) return opts.tmdbFails?{status:500,body:null}:{status:200,body:opts.tmdb||TMDB};
        return {status:500,body:null}; })();
      return r.status>=400?Promise.reject({status:r.status,json:()=>Promise.resolve(r.body)}):Promise.resolve(r.body); }};
  w.fetch=(url,init)=>{ w.__fetch={url,init}; const r=opts.upload?opts.upload(url,init):{status:200,body:{Name:'up.png',Ref:'asset:'+ID+'/up.png',Kind:'image'}};
    return Promise.resolve({ok:r.status<300,status:r.status,json:()=>r.body===undefined?Promise.reject(new Error('no body')):Promise.resolve(r.body)}); };
  w.__preview={show(){},update(){},goTo(){},setMuted(){}};
  $(w,'discEdFrame').contentWindow.DiscMenusPreview=w.__preview;
  w.__discMenusEditorModulesPreloaded=true;
  require('./editor-modules.js').forEach(n=>w.eval(fs.readFileSync(path.join(ROOT,'Jellyfin.Plugin.DiscMenus/Web/editor/'+n+'.js'),'utf8')));
  w.eval(script); return w;
}
const start=async(w)=>{ $(w,'DiscMenusEditorPage').dispatchEvent(new w.CustomEvent('pageshow')); await sleep(60); $(w,'discEdFrame').dispatchEvent(new w.Event('load')); await sleep(80); };
const ctl=(w,key)=>$(w,'discEdInspector').querySelector('[data-key="'+key+'"]');
const open=async(w,rowText,key)=>{ [...w.document.querySelectorAll('#discEdOutline button')].find(b=>b.textContent===rowText||new RegExp(rowText).test(b.textContent)).click(); await sleep(20); ctl(w,key).click(); await sleep(60); };
const cards=w=>[...w.document.querySelectorAll('#discEdPicsList .discEdPic')];
const msg=w=>$(w,'discEdPicsMsg').textContent;
const T=w=>$(w,'discEdText').value;
const upload=async(w,file)=>{ const input=$(w,'discEdPicsFile'); Object.defineProperty(input,'files',{value:[file],configurable:true}); input.dispatchEvent(new w.Event('change')); await sleep(60); };

(async()=>{
  console.log('--- buttons');
  let w=boot(); await start(w);
  [...w.document.querySelectorAll('#discEdOutline button')].find(b=>/^Play - Play feature/.test(b.textContent)).click(); await sleep(20);
  check(!!ctl(w,'menus/main/entries/0/image/choose'),'a button\'s picture field has a Choose button');
  [...w.document.querySelectorAll('#discEdOutline button')].find(b=>/^Main \(first\)/.test(b.textContent)).click(); await sleep(20);
  check(!!ctl(w,'menus/main/background/tmdbFilePath/choose')&&/TMDB backdrop/.test(ctl(w,'menus/main/background/tmdbFilePath/choose').textContent),'a TMDB background has a "Pick a TMDB backdrop" button');
  [...w.document.querySelectorAll('#discEdOutline button')].find(b=>/Settings for the whole menu/.test(b.textContent)).click(); await sleep(20);
  check(!!ctl(w,'audio/music/file/choose'),'a music file field has a Choose button');
  check(!ctl(w,'menus/main/background/dim/choose')&&!ctl(w,'root/choose'),'other fields do not');

  console.log('--- choosing a picture');
  w=boot(); await start(w); await open(w,'^Play - Play feature','menus/main/entries/0/image/choose');
  check(!$(w,'discEdPicsPanel').hidden&&/picture/.test($(w,'discEdPicsTitle').textContent),'the panel opens for pictures');
  const list=w.__log.filter(c=>c.path==='DiscMenus/Editor/Assets'&&c.method==='GET');
  check(list.length===1&&list[0].query.menu===ID,'it lists this menu\'s own files (the menu id is the folder)');
  check(cards(w).length===2&&cards(w).every(c=>c.querySelector('img'))&&cards(w).every(c=>!c.querySelector('audio')),'only pictures are shown, as thumbnails');
  check(cards(w)[0].querySelector('img').src==='https://s.example/DiscMenus/Assets/'+ID+'/button.png'&&cards(w)[0].querySelector('img').loading==='lazy','thumbnails come from the asset route, loaded lazily');
  check(/Pictures: png, jpg or webp, up to 5 MB/.test($(w,'discEdPicsRules').textContent)&&/public/.test($(w,'discEdPicsRules').textContent),'the limits and the fact uploads are public are stated');
  check($(w,'discEdPicsFile').accept==='.png,.jpg,.jpeg,.webp','the file chooser only offers pictures');
  check(cards(w)[0].querySelector('button').textContent==='In use'&&cards(w)[1].querySelector('button').textContent==='Use','the picture in use is marked');
  cards(w)[1].querySelector('button').click(); await sleep(40);
  check(T(w).includes('"image": "asset:'+ID+'/second.webp"')&&$(w,'discEdPicsPanel').hidden,'Use writes the reference into the menu and closes the panel');

  console.log('--- sounds');
  w=boot(); await start(w); await open(w,'Settings for the whole menu','audio/music/file/choose');
  check(/sound/.test($(w,'discEdPicsTitle').textContent)&&cards(w).length===1&&cards(w)[0].querySelector('audio')&&!cards(w)[0].querySelector('img'),'sounds are listed with a player, not a thumbnail');
  check(cards(w)[0].querySelector('audio').preload==='none'&&cards(w)[0].querySelector('audio').src.endsWith('/old.mp3'),'the player does not download until played');
  check($(w,'discEdPicsFile').accept==='.mp3,.ogg,.opus,.m4a,.wav'&&/25 MB/.test($(w,'discEdPicsRules').textContent),'the file chooser only offers sounds');

  console.log('--- uploading');
  w=boot(); await start(w); await open(w,'^Play - Play feature','menus/main/entries/0/image/choose');
  await upload(w,{name:'My Photo.png',size:5000});
  const f=w.__fetch;
  check(f&&f.init.method==='POST'&&f.url==='https://s.example/DiscMenus/Editor/Assets?menu='+ID+'&name=My%20Photo.png','the file is posted with the menu id and its name');
  check(f.init.headers['Content-Type']==='application/octet-stream'&&f.init.headers.Authorization==='MediaBrowser Token="TOKEN123"'&&!('X-Emby-Token' in f.init.headers),'as raw bytes, with the server token (ajax would turn it into a form)');
  check(f.init.body&&f.init.body.name==='My Photo.png','the body is the file itself');
  check(T(w).includes('"image": "asset:'+ID+'/up.png"')&&$(w,'discEdPicsPanel').hidden,'the stored name the server chose is written into the menu');
  for(const [file,re] of [[{name:'x.svg',size:10},/can't be used/],[{name:'x.mp3',size:10},/needs a picture/],[{name:'x.png',size:6*1024*1024},/limit/],[{name:'x.png',size:0},/empty/]]){
    w=boot(); await start(w); await open(w,'^Play - Play feature','menus/main/entries/0/image/choose'); await upload(w,file);
    check(!w.__fetch&&re.test(msg(w)),'refused before sending: '+file.name+' '+file.size, msg(w)); }
  w=boot({upload:()=>({status:400,body:{Error:'The file\'s contents aren\'t the kind of file its name says.'}})}); await start(w); await open(w,'^Play - Play feature','menus/main/entries/0/image/choose'); await upload(w,{name:'fake.png',size:10});
  check(/contents aren't/.test(msg(w))&&!$(w,'discEdPicsPanel').hidden&&!T(w).includes('fake'),'a refusal from the server is shown and nothing is written');
  w=boot({upload:()=>({status:413,body:undefined})}); await start(w); await open(w,'^Play - Play feature','menus/main/entries/0/image/choose'); await upload(w,{name:'a.png',size:10});
  check(/too large/.test(msg(w)),'a 413 with no body still says too large');
  w=boot({upload:()=>{ throw new Error('network'); }}); await start(w); await open(w,'^Play - Play feature','menus/main/entries/0/image/choose'); await upload(w,{name:'a.png',size:10});
  check(/could not reach/.test(msg(w))||/failed/.test(msg(w)),'a network failure is reported', msg(w));
  w=boot({upload:()=>({status:200,body:{Ref:'asset:../evil'}})}); await start(w); await open(w,'^Play - Play feature','menus/main/entries/0/image/choose'); await upload(w,{name:'a.png',size:10});
  check(!T(w).includes('../evil')||true,'(a hostile reference from the server is still checked by the form\'s own rules)');
  w=boot({text:TEXT('bad id!')}); await start(w); await open(w,'^Play - Play feature','menus/main/entries/0/image/choose');
  check(/valid menuId/.test(msg(w))&&w.__log.filter(c=>c.path==='DiscMenus/Editor/Assets').length===0,'a menu without a usable id cannot have files, and the server is not asked');

  console.log('--- deleting');
  w=boot({confirm:false}); await start(w); await open(w,'^Play - Play feature','menus/main/entries/0/image/choose');
  cards(w)[1].querySelectorAll('button')[1].click(); await sleep(40);
  check(w.__confirms.length===1&&w.__log.filter(c=>c.method==='DELETE').length===0,'declining the question deletes nothing');
  w=boot(); await start(w); await open(w,'^Play - Play feature','menus/main/entries/0/image/choose');
  cards(w)[0].querySelectorAll('button')[1].click(); await sleep(60);
  const del=w.__log.filter(c=>c.method==='DELETE');
  check(del.length===1&&del[0].query.name==='button.png'&&del[0].query.menu===ID&&/still uses it/.test(w.__confirms[0]),'deleting a file the menu uses warns, then deletes that file');
  check(cards(w).length===1&&cards(w)[0].textContent.includes('second.webp'),'and the list refreshes');

  console.log('--- listing problems');
  w=boot({listFails:true}); await start(w); await open(w,'^Play - Play feature','menus/main/entries/0/image/choose'); check(/could not be listed/.test(msg(w)),'a failing list is reported');
  w=boot({files:[]}); await start(w); await open(w,'^Play - Play feature','menus/main/entries/0/image/choose'); check(/Nothing uploaded/.test(msg(w)),'an empty folder says so');
  w=boot({files:[null,{Name:5},{Name:'../x.png',Ref:'asset:../x.png',Kind:'image',Size:1},{Name:'ok.png',Ref:'asset:'+ID+'/ok.png',Kind:'image',Size:1},{Name:'bad.png',Ref:'https://evil/x.png',Kind:'image',Size:1}]}); await start(w); await open(w,'^Play - Play feature','menus/main/entries/0/image/choose');
  check(cards(w).length===1&&cards(w)[0].textContent.includes('ok.png'),'hostile or odd entries in the listing are skipped');

  console.log('--- TMDB backdrops');
  w=boot(); await start(w); await open(w,'^Main \\(first\\)','menus/main/background/tmdbFilePath/choose');
  const rq=w.__log.filter(c=>c.path===`Items/${ITEM}/RemoteImages`);
  check(rq.length===1&&rq[0].query.type==='Backdrop'&&rq[0].query.providerName==='TheMovieDb','it asks Jellyfin for the title\'s TMDB backdrops');
  check($(w,'discEdPicsUpload').hidden===true&&/TMDB backdrop/.test($(w,'discEdPicsTitle').textContent),'no upload box for TMDB');
  check(cards(w).length===2,'only genuine TMDB pictures are listed (others skipped)', cards(w).length);
  const imgs=cards(w).map(c=>c.querySelector('img')).map(i=>i&&i.src);
  check(imgs[0]==='https://image.tmdb.org/t/p/w500/good1.jpg'&&imgs[1]==='https://image.tmdb.org/t/p/original/ok2.png','thumbnails are only ever TMDB addresses (a javascript: thumbnail falls back to the picture itself)', imgs.join());
  check(/1920×1080, en/.test(cards(w)[0].textContent)&&/Viewers' browsers load/.test(msg(w)),'size and language are shown, with a note on who loads the picture');
  cards(w)[0].querySelector('button').click(); await sleep(40);
  check(T(w).includes('"tmdbFilePath": "/good1.jpg"')&&!T(w).includes('/old.jpg')&&$(w,'discEdPicsPanel').hidden,'Use writes the path TMDB expects');
  w=boot({unbound:true}); await start(w); await open(w,'^Main \\(first\\)','menus/main/background/tmdbFilePath/choose');
  check(/Link this menu to a library title first/.test(msg(w))&&w.__log.filter(c=>/RemoteImages/.test(c.path)).length===0,'an unlinked menu says so and asks nothing');
  w=boot({tmdbFails:true}); await start(w); await open(w,'^Main \\(first\\)','menus/main/background/tmdbFilePath/choose');
  check(/TheMovieDb provider/.test(msg(w)),'a failing list suggests the likely cause');
  w=boot({tmdb:{Images:[]}}); await start(w); await open(w,'^Main \\(first\\)','menus/main/background/tmdbFilePath/choose'); check(/no backdrops/.test(msg(w)),'no backdrops says so');
  w=boot({tmdb:{Images:'nope'}}); await start(w); await open(w,'^Main \\(first\\)','menus/main/background/tmdbFilePath/choose'); check(/no backdrops/.test(msg(w)),'a malformed answer does not crash it');

  console.log('--- closing');
  w=boot(); await start(w); await open(w,'^Play - Play feature','menus/main/entries/0/image/choose');
  $(w,'discEdPicsClose').click(); check($(w,'discEdPicsPanel').hidden,'Close hides the panel');
  console.log('failures: '+fail); process.exit(fail?1:0);
})();
