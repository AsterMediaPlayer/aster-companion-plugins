// Music, button sounds, theme songs and every transition style and direction.
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
  // ---- stubs for browser audio / animation APIs, recording what the code asks for
  w.__audios=[]; w.__tones=[]; w.__anims=[]; w.__fetches=[]; w.__buffers=0;
  w.Audio=function(url){ const a={src:url||'',volume:1,loop:false,paused:true,preload:'',played:0,
      play(){ a.paused=false; a.played++; return Promise.resolve(); }, pause(){ a.paused=true; }, removeAttribute(n){ if(n==='src') a.src=''; } };
    w.__audios.push(a); return a; };
  w.AudioContext=function(){ return { state:'running', currentTime:0, destination:{}, resume(){},
    createOscillator(){ const o={type:'',frequency:{value:0},connect(){},start(){},stop(){}}; w.__tones.push(o); return o; },
    createGain(){ return {gain:{value:1,setValueAtTime(){},exponentialRampToValueAtTime(){}},connect(){}}; },
    createBufferSource(){ return {connect(){},start(){ w.__buffers++; }}; },
    decodeAudioData(){ return Promise.resolve({fake:'buffer'}); } }; };
  w.fetch=(u)=>{ w.__fetches.push(u); return Promise.resolve({arrayBuffer:()=>Promise.resolve(new ArrayBuffer(8))}); };
  w.matchMedia=()=>({matches:!!opts.reducedMotion});
  if(opts.animate!==false) w.Element.prototype.animate=function(frames,o){ const a={el:this,frames,opts:o,onfinish:null}; w.__anims.push(a); return a; };
  w.ApiClient={getJSON:()=>Promise.resolve(doc),getUrl:(u,p)=>'https://s.example/'+u+(p?'?'+Object.entries(p).filter(([,v])=>v!==undefined).map(([k,v])=>k+'='+v).join('&'):''),getImageUrl:()=>'x',deviceId:()=>'d',accessToken:()=>'TOK'};
  w.eval(src); return w;
}
const mk=(extra,menus)=>Object.assign({Root:'main',Layout:{ButtonStyle:'text'},Menus:Object.assign({
  main:{Title:'Main',Entries:[{Action:'submenu',Label:'Features',Menu:'features'},{Action:'playFeature',Label:'Play'}]},
  features:{Title:'F',Entries:[{Action:'back',Label:'Back'}]}},menus||{})},extra);
