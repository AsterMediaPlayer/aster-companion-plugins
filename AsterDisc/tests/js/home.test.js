// The Home action versus Back, across menu depths and pages.
// Runs the real browser code in a simulated browser (jsdom) with scripted server responses.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const src=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/discmenus.js'),'utf8');
const dom=new JSDOM('<body></body>',{url:'http://x/web/#/details?id=4cf4efb9cb2c96b4ec157568bcccdf7f',runScripts:'outside-only',pretendToBeVisual:true});
const w=dom.window; let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e||''); };
const ex=(l,id)=>({Action:'playExtra',Label:l,ItemId:id});
const flow={Flow:{Region:{X:49,Y:87.5,W:60,H:9,Anchor:'center'},Columns:5,Rows:1}};
const doc={Root:'main',Layout:{ButtonStyle:'text'},Menus:{
  main:{Title:'Main',Entries:[{Action:'playFeature',Label:'Play',Position:{X:30,Y:80,W:18}},{Action:'submenu',Label:'Special Features',Menu:'features',Position:{X:70,Y:80,W:18}}]},
  features:{Title:'F',Layout:flow,Entries:[ex('A','1'),ex('B','2'),ex('C','3'),ex('D','4'),{Action:'submenu',Label:'Deleted Scenes',Menu:'deleted'},{Action:'home',Label:'Home'}]},
  deleted:{Title:'D',Layout:flow,Entries:[ex('Rooftop','5'),ex('Lobby','6'),{Action:'back',Label:'Back'},{Action:'home',Label:'Home'}]}}};
w.ApiClient={getJSON:()=>Promise.resolve(doc),getUrl:u=>u,getImageUrl:()=>'x',deviceId:()=>'d'};
w.console.warn=()=>{}; w.eval(src);
const names=()=>[...w.document.querySelectorAll('.discMenuEntry')].map(b=>b.getAttribute('aria-label'));
const click=l=>{const b=[...w.document.querySelectorAll('.discMenuEntry')].find(x=>x.getAttribute('aria-label')===l); if(!b) throw new Error('no '+l+' in '+names()); b.click();};
setTimeout(()=>{
  w.document.getElementById('discMenusButton').click();
  click('Special Features');
  check(names().join()==='A,B,C,Home,More','features p1: Home is pinned next to More; no Back/Previous confusion', names().join());
  click('More');
  check(names().join()==='D,Deleted Scenes,Home,Previous','features p2: Home + Previous', names().join());
  click('Deleted Scenes');
  check(names().join()==='Rooftop,Lobby,Back,Home','third level: both Back (parent) and Home (root) offered', names().join());
  click('Back');
  check(names().includes('Deleted Scenes')&&names().includes('Home'),'Back goes up ONE level (to Special Features, page 2 where we left it)', names().join());
  click('Deleted Scenes'); click('Home');
  check(names().join()==='Play,Special Features','Home from the third level goes straight to the main menu', names().join());
  check(w.document.activeElement.getAttribute('aria-label')==='Special Features','...landing on the Special Features button we left from');
  click('Special Features');
  check(names().join()==='A,B,C,Home,More','re-entering Special Features starts at page 1');
  click('Home');
  check(names().join()==='Play,Special Features','Home from the second level goes to the main menu too');
  console.log('failures:',fail); process.exit(0);
},50);
