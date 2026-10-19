// fanart.tv backgrounds: the renderer asks THIS server for the picture (GET DiscMenus/Fanart/{item}/{id}) and never contacts fanart.tv;
// bad ids and unknown items fall back to the plain dark background.
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
  w.__images=[]; w.__fetches=[];
  w.Image=function(){ const o={}; Object.defineProperty(o,'src',{set(v){ w.__images.push(v); },get(){return '';}}); return o; };
  w.fetch=(u)=>{ w.__fetches.push(String(u)); return Promise.resolve({ok:false}); };
  w.matchMedia=()=>({matches:true});
  w.ApiClient={getJSON:()=>Promise.resolve(doc),getUrl:u=>'https://s.example/'+u,getImageUrl:()=>'x',deviceId:()=>'d',accessToken:()=>'T'};
  if(opts.preview) w.__discMenusPreview=true;
  w.eval(src); return w;
}
const fan=(id,extra)=>Object.assign({Source:'fanart',FanartId:id},extra||{});
const mk=(rootBg,menuBgs)=>({Root:'main',Background:rootBg,Layout:{ButtonStyle:'text'},Menus:{
  main:{Title:'Main',Background:menuBgs&&menuBgs.main,Entries:[{Action:'submenu',Label:'Features',Menu:'features'}]},
  features:{Title:'F',Background:menuBgs&&menuBgs.features,Entries:[{Action:'back',Label:'Back'}]}}});
const open=async(w)=>{ await sleep(60); w.document.getElementById('discMenusButton').click(); };
const click=(w,l)=>{ const b=[...w.document.querySelectorAll('.discMenuEntry')].find(x=>x.getAttribute('aria-label')===l); b.click(); };
const bgOf=w=>w.document.getElementById('discMenusOverlay').getAttribute('data-bg')||'';
(async()=>{
  console.log('--- the picture comes from this server');
  let w=boot(mk(fan('47835'))); await open(w);
  const URL1='https://s.example/DiscMenus/Fanart/'+PARENT+'/47835';
  check(bgOf(w).includes('url('+URL1+')'),'a fanart background asks this server for the picture', bgOf(w).slice(0,170));
  check(/rgba\(0,0,0,0\.4\)/.test(bgOf(w))&&bgOf(w).includes('background-size:cover')&&bgOf(w).includes('#101010'),'default dim 0.4, cover sizing, dark base');
  check(!/fanart\.tv/.test(bgOf(w)),'nothing points at fanart.tv');
  w=boot(mk(fan('47835',{Dim:0.7}))); await open(w); check(/rgba\(0,0,0,0\.7\)/.test(bgOf(w)),'the menu\'s dim is used');
  w=boot(mk(fan('999999999999'))); await open(w); check(bgOf(w).includes('/Fanart/'+PARENT+'/999999999999)'),'a 12-digit id is accepted');

  console.log('--- bad ids fall back to the plain dark background');
  for(const bad of ['','abc','1234567890123','-1','1/2','1.5','1)x:url(','1 ',' 1','../1','١٢٣',undefined,null,5,{},['1']]){
    w=boot(mk(fan(bad))); await open(w);
    check(!bgOf(w).includes('url(')&&bgOf(w).includes('#101010'),'refused: '+JSON.stringify(bad), bgOf(w).slice(0,80));
  }

  console.log('--- preloading and a picture per page');
  w=boot(mk(fan('1'),{main:fan('2'),features:fan('3')})); await open(w);
  check(w.__images.includes('https://s.example/DiscMenus/Fanart/'+PARENT+'/1')&&w.__images.includes('https://s.example/DiscMenus/Fanart/'+PARENT+'/2')&&w.__images.includes('https://s.example/DiscMenus/Fanart/'+PARENT+'/3'),'every page\'s fanart picture is preloaded', JSON.stringify(w.__images));
  check(bgOf(w).includes('/Fanart/'+PARENT+'/2)'),'the main page shows its own picture');
  click(w,'Features'); check(bgOf(w).includes('/Fanart/'+PARENT+'/3)'),'the next page shows its own picture');
  check(w.__fetches.every(u=>!/fanart\.tv/.test(u)),'the renderer never fetches from fanart.tv itself');

  console.log('--- an unknown item (the editor preview of an unsaved menu) shows plain dark, and never builds a bad path');
  w=boot(mk(fan('47835')),{preview:true}); await sleep(30);
  w.DiscMenusPreview.show(mk(fan('47835')),'preview'); await sleep(30);
  check(!bgOf(w).includes('url(')&&bgOf(w).includes('#101010'),'parent "preview" is not an item id: plain dark', bgOf(w).slice(0,80));
  w.DiscMenusPreview.show(mk(fan('47835')),'../../etc'); await sleep(30);
  check(!bgOf(w).includes('url('),'a path-like parent id is refused');
  w.DiscMenusPreview.show(mk(fan('47835')),'0b311cdd-e7c2-4e27-b4bc-fb1e5acebfc8'); await sleep(30);
  check(bgOf(w).includes('/Fanart/0b311cdd-e7c2-4e27-b4bc-fb1e5acebfc8/47835)'),'a real item id (with hyphens) is accepted');

  console.log('failures:',fail); process.exit(fail?1:0);
})();
