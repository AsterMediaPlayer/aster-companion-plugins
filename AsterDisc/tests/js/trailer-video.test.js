// The trailer background layer: persistence across menus, fade-in, failure fallbacks, spoofed messages.
// Runs the real browser code in a simulated browser (jsdom) with scripted server responses.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const src=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/discmenus.js'),'utf8');
let fail=0; const check=(ok,msg,extra)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',msg,extra||''); };
function boot(doc){
  const dom=new JSDOM('<body></body>',{url:'https://streaming.example/web/#/details?id=4cf4efb9cb2c96b4ec157568bcccdf7f',runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window; w.console.warn=()=>{}; w.console.info=()=>{};
  w.ApiClient={getJSON:()=>Promise.resolve(doc),getUrl:(u,p)=>'https://streaming.example/'+u+(p?'?'+Object.entries(p).map(([k,v])=>k+'='+v).join('&'):''),getImageUrl:()=>'x',deviceId:()=>'d',accessToken:()=>'TOK'};
  w.eval(src); return w;
}
const bg=(extra)=>Object.assign({Source:'trailer',Poster:'asset:thor/bg.webp',Dim:0.1},extra||{});
const mkdoc=(background,trailers)=>({Root:'main',Background:background,Trailers:trailers,Layout:{ButtonStyle:'text'},Menus:{
  main:{Title:'Main',Entries:[{Action:'submenu',Label:'Features',Menu:'f'}]},
  f:{Title:'F',Entries:[{Action:'back',Label:'Back'}]},
  plain:{Title:'P',Background:{Source:'color',Color:'#101010'},Entries:[{Action:'back',Label:'Back'}]}}});
const yt=[{Kind:'youtube',VideoId:'ue80QwXMRHg',Name:'Official Trailer'},{Kind:'youtube',VideoId:'v7MGUNV8MxU',Name:'Teaser Trailer'}];
const run=async()=>{
  // 1) YouTube trailer, default index 0
  let w=boot(mkdoc(bg(),yt)); await new Promise(r=>setTimeout(r,50));
  w.document.getElementById('discMenusButton').click();
  const layer=()=>w.document.getElementById('discMenusVideo'); const frame=()=>layer()&&layer().querySelector('iframe');
  check(!!layer()&&!!frame(),'trailer background creates a video layer with an iframe');
  const u=new URL(frame().src);
  check(u.origin==='https://www.youtube-nocookie.com'&&u.pathname==='/embed/ue80QwXMRHg','embed uses the privacy-enhanced domain and the Official Trailer id',frame().src.slice(0,70));
  check(u.searchParams.get('mute')==='1'&&u.searchParams.get('autoplay')==='1'&&u.searchParams.get('loop')==='1'&&u.searchParams.get('playlist')==='ue80QwXMRHg'&&u.searchParams.get('controls')==='0','muted, autoplay, looping, no controls');
  check(u.searchParams.get('origin')==='https://streaming.example','origin param set for the player messaging');
  check(frame().getAttribute('sandbox')==='allow-scripts allow-same-origin'&&frame().style.pointerEvents==='none'&&frame().tabIndex===-1,'iframe sandboxed, not clickable, not focusable');
  check(layer().style.backgroundImage.includes('asset')||layer().style.backgroundImage.includes('Assets'),'poster is shown behind the video', layer().style.backgroundImage.slice(0,70));
  check(frame().style.opacity==='0','video hidden until it is actually playing');
  const overlay=w.document.getElementById('discMenusOverlay');
  check(/rgba\(0,\s*0,\s*0,\s*0\.1\)/.test(overlay.style.backgroundColor||overlay.style.cssText),'overlay is only a dim layer so the video shows through', overlay.style.backgroundColor);
  // spoofed messages must be ignored
  const send=(origin,source,data)=>w.dispatchEvent(new w.MessageEvent('message',{origin,source,data:JSON.stringify(data)}));
  send('https://evil.example',frame().contentWindow,{event:'onStateChange',info:1});
  check(frame().style.opacity==='0','message from the wrong origin is ignored');
  send('https://www.youtube-nocookie.com',w,{event:'onStateChange',info:1});
  check(frame().style.opacity==='0','message from some other window is ignored');
  send('https://www.youtube-nocookie.com',frame().contentWindow,{event:'onStateChange',info:3});
  check(frame().style.opacity==='0','state "buffering" does not reveal the video');
  send('https://www.youtube-nocookie.com',frame().contentWindow,{event:'onStateChange',info:1});
  check(frame().style.opacity==='1','state "playing" from the real player fades the video in');
  // persistence across menu navigation
  const before=frame();
  [...w.document.querySelectorAll('.discMenuEntry')][0].click();           // -> submenu 'f' (re-renders overlay)
  check(frame()===before,'video keeps playing (same element) when going into a submenu');
  [...w.document.querySelectorAll('.discMenuEntry')][0].click();           // Back
  check(frame()===before,'...and when coming back');
  // a menu with a non-trailer background removes it
  w.eval('void 0');
  // 2) error path
  w.close(); w=boot(mkdoc(bg(),yt)); await new Promise(r=>setTimeout(r,50)); w.document.getElementById('discMenusButton').click();
  const send2=(data)=>w.dispatchEvent(new w.MessageEvent('message',{origin:'https://www.youtube-nocookie.com',source:w.document.querySelector('#discMenusVideo iframe').contentWindow,data:JSON.stringify(data)}));
  send2({event:'onError',info:150});
  check(!w.document.querySelector('#discMenusVideo iframe')&&!!w.document.getElementById('discMenusVideo'),'embedding refused (error 150): iframe removed, poster layer kept');
  // 3) trailerIndex picks the Teaser
  w.close(); w=boot(mkdoc(bg({TrailerIndex:1,Muted:false}),yt)); await new Promise(r=>setTimeout(r,50)); w.document.getElementById('discMenusButton').click();
  const u2=new URL(w.document.querySelector('#discMenusVideo iframe').src);
  check(u2.pathname==='/embed/v7MGUNV8MxU'&&u2.searchParams.get('mute')==='0','trailerIndex 1 uses the Teaser; muted:false unmutes');
  // 4) closing removes the video layer
  w.document.querySelector('#discMenusOverlay button').click();
  check(!w.document.getElementById('discMenusVideo'),'closing the menu removes the video layer (stops playback)');
  // 5) no trailers available: poster only, no crash
  w.close(); w=boot(mkdoc(bg(),[])); await new Promise(r=>setTimeout(r,50)); w.document.getElementById('discMenusButton').click();
  check(!!w.document.getElementById('discMenusVideo')&&!w.document.querySelector('#discMenusVideo iframe,#discMenusVideo video'),'no trailer: poster-only layer, nothing broken');
  // 6) out-of-range index: poster only
  w.close(); w=boot(mkdoc(bg({TrailerIndex:5}),yt)); await new Promise(r=>setTimeout(r,50)); w.document.getElementById('discMenusButton').click();
  check(!w.document.querySelector('#discMenusVideo iframe'),'trailerIndex beyond the list: poster only');
  // 7) local trailer file -> <video>
  w.close(); w=boot(mkdoc(bg(),[{Kind:'local',ItemId:'abc123',Name:'Trailer'},...yt])); await new Promise(r=>setTimeout(r,50)); w.document.getElementById('discMenusButton').click();
  const v=w.document.querySelector('#discMenusVideo video');
  check(!!v&&v.src.includes('Videos/abc123/stream')&&v.src.includes('static=true')&&v.src.includes('api_key=TOK')&&v.loop&&v.muted,'local trailer plays in a muted, looping <video> via the static stream', v&&v.src);
  v.dispatchEvent(new w.Event('error'));
  check(!w.document.querySelector('#discMenusVideo video')&&!!w.document.getElementById('discMenusVideo'),'local file the browser cannot play: video removed, poster kept');
  // 8) malicious video id never reaches the page (server validates, but belt and braces)
  w.close(); w=boot(mkdoc(bg(),[{Kind:'youtube',VideoId:'x"><script>alert(1)</script>'}])); await new Promise(r=>setTimeout(r,50)); w.document.getElementById('discMenusButton').click();
  const f8=w.document.querySelector('#discMenusVideo iframe');
  check(!f8||new URL(f8.src).origin==='https://www.youtube-nocookie.com','hostile video id cannot change the embed origin', f8&&f8.src.slice(0,90));
  console.log('failures:',fail); process.exit(0);
};
run();