const open=async(w)=>{ await sleep(60); w.document.getElementById('discMenusButton').click(); };
const click=(w,l)=>{ const b=[...w.document.querySelectorAll('.discMenuEntry')].find(x=>x.getAttribute('aria-label')===l); if(!b) throw new Error('no '+l); b.click(); };
const key=(w,k)=>w.dispatchEvent(new w.KeyboardEvent('keydown',{key:k,bubbles:true,cancelable:true}));
const MUSIC=(file,vol)=>({Music:{Source:'file',File:file,Volume:vol}});
(async()=>{
  console.log('--- AUDIO: opt-in');
  let w=boot(mk({})); await open(w); key(w,'ArrowDown'); click(w,'Features');
  check(w.__audios.length===0&&w.__tones.length===0,'no audio config: nothing plays, no sounds, no audio objects created');

  console.log('--- AUDIO: music');
  w=boot(mk({Audio:MUSIC('asset:local-art/ambient.mp3',0.4)})); await open(w);
  let a=w.__audios[0];
  check(w.__audios.length===1&&a.src==='https://s.example/DiscMenus/Assets/local-art/ambient.mp3'&&a.loop&&a.played===1,'asset music: one looping <audio>, started, served by the assets endpoint', a&&a.src);
  check(a.volume===0,'starts silent and fades in (no abrupt start)');
  await sleep(1100); check(Math.abs(a.volume-0.4)<0.02,'fades up to the requested volume 0.4', a.volume.toFixed(2));
  click(w,'Features'); check(w.__audios.length===1&&w.__audios[0]===a&&!a.paused,'music keeps playing (same element) across menus using the same track');
  // menu-level override: different track crossfades
  w=boot(mk({Audio:MUSIC('asset:x/a.mp3',0.5)},{features:{Title:'F',Audio:MUSIC('asset:x/b.mp3',0.5),Entries:[{Action:'back',Label:'Back'}]}})); await open(w);
  const first=w.__audios[0]; click(w,'Features'); await sleep(50);
  check(w.__audios.length===2&&w.__audios[1].src.endsWith('/x/b.mp3'),'a menu with its own music starts that track', w.__audios.map(x=>x.src.split('/').pop()).join(' -> '));
  await sleep(900); check(first.paused&&w.__audios[1].volume>0.4,'...while the previous track fades out and stops');
  // menu-level none silences
  w=boot(mk({Audio:MUSIC('asset:x/a.mp3',0.5)},{features:{Title:'F',Audio:{Music:{Source:'none'}},Entries:[{Action:'back',Label:'Back'}]}})); await open(w);
  const m1=w.__audios[0]; click(w,'Features'); await sleep(800);
  check(m1.paused,'source "none" on a menu silences the default music');
  click(w,'Back'); await sleep(50); check(w.__audios.length===2&&w.__audios[1].src.endsWith('/x/a.mp3'),'returning to a menu without that override restarts the default music');
  // closing stops it
  w=boot(mk({Audio:MUSIC('asset:x/a.mp3',0.5)})); await open(w); const m2=w.__audios[0]; await sleep(300);
  w.document.querySelector('#discMenusOverlay button').click(); await sleep(700);
  check(m2.paused,'closing the menu stops the music');
  // theme song
  w=boot(mk({Audio:{Music:{Source:'themeSong'}},ThemeSongs:['abc123']})); await open(w);
  check(w.__audios.length===1&&w.__audios[0].src==='https://s.example/Audio/abc123/stream?static=true&api_key=TOK','themeSong plays the item\'s own Jellyfin theme song', w.__audios[0]&&w.__audios[0].src);
  w=boot(mk({Audio:{Music:{Source:'themeSong'}},ThemeSongs:[]})); await open(w);
  check(w.__audios.length===0,'themeSong with no theme song available: silent, no error');
  // hostile refs never reach an <audio>
  for (const bad of ['http://e.com/a.mp3','/mnt/a.mp3','asset:../a.mp3','asset:x/a.exe','https://e.com/a.mp3")x:url(','data:audio/mp3;base64,AA','javascript:alert(1)']) {
    w=boot(mk({Audio:MUSIC(bad,0.5)})); await open(w);
    check(w.__audios.length===0,'hostile music ref refused: '+bad);
  }

  console.log('--- AUDIO: button sounds');
  w=boot(mk({Audio:{Sounds:{Preset:'click',Volume:0.5}}})); await open(w);
  key(w,'ArrowDown'); check(w.__tones.length===1&&w.__tones[0].type==='square'&&w.__tones[0].frequency.value===1500,'keyboard move plays the preset "move" tick', w.__tones.length);
  key(w,'ArrowUp'); check(w.__tones.length===1,'moves within 45ms are throttled (key repeat does not machine-gun)');
  await sleep(60); key(w,'ArrowUp'); check(w.__tones.length===2,'...but a later move sounds again');
  let n=w.__tones.length; click(w,'Features'); check(w.__tones.length===n+2,'activating a button plays the two-tone "select"', (w.__tones.length-n)+' tones');
  n=w.__tones.length; click(w,'Back'); check(w.__tones.length===n+1&&w.__tones[n].frequency.value===600,'Back button plays the "back" sound');
  await sleep(60); n=w.__tones.length; click(w,'Features'); n=w.__tones.length; await sleep(60); key(w,'Escape'); check(w.__tones.length===n+1&&w.__tones[n].frequency.value===600,'Back KEY plays the "back" sound once');
  await sleep(60); n=w.__tones.length; const btn=[...w.document.querySelectorAll('.discMenuEntry')][1]; w.document.activeElement.blur();
  btn.dispatchEvent(new w.MouseEvent('mousemove',{bubbles:true,screenX:5,screenY:9})); check(w.__tones.length===n+1,'hover (real pointer movement) plays "move"');
  w=boot(mk({Audio:{Sounds:{Preset:'none'}}})); await open(w); key(w,'ArrowDown'); click(w,'Features'); check(w.__tones.length===0,'preset "none": silent');
  w=boot(mk({Audio:{Sounds:{Preset:'chime'}}},{features:{Title:'F',Audio:{Sounds:{Preset:'beep'}},Entries:[{Action:'back',Label:'Back'}]}})); await open(w);
  click(w,'Features'); await sleep(60); n=w.__tones.length; click(w,'Back'); check(w.__tones[n].frequency.value===520,'a menu\'s own sounds replace the default sounds (beep "back" = 520Hz)', w.__tones[n]&&w.__tones[n].frequency.value);
  // sound files: decoded once, then replayed from memory
  w=boot(mk({Audio:{Sounds:{Select:'asset:s/ok.wav',Volume:0.7}}})); await open(w);
  click(w,'Features'); await sleep(40); check(w.__fetches.length===1&&w.__fetches[0]==='https://s.example/DiscMenus/Assets/s/ok.wav'&&w.__buffers===1,'sound file: fetched, decoded and played via WebAudio', w.__fetches.join());
  click(w,'Back'); await sleep(20); click(w,'Features'); await sleep(40); check(w.__fetches.length===1&&w.__buffers>=2,'...second play comes from the decoded buffer (no refetch)');
  w=boot(mk({Audio:{Sounds:{Select:'http://e.com/x.wav'}}})); await open(w); click(w,'Features'); await sleep(30);
  check(w.__fetches.length===0&&w.__audios.length===0&&w.__tones.length===0,'hostile sound ref refused');

  console.log('--- TRANSITIONS');
  w=boot(mk({})); await open(w); click(w,'Features');
  check(w.__anims.length===0&&w.document.querySelectorAll('.discMenusScreen').length===1,'no transition configured: instant swap, no animation, no leftover screen');
  const lay=(t)=>({Layout:{ButtonStyle:'text',Transition:t}});
  w=boot(mk(lay({Style:'fade',DurationMs:300}))); await open(w);
  check(w.__anims.length===1&&w.__anims[0].frames[0].opacity===0&&w.__anims[0].opts.duration===300,'menu opening fades the first screen in (intro)');
  w.__anims.length=0; click(w,'Features');
  const ins=w.__anims.filter(x=>x.frames[0].opacity===0), outs=w.__anims.filter(x=>x.frames[0].opacity===1);
  check(w.__anims.length===2&&ins.length===1&&outs.length===1&&w.__anims.every(x=>x.opts.duration===300),'submenu: old screen animates out and new one in, 300ms each');
  const screens=[...w.document.querySelectorAll('.discMenusScreen')]; const old=screens.find(s=>s.classList.contains('leaving'));
  check(screens.length===2&&!!old&&old.getAttribute('aria-hidden')==='true'&&old.style.pointerEvents==='none','old screen is marked leaving, hidden from assistive tech, not clickable');
  check(old.querySelectorAll('.discMenuEntry').length===0&&[...old.querySelectorAll('button')].every(b=>b.disabled),'old screen buttons are disabled and not focus targets');
  check(w.document.querySelectorAll('.discMenuEntry').length===1&&w.document.activeElement.getAttribute('aria-label')==='Back','keyboard navigation only sees the new screen, focus is on it');
  outs[0].onfinish&&outs[0].onfinish(); check(w.document.querySelectorAll('.discMenusScreen').length===1,'old screen is removed when its exit animation finishes');
  // rapid change: never more than one leaving screen
  w.__anims.length=0; click(w,'Back'); click(w,'Features'); click(w,'Back');
  check(w.document.querySelectorAll('.discMenusScreen.leaving').length<=1,'rapid navigation does not pile up ghost screens', w.document.querySelectorAll('.discMenusScreen.leaving').length);
  // directions
  w=boot(mk(lay({Style:'slide',DurationMs:200}))); await open(w); w.__anims.length=0;
  click(w,'Features'); const fwdIn=w.__anims.find(x=>x.frames[0].opacity===0); check(fwdIn.frames[0].transform==='translateX(8%)','slide forward: new screen enters from the right (+8%)', fwdIn.frames[0].transform);
  w.__anims.length=0; click(w,'Back'); const backIn=w.__anims.find(x=>x.frames[0].opacity===0); check(backIn.frames[0].transform==='translateX(-8%)','slide back: new screen enters from the left (-8%)', backIn.frames[0].transform);
  for (const [st,probe] of [['rise',f=>f[0].transform.startsWith('translateY')],['zoom',f=>f[0].transform.startsWith('scale')],['wipe',f=>!!f[0].clipPath]]) {
    w=boot(mk(lay({Style:st,DurationMs:150}))); await open(w); w.__anims.length=0; click(w,'Features');
    const inn=w.__anims.find(x=>x.frames[1]&&(x.frames[1].opacity===1||x.frames[1].clipPath)&&probe(x.frames)); check(!!inn,'style "'+st+'" produces its own keyframes');
  }
  // paging directions
  const pages=mk({Layout:{ButtonStyle:'text',Transition:{Style:'slide',DurationMs:200},Flow:{Region:{X:50,Y:50,W:80,H:20,Anchor:'center'},Columns:4,Rows:1}}},{main:{Title:'M',Entries:[1,2,3,4,5].map(i=>({Action:'playExtra',Label:'E'+i,ItemId:'x'+i})).concat([{Action:'home',Label:'Home'}])}});
  w=boot(pages); await open(w); w.__anims.length=0; click(w,'More'); check(w.__anims.find(x=>x.frames[0].opacity===0).frames[0].transform==='translateX(8%)','"More" slides forward');
  w.__anims.length=0; click(w,'Previous'); check(w.__anims.find(x=>x.frames[0].opacity===0).frames[0].transform==='translateX(-8%)','"Previous" slides back');
  // opt-outs
  w=boot(mk(lay({Style:'fade',DurationMs:300})),{reducedMotion:true}); await open(w); click(w,'Features'); check(w.__anims.length===0&&w.document.querySelectorAll('.discMenusScreen').length===1,'prefers-reduced-motion: no animation at all');
  w=boot(mk(lay({Style:'fade',DurationMs:0}))); await open(w); click(w,'Features'); check(w.__anims.length===0,'durationMs 0: no animation');
  w=boot(mk(lay({Style:'zoom',DurationMs:300})),{animate:false}); await open(w); click(w,'Features'); check(w.document.querySelectorAll('.discMenusScreen').length===1,'browser without Web Animations: still switches menus cleanly');
  // shared layers stay put
  const banner={Type:'panel',Fill:'#112233',Position:{X:50,Y:90,W:80,H:10,Anchor:'bottom'}};
  w=boot(mk({Layout:{ButtonStyle:'text',Transition:{Style:'fade',DurationMs:250},Layers:[banner]}})); await open(w);
  const L1=w.document.querySelector('.discMenusLayers'); w.__anims.length=0; click(w,'Features');
  check(w.document.querySelector('.discMenusLayers')===L1&&!w.__anims.some(x=>x.el===L1),'a banner shared by both menus is untouched (not re-rendered or animated)');
  w=boot(mk({Layout:{ButtonStyle:'text',Transition:{Style:'fade',DurationMs:250},Layers:[banner]}},{features:{Title:'F',Layout:{Layers:[]},Entries:[{Action:'back',Label:'Back'}]}})); await open(w);
  const L2=w.document.querySelector('.discMenusLayers'); w.__anims.length=0; click(w,'Features');
  check(w.document.querySelector('.discMenusLayers')!==L2&&w.document.querySelector('.discMenusLayers').children.length===0,'menu that switches the banner off gets an empty layers layer');
  console.log('failures:',fail); process.exit(0);
})();
