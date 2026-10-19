// TMDB backgrounds, a different background per page, preloading and the background crossfade.
// Runs the real browser code in a simulated browser (jsdom) with scripted server responses.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const src=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/discmenus.js'),'utf8');
let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e===undefined?'':e); };
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const PARENT='4cf4efb9cb2c96b4ec157568bcccdf7f';
function boot(doc,opts={}){
  const dom=new JSDOM('<body></body>',{url:'https://s.example/web/#/details?id='+PARENT,runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window; w.console.warn=()=>{}; w.console.info=()=>{};
  w.__anims=[]; w.__images=[];
  w.Image=function(){ const o={}; Object.defineProperty(o,'src',{set(v){ w.__images.push(v); },get(){return '';}}); return o; };
  w.matchMedia=()=>({matches:!!opts.reducedMotion});
  w.Element.prototype.animate=function(frames,o){ const a={el:this,frames,opts:o,onfinish:null}; w.__anims.push(a); return a; };
  w.ApiClient={getJSON:()=>Promise.resolve(doc),getUrl:u=>'https://s.example/'+u,getImageUrl:(id,o)=>'https://s.example/Items/'+id+'/Images/'+o.type+'/'+o.index,deviceId:()=>'d',accessToken:()=>'T'};
  w.eval(src); return w;
}
const bg=(path,extra)=>Object.assign({Source:'tmdb',TmdbFilePath:path,Dim:0.2},extra||{});
const mk=(rootBg,menuBgs,layout)=>({Root:'main',Background:rootBg,Layout:Object.assign({ButtonStyle:'text'},layout||{}),Menus:{
  main:{Title:'Main',Background:menuBgs&&menuBgs.main,Entries:[{Action:'submenu',Label:'Features',Menu:'features'}]},
  features:{Title:'F',Background:menuBgs&&menuBgs.features,Entries:[{Action:'back',Label:'Back'}]}}});
const open=async(w)=>{ await sleep(60); w.document.getElementById('discMenusButton').click(); };
const click=(w,l)=>{ const b=[...w.document.querySelectorAll('.discMenuEntry')].find(x=>x.getAttribute('aria-label')===l); b.click(); };
const bgOf=w=>w.document.getElementById('discMenusOverlay').getAttribute('data-bg')||'';
(async()=>{
  console.log('--- TMDB source');
  let w=boot(mk(bg('/6YozDnrA2fXdIbB5nWDFHtB5ZY6.jpg'))); await open(w);
  check(bgOf(w).includes('url(https://image.tmdb.org/t/p/w1280/6YozDnrA2fXdIbB5nWDFHtB5ZY6.jpg)'),'tmdb background uses TMDB\'s image server at the default w1280 size', bgOf(w).slice(0,160));
  check(/rgba\(0,0,0,0\.2\)/.test(bgOf(w))&&bgOf(w).includes('background-size:cover'),'dim overlay and cover sizing applied');
  w=boot(mk(bg('/a.jpg',{TmdbSize:'original'}))); await open(w); check(bgOf(w).includes('/t/p/original/a.jpg'),'tmdbSize original');
  w=boot(mk(bg('/a.png',{TmdbSize:'w780'}))); await open(w); check(bgOf(w).includes('/t/p/w780/a.png'),'tmdbSize w780 (png)');
  w=boot(mk(bg('/a.jpg',{TmdbSize:'w99999'}))); await open(w); check(bgOf(w).includes('/t/p/w1280/a.jpg'),'unknown size falls back to w1280');
  for(const bad of ['/a.svg','/../etc/passwd.jpg','https://evil.example/a.jpg','a.jpg','/a/b.jpg','/a.jpg?x=1','/a.jpg")x:url(',undefined,'']){
    w=boot(mk(bg(bad))); await open(w);
    check(!bgOf(w).includes('http')&&bgOf(w).includes('#101010'),'bad tmdb path refused, dark fallback: '+JSON.stringify(bad));
  }
  console.log('--- different background per page');
  w=boot(mk(bg('/doc.jpg'),{main:bg('/main.jpg'),features:bg('/features.jpg',{Dim:0.5})})); await open(w);
  check(bgOf(w).includes('/main.jpg'),'main menu shows its own backdrop');
  click(w,'Features'); check(bgOf(w).includes('/features.jpg')&&/rgba\(0,0,0,0\.5\)/.test(bgOf(w)),'Special Features shows a different backdrop, with its own dim');
  click(w,'Back'); check(bgOf(w).includes('/main.jpg'),'going back restores the main backdrop');
  w=boot(mk(bg('/doc.jpg'),{features:bg('/features.jpg')})); await open(w);
  check(bgOf(w).includes('/doc.jpg'),'a menu with no background of its own inherits the document one');
  console.log('--- preloading');
  w=boot(mk(bg('/doc.jpg'),{main:bg('/main.jpg'),features:bg('/features.jpg')})); await open(w);
  check(w.__images.length===3&&['/doc.jpg','/main.jpg','/features.jpg'].every(p=>w.__images.some(u=>u.endsWith(p))),'every page\'s background is preloaded when the menu opens', w.__images.map(u=>u.split('/').pop()).join(','));
  w=boot(mk(bg('/same.jpg'),{main:bg('/same.jpg'),features:bg('/same.jpg')})); await open(w);
  check(w.__images.length===1,'the same picture used by several pages is preloaded once');
  w=boot(mk({Source:'jellyfin',ImageType:'Backdrop',Index:2,Dim:0.3},{features:{Source:'image',Image:'asset:x/y.webp'},main:bg('/../bad.jpg')})); await open(w);
  check(w.__images.some(u=>u.endsWith('/Images/Backdrop/2'))&&w.__images.some(u=>u.endsWith('/DiscMenus/Assets/x/y.webp'))&&!w.__images.some(u=>u.includes('bad')),'preloads jellyfin and asset backgrounds too, never a refused one', w.__images.length);
  console.log('--- crossfade between pages');
  const FADE={Transition:{Style:'fade',DurationMs:300}};
  w=boot(mk(bg('/doc.jpg'),{features:bg('/features.jpg')},FADE)); await open(w); w.__anims.length=0; click(w,'Features');
  let ghost=w.document.querySelector('.discMenusBgFade');
  check(!!ghost,'changing background adds a ghost layer');
  const ga=w.__anims.find(a=>a.el===ghost);
  check(!!ga&&ga.frames[0].opacity===1&&ga.frames[1].opacity===0&&ga.opts.duration===300,'the old picture fades out over the new one (300ms crossfade)');
  check(w.document.getElementById('discMenusOverlay').firstChild===ghost,'the ghost sits behind the buttons and banner');
  ga.onfinish(); check(!w.document.querySelector('.discMenusBgFade'),'ghost layer is removed when the fade ends');
  w=boot(mk(bg('/same.jpg'),{},FADE)); await open(w); w.__anims.length=0; click(w,'Features');
  check(!w.document.querySelector('.discMenusBgFade'),'same background on both pages: no crossfade');
  w=boot(mk(bg('/doc.jpg'),{features:bg('/features.jpg')},FADE),{reducedMotion:true}); await open(w); click(w,'Features');
  check(!w.document.querySelector('.discMenusBgFade')&&bgOf(w).includes('/features.jpg'),'reduced motion: background switches instantly, no fade');
  w=boot(mk(bg('/doc.jpg'),{features:bg('/features.jpg')})); await open(w); click(w,'Features');
  check(!w.document.querySelector('.discMenusBgFade')&&bgOf(w).includes('/features.jpg'),'no transition configured: instant switch');
  w=boot(mk(bg('/doc.jpg'),{features:bg('/features.jpg')},FADE)); await open(w); click(w,'Features'); click(w,'Back'); click(w,'Features');
  check(w.document.querySelectorAll('.discMenusBgFade').length<=1,'rapid page changes do not pile up ghost layers', w.document.querySelectorAll('.discMenusBgFade').length);
  console.log('failures:',fail); process.exit(0);
})();
