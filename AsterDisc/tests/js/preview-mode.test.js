// The renderer in preview mode (used by the editor): never plays or alerts, cannot close, keeps its place across edits, starts muted.
// Runs the real browser code in a simulated browser (jsdom) with scripted server responses.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const src=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/discmenus.js'),'utf8');
let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e===undefined?'':e); };
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const PARENT='4cf4efb9cb2c96b4ec157568bcccdf7f';
function boot(opts={}){
  const dom=new JSDOM('<body></body>',{url:'https://s.example/DiscMenus/web/preview.html',runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window; w.console.warn=()=>{}; w.console.info=()=>{}; w.console.log=()=>{};
  w.__msgs=[]; w.__calls=[]; w.__audios=[]; w.__alerts=[]; w.__tones=[];
  Object.defineProperty(w,'parent',{value:{postMessage:(m,o)=>w.__msgs.push([m,o])},configurable:true});
  w.alert=(m)=>w.__alerts.push(m);
  w.Audio=function(u){ const a={src:u||'',volume:1,loop:false,paused:true,play(){a.paused=false;return Promise.resolve();},pause(){a.paused=true;},removeAttribute(){}}; w.__audios.push(a); return a; };
  w.AudioContext=function(){ return {state:'running',currentTime:0,destination:{},resume(){},createOscillator(){const o={type:'',frequency:{value:0},connect(){},start(){},stop(){}};w.__tones.push(o);return o;},createGain(){return {gain:{value:1,setValueAtTime(){},exponentialRampToValueAtTime(){}},connect(){}};}}; };
  w.matchMedia=()=>({matches:true}); // reduced motion: keep the tests synchronous
  w.ApiClient={getJSON:(u)=>{w.__calls.push('getJSON '+u);return Promise.resolve([]);},ajax:(o)=>{w.__calls.push('ajax '+o.url);return Promise.resolve();},getUrl:(u,p)=>'https://s.example/'+u+(p?'?'+Object.entries(p).filter(([,v])=>v!==undefined).map(([k,v])=>k+'='+v).join('&'):''),getImageUrl:()=>'x',deviceId:()=>'d',accessToken:()=>'T'};
  if(opts.preview!==false) w.__discMenusPreview=true;
  w.eval(src); return w;
}
const ch=(n)=>Array.from({length:n},(_,i)=>({Index:i,Name:'Chapter '+(i+1),StartTicks:i*600000000,HasImage:false}));
const mkdoc=(over)=>Object.assign({Root:'main',Layout:{ButtonStyle:'text'},Chapters:ch(8),Menus:{
  main:{Title:'Main',Entries:[{Action:'playFeature',Label:'Play Movie'},{Action:'chapters',Label:'Scenes',PerPage:4},{Action:'submenu',Label:'Features',Menu:'features'}]},
  features:{Title:'Special Features',Entries:[{Action:'playExtra',Label:'Making Of',ItemId:'EXTRA1'},{Action:'playExtra',Label:'Unlinked'},{Action:'back',Label:'Back'}]}}},over||{});
const names=w=>[...w.document.querySelectorAll('.discMenuEntry')].map(b=>b.getAttribute('aria-label'));
const click=(w,l)=>{ const b=[...w.document.querySelectorAll('.discMenuEntry')].find(x=>x.getAttribute('aria-label')===l); if(!b) throw new Error('no '+l+' in '+names(w)); b.click(); };
const last=(w,type)=>[...w.__msgs].reverse().map(m=>m[0]).find(m=>m.type===type);
const key=(w,k)=>w.dispatchEvent(new w.KeyboardEvent('keydown',{key:k,bubbles:true,cancelable:true}));
(async()=>{
  console.log('--- starting up in preview mode');
  let w=boot(); await sleep(60);
  check(typeof w.DiscMenusPreview==='object'&&['show','update','goTo','setMuted'].every(k=>typeof w.DiscMenusPreview[k]==='function'),'exposes show/update/goTo/setMuted');
  check(!w.document.getElementById('discMenusButton')&&w.__calls.length===0,'no launcher button, no fetching, no watching the page');
  check(last(w,'ready')&&w.__msgs[0][1]==='https://s.example','tells the editor it is ready, addressed to its own origin only', JSON.stringify(w.__msgs[0]));
  w=boot({preview:false}); await sleep(60);
  check(w.DiscMenusPreview===undefined,'normal mode: no preview API is exposed');
  // and in normal mode the page watcher IS active: navigating to an item page asks for its menu
  w.location.hash='#/details?id='+PARENT; await sleep(50); check(w.__calls.some(c=>c.includes('DiscMenus/'+PARENT+'/Menu')),'normal mode still watches the page and fetches the item\'s menu', w.__calls.join());

  console.log('--- showing a menu');
  w=boot(); w.DiscMenusPreview.show(mkdoc(),PARENT);
  check(names(w).join()==='Play Movie,Scenes,Features','show() draws the menu', names(w).join());
  check(last(w,'navigate').menu==='main','reports which menu is showing');
  click(w,'Features'); check(last(w,'navigate').menu==='features','navigating inside the preview is reported to the editor');
  check(names(w).join()==='Making Of,Unlinked,Back','submenu works inside the preview');

  console.log('--- never touches the real session');
  click(w,'Making Of');
  const play=last(w,'play'); check(play&&play.itemIds.join()==='EXTRA1','playing an extra only reports what it would play', JSON.stringify(play));
  click(w,'Unlinked'); check(last(w,'message')&&/linked/i.test(last(w,'message').text)&&w.__alerts.length===0,'"not linked" goes to the editor, never a native alert', last(w,'message')&&last(w,'message').text);
  click(w,'Back'); click(w,'Play Movie'); check(last(w,'play').itemIds.join()===PARENT&&last(w,'play').startTicks===0,'Play Movie reports the feature');
  click(w,'Scenes'); click(w,'Chapter 3');
  check(last(w,'play').itemIds.join()===PARENT&&last(w,'play').startTicks===1200000000,'a chapter reports its start time', JSON.stringify(last(w,'play')));
  check(!w.__calls.some(c=>/Sessions|Playing/.test(c)),'no Sessions or Playing request was ever made', w.__calls.join());
  check(!!w.document.getElementById('discMenusOverlay'),'the preview stays up after every action');

  console.log('--- cannot close itself');
  w=boot(); w.DiscMenusPreview.show(mkdoc(),PARENT); click(w,'Features'); key(w,'Escape');
  check(names(w).join()==='Play Movie,Scenes,Features','Esc steps back like a real menu');
  key(w,'Escape'); check(!!w.document.getElementById('discMenusOverlay')&&names(w).join()==='Play Movie,Scenes,Features','Esc at the root does not blank the preview');
  w.document.querySelector('#discMenusOverlay button').click(); check(!!w.document.getElementById('discMenusOverlay')&&names(w).length===3,'the X button returns to the main menu instead of closing');
  click(w,'Features'); w.document.querySelector('#discMenusOverlay button').click(); check(names(w).join()==='Play Movie,Scenes,Features','the X from a submenu returns to the main menu');

  console.log('--- live updates keep your place');
  w=boot(); w.DiscMenusPreview.show(mkdoc(),PARENT); click(w,'Features');
  const edited=mkdoc(); edited.Menus.features.Entries[0].Label='Making Of (edited)';
  w.DiscMenusPreview.update(edited);
  check(names(w).join()==='Making Of (edited),Unlinked,Back','update() shows the edit in the same menu you were viewing', names(w).join());
  check(w.document.querySelectorAll('#discMenusOverlay').length===1,'one overlay, not a new one per edit');
  const gone=mkdoc(); delete gone.Menus.features; gone.Menus.main.Entries=gone.Menus.main.Entries.slice(0,2);
  w.DiscMenusPreview.update(gone); check(names(w).join()==='Play Movie,Scenes','if the viewed menu was deleted, falls back to the root', names(w).join());
  w=boot(); w.DiscMenusPreview.show(mkdoc(),PARENT); click(w,'Scenes'); w.DiscMenusPreview.update(mkdoc());
  check(names(w).join()==='Play Movie,Scenes,Features','a generated scene screen is dropped back to its parent on update');
  w=boot(); w.DiscMenusPreview.update(mkdoc(),PARENT); check(names(w).length===3,'update() before show() just shows it');
  const newRoot=mkdoc(); newRoot.Root='features'; w=boot(); w.DiscMenusPreview.show(mkdoc(),PARENT); click(w,'Features'); w.DiscMenusPreview.update(newRoot); check(names(w).join()==='Making Of,Unlinked,Back','changing the root menu re-roots the preview');
  for(const bad of [null,undefined,{},{Root:'x',Menus:{}},{Root:'main'},'string',42]){ w=boot(); w.DiscMenusPreview.show(mkdoc(),PARENT); let ok=true; try{ w.DiscMenusPreview.update(bad); w.DiscMenusPreview.show(bad);}catch(e){ok=false;} check(ok&&names(w).length===3,'ignores an unusable document without breaking: '+JSON.stringify(bad)); }

  console.log('--- jumping to a menu');
  w=boot(); w.DiscMenusPreview.show(mkdoc(),PARENT); w.DiscMenusPreview.goTo('features'); check(names(w).join()==='Making Of,Unlinked,Back','goTo() shows that menu');
  w.DiscMenusPreview.goTo('nope'); check(names(w).join()==='Making Of,Unlinked,Back','goTo() an unknown menu is ignored');
  w.DiscMenusPreview.goTo('main'); check(names(w).join()==='Play Movie,Scenes,Features','goTo() the root');
  w.DiscMenusPreview.goTo('features'); click(w,'Back'); check(names(w).join()==='Play Movie,Scenes,Features','Back after a jump returns to the root');

  console.log('--- muted by default');
  const loud=mkdoc({Audio:{Music:{Source:'file',File:'asset:a/b.mp3',Volume:0.5},Sounds:{Preset:'click'}},Background:{Source:'trailer',Muted:false},Trailers:[{Kind:'youtube',VideoId:'ue80QwXMRHg'}]});
  w=boot(); w.DiscMenusPreview.show(loud,PARENT);
  check(w.__audios.length===0,'music does not play in a fresh preview');
  key(w,'ArrowDown'); click(w,'Features'); check(w.__tones.length===0,'button sounds are silent too');
  const frame=w.document.querySelector('#discMenusVideo iframe'); check(frame&&new URL(frame.src).searchParams.get('mute')==='1','a trailer is forced muted even if the menu asks for sound', frame&&frame.src.split('?')[1].slice(0,40));
  w.DiscMenusPreview.setMuted(false); check(w.__audios.length===1&&w.__audios[0].src.endsWith('/a/b.mp3'),'unmuting starts the music');
  key(w,'ArrowUp'); await sleep(60); key(w,'ArrowDown'); check(w.__tones.length>0,'...and the button sounds');
  check(new URL(w.document.querySelector('#discMenusVideo iframe').src).searchParams.get('mute')==='0','...and the trailer\'s own audio');
  w.DiscMenusPreview.setMuted(true); await sleep(900); check(w.__audios[0].paused,'muting again stops the music');
  console.log('failures:',fail); process.exit(0);
})();
