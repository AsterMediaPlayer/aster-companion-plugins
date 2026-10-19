// Captions are always suppressed for background trailers (YouTube and local files).
// Runs the real browser code in a simulated browser (jsdom) with scripted server responses.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const src=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/discmenus.js'),'utf8');
let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e||''); };
const boot=(trailers)=>{ const dom=new JSDOM('<body></body>',{url:'https://s.example/web/#/details?id=4cf4efb9cb2c96b4ec157568bcccdf7f',runScripts:'outside-only',pretendToBeVisual:true}); const w=dom.window; w.console.warn=()=>{}; w.console.info=()=>{};
  const doc={Root:'m',Background:{Source:'trailer',Dim:0.1},Trailers:trailers,Menus:{m:{Title:'M',Entries:[{Action:'back',Label:'x'}]}}};
  w.ApiClient={getJSON:()=>Promise.resolve(doc),getUrl:(u,p)=>'https://s.example/'+u,getImageUrl:()=>'x',deviceId:()=>'d',accessToken:()=>'T'}; w.eval(src); return w; };
(async()=>{
  let w=boot([{Kind:'youtube',VideoId:'ue80QwXMRHg'}]); await new Promise(r=>setTimeout(r,50)); w.document.getElementById('discMenusButton').click();
  const f=w.document.querySelector('#discMenusVideo iframe'); const sent=[]; f.contentWindow.postMessage=(m,o)=>sent.push([JSON.parse(m),o]);
  check(new URL(f.src).searchParams.get('cc_load_policy')==='0','YouTube URL asks for captions off (cc_load_policy=0)');
  f.dispatchEvent(new w.Event('load'));
  const unl=()=>sent.filter(([m])=>m.event==='command'&&m.func==='unloadModule').map(([m])=>m.args[0]);
  check(unl().includes('captions')&&unl().includes('cc'),'on load: tells the player to unload its captions module (both module names)', unl().join());
  check(sent.every(([,o])=>o==='https://www.youtube-nocookie.com'),'commands are only ever posted to the YouTube origin');
  const before=unl().length;
  w.dispatchEvent(new w.MessageEvent('message',{origin:'https://www.youtube-nocookie.com',source:f.contentWindow,data:JSON.stringify({event:'onStateChange',info:1})}));
  check(unl().length>before,'when playback starts: asks again (captions load after start)', (unl().length-before)+' more');
  // local file with embedded text tracks
  w=boot([{Kind:'local',ItemId:'abc'}]); await new Promise(r=>setTimeout(r,50)); w.document.getElementById('discMenusButton').click();
  const v=w.document.querySelector('#discMenusVideo video');
  const tracks=[{mode:'showing'},{mode:'hidden'}]; tracks.addEventListener=(ev,fn)=>{tracks._add=fn};
  Object.defineProperty(v,'textTracks',{value:tracks});
  v.dispatchEvent(new w.Event('loadedmetadata'));
  check(tracks.every(t=>t.mode==='disabled'),'local trailer: embedded subtitle tracks forced off at metadata load');
  tracks.push({mode:'showing'}); v.dispatchEvent(new w.Event('playing'));
  check(tracks.every(t=>t.mode==='disabled'),'...including tracks that appear later, when playback starts');
  console.log('failures:',fail); process.exit(0);
})();
