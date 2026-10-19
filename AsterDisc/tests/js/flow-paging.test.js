// The paged grid (More / Previous, pinned Back) through the real renderer.
// Runs the real browser code in a simulated browser (jsdom) with scripted server responses.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const src=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/discmenus.js'),'utf8');
const dom=new JSDOM('<body></body>',{url:'http://x/web/#/details?id=4cf4efb9cb2c96b4ec157568bcccdf7f',runScripts:'outside-only',pretendToBeVisual:true});
const w=dom.window;
const banner={Type:'image',Image:'asset:thor/banner.webp',Position:{X:50,Y:97,W:86,H:19,Anchor:'bottom'},Fit:'contain'};
const ex=(l,id)=>({Action:'playExtra',Label:l,ItemId:id});
const doc={Root:'main',Layout:{HideTitle:true,ButtonStyle:'text',Layers:[banner]},Theme:{Id:'x',Accent:'#ff4fd8',Align:'center'},
 Background:{Source:'image',Image:'asset:thor/bg.webp',Dim:0},
 Menus:{
  main:{Title:'Main',Layout:{},Entries:[{Action:'playFeature',Label:'Play',Position:{X:26,Y:87.5,W:18,Anchor:'center'}},{Action:'submenu',Label:'Special Features',Menu:'features',Position:{X:72,Y:87.5,W:18,Anchor:'center'}}]},
  features:{Title:'Special Features',Layout:{Flow:{Region:{X:49,Y:87.5,W:60,H:9,Anchor:'center'},Columns:5,Rows:1}},Entries:[
    ex('Gag Reel','1'),ex('Deleted Scenes','2'),ex('Featurettes','3'),ex('Evolution of Heroes','4'),ex('Team Darryl','5'),{Action:'back',Label:'Back'}]}}};
w.ApiClient={getJSON:()=>Promise.resolve(doc),getUrl:u=>'http://s/'+u,getImageUrl:()=>'x',deviceId:()=>'d'};
w.eval(src);
const $=()=>[...w.document.querySelectorAll('.discMenuEntry')];
const show=()=>$().map(b=>b.getAttribute('aria-label')+'@'+Math.round(parseFloat(b.style.left))+'%');
const key=(k)=>w.document.dispatchEvent(new w.KeyboardEvent('keydown',{key:k,bubbles:true,cancelable:true}));
const click=(label)=>{ const b=$().find(x=>x.getAttribute('aria-label')===label); if(!b) throw new Error('no button '+label+' in '+show()); b.click(); };
let fail=0; const check=(ok,msg,extra)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',msg,extra||''); };
setTimeout(()=>{
 w.document.getElementById('discMenusButton').click();
 check(show().join()==='Play@26%,Special Features@72%','main menu: hand-positioned buttons unchanged', show().join());
 check(w.document.querySelectorAll('img').length===1,'main menu: inherited banner layer drawn from the document default');
 check(!w.document.querySelector('h1'),'main menu: inherited hideTitle');
 click('Special Features');
 check(show().join()==='Gag Reel@25%,Deleted Scenes@37%,Featurettes@49%,Back@61%,More@73%','features p1: 3 items + Back + More in the banner cells', show().join());
 check(w.document.querySelectorAll('img').length===1,'features: inherits the same banner');
 check(w.document.activeElement.getAttribute('aria-label')==='Gag Reel','features p1: focus starts on first entry');
 click('More');
 check(show().join()==='Evolution of Heroes@25%,Team Darryl@37%,Back@61%,Previous@73%','features p2: remaining items + Back + Previous', show().join());
 check(w.document.activeElement.getAttribute('aria-label')==='Evolution of Heroes','features p2: focus on first entry of the new page');
 key('Escape');
 check(show().includes('More@73%')&&show()[0].startsWith('Gag'),'Back KEY on page 2 returns to page 1, not the main menu', show().join());
 click('More'); click('Previous');
 check(show()[0].startsWith('Gag'),'Previous button returns to page 1');
 click('More'); click('Back');
 check(show().join()==='Play@26%,Special Features@72%','on-screen Back button goes UP to the main menu from page 2', show().join());
 click('Special Features');
 check(show()[0].startsWith('Gag'),'re-entering the submenu starts at page 1 again', show().join());
 key('Escape');
 check(show().join()==='Play@26%,Special Features@72%','Back KEY on page 1 goes up to main');
 check(w.document.activeElement.getAttribute('aria-label')==='Special Features','...and focus returns to the Special Features button');
 click('Special Features'); click('Gag Reel');
 console.log('failures:',fail); process.exit(0);
},50);
