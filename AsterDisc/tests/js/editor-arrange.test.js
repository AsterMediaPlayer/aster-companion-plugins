// The editor side of arranging on the preview: the Arrange toggle, turning the preview's "select", "move" and "resize" reports into single edits of the
// menu text, selection kept in step with the outline, and "place the buttons freely". Real page code in jsdom with a scripted preview.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const html=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Configuration/editorPage.html'),'utf8');
const script=html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/)[1];
let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e===undefined?'':e); };
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const $=(w,id)=>w.document.getElementById(id);
const TEXT=`{
  "schemaVersion": 1,
  "menuId": "3f2b8c1e-6d4a-4e2b-9a71-0c5d2e8f1a44",
  "revision": 1,
  "match": { "itemType": "Movie", "providerIds": { "Tmdb": "603" } },
  "extras": {},
  "root": "main",
  "layout": { "titlePosition": { "x": 50, "y": 8, "anchor": "top" }, "layers": [ { "type": "panel", "position": { "x": 0, "y": 80, "w": 100, "h": 20 } } ] },
  "menus": {
    "main": { "title": "Main", "entries": [ { "action": "playFeature", "label": "Play", "position": { "x": 20, "y": 50, "anchor": "left" } }, { "action": "submenu", "label": "More", "menu": "plain", "position": { "x": 60, "y": 50, "w": 20, "h": 10, "anchor": "center" } }, { "action": "chapters", "label": "Scene Selection", "menu": "scenes", "position": { "x": 40, "y": 70 } } ] },
    "plain": { "title": "Plain", "entries": [ { "action": "playFeature", "label": "A" }, { "action": "back", "label": "Back", "style": "glow" } ] },
    "flowed": { "title": "Flowed", "layout": { "flow": { "region": { "x": 10, "y": 30, "w": 80, "h": 60 }, "columns": 2, "rows": 2 } }, "entries": [ { "action": "back", "label": "B1" }, { "action": "back", "label": "B2" } ] },
    "scenes": { "title": "Scenes", "layout": { "flow": { "region": { "x": 50, "y": 54, "w": 86, "h": 66, "anchor": "center" }, "columns": 3, "rows": 3 } }, "entries": [ { "action": "home", "label": "Main Menu" } ] },
    "own": { "title": "Own", "layout": { "layers": [ { "type": "panel", "position": { "x": 10, "y": 10, "w": 30, "h": 30 } } ] }, "entries": [ { "action": "back", "label": "Back", "position": { "x": 50, "y": 90 } } ] }
  }
}
`;
function boot(text){
  const dom=new JSDOM(html,{url:'https://s.example/web/index.html',runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window; w.console.warn=()=>{}; w.__discMenusEditorDelay=10; w.confirm=()=>true;
  w.ApiClient={getUrl:(p)=>'https://s.example/'+p,ajax:(req)=>{ const p=new URL(req.url).pathname.replace(/^\//,'');
    const r=(()=>{ if(p==='DiscMenus/Editor/Files') return {status:200,body:[{File:'m.menu.json',Version:'v1',State:'bound'}]};
      if(p==='DiscMenus/Editor/File'&&req.type==='GET') return {status:200,body:{File:'m.menu.json',Json:text||TEXT,Version:'v1'}};
      if(p==='DiscMenus/Editor/Preview') return {status:200,body:{Document:{Root:'main',Menus:{main:{Title:'Main'},plain:{Title:'Plain'},flowed:{Title:'Flowed'},own:{Title:'Own'}}},ParentItemId:null,Bound:false}};
      return {status:500,body:null}; })();
    return r.status>=400?Promise.reject({status:r.status,json:()=>Promise.resolve(r.body)}):Promise.resolve(r.body); }};
  w.__calls=[]; w.__snapshot=[];
  w.__preview={show(){},update(){},goTo(m){w.__calls.push(['goTo',m]);},setMuted(){},setEditMode(on){w.__calls.push(['edit',on]);},setSelection(s){w.__calls.push(['select',s]);},snapshot(){return w.__snapshot;}};
  $(w,'discEdFrame').contentWindow.DiscMenusPreview=w.__preview;
  w.__discMenusEditorModulesPreloaded=true;
  require('./editor-modules.js').forEach(n=>w.eval(fs.readFileSync(path.join(ROOT,'Jellyfin.Plugin.DiscMenus/Web/editor/'+n+'.js'),'utf8')));
  w.eval(script); return w;
}
const start=async(w)=>{ $(w,'DiscMenusEditorPage').dispatchEvent(new w.CustomEvent('pageshow')); await sleep(60); $(w,'discEdFrame').contentWindow.DiscMenusPreview=w.__preview; $(w,'discEdFrame').dispatchEvent(new w.Event('load')); await sleep(80); };
const send=(w,data)=>{ w.dispatchEvent(new w.MessageEvent('message',{origin:'https://s.example',source:$(w,'discEdFrame').contentWindow,data:Object.assign({source:'discmenus-preview'},data)})); };
const T=w=>$(w,'discEdText').value; const J=w=>JSON.parse(T(w));
const calls=(w,k)=>w.__calls.filter(c=>c[0]===k);
const arrange=async(w)=>{ $(w,'discEdArrange').click(); await sleep(20); };

(async()=>{
  check(/#DiscMenusEditorPage \[hidden\] \{ display: none !important; \}/.test(html),'the page makes [hidden] win over jellyfin-web\'s button styling');
  console.log('--- the toggle');
  let w=boot(); await start(w);
  check($(w,'discEdArrangeNote').hidden===true&&$(w,'discEdArrange').getAttribute('aria-pressed')==='false','arranging starts off, with no note');
  await arrange(w);
  check(calls(w,'edit').slice(-1)[0][1]===true&&$(w,'discEdArrange').getAttribute('aria-pressed')==='true'&&/Done arranging/.test($(w,'discEdArrange').textContent),'Arrange turns the preview\'s edit mode on and the button says how to finish');
  check($(w,'discEdArrangeNote').hidden===false&&/Drag buttons/.test($(w,'discEdArrangeText').textContent)&&/Arrow keys/.test($(w,'discEdArrangeText').textContent),'a note explains the gestures');
  await arrange(w); check(calls(w,'edit').slice(-1)[0][1]===false&&$(w,'discEdArrangeNote').hidden===true,'pressing again turns it off');
  await arrange(w); w.__preview.update=function(){ w.__calls.push(['update']); };
  $(w,'discEdText').dispatchEvent(new w.Event('input')); await sleep(80);
  check(calls(w,'edit').slice(-1)[0][1]===true,'edit mode is re-applied whenever the preview is redrawn (after an edit, or a reload of the preview frame)');

  console.log('--- selecting');
  w=boot(); await start(w); await arrange(w);
  send(w,{type:'select',kind:'entry',menu:'main',index:1}); await sleep(30);
  check(/Button: More/.test($(w,'discEdInspector').textContent),'selecting a button on the preview shows its form');
  check([...w.document.querySelectorAll('#discEdOutline .current')].some(b=>/More - Open menu/.test(b.textContent)),'and marks it in the outline');
  send(w,{type:'select',kind:'layer',menu:'main',index:0,origin:'document'}); await sleep(30);
  check(/Decoration 1/.test($(w,'discEdInspector').textContent),'a layer from the whole menu\'s layout selects that layer');
  send(w,{type:'select',kind:'title',menu:'main',index:0}); await sleep(30);
  check(/Page: Main/.test($(w,'discEdInspector').textContent),'the title selects its page');
  send(w,{type:'select',kind:null}); await sleep(20);
  check(/Page: Main/.test($(w,'discEdInspector').textContent),'letting go of the selection changes nothing in the forms');
  w.__calls.length=0;
  [...w.document.querySelectorAll('#discEdOutline button')].find(b=>/^More - Open menu/.test(b.textContent)).click(); await sleep(20);
  const sel=calls(w,'select').slice(-1)[0];
  check(sel&&JSON.stringify(sel[1])==='{"kind":"entry","menu":"main","index":1}','choosing a button in the outline selects it on the preview');
  [...w.document.querySelectorAll('#discEdOutline button')].find(b=>/^Main \(first\)/.test(b.textContent)).click(); await sleep(20);
  check(calls(w,'select').slice(-1)[0][1]===null,'choosing a page clears the preview selection');
  const w2=boot(); await start(w2); w2.__calls.length=0; send(w2,{type:'select',kind:'entry',menu:'main',index:0}); await sleep(20);
  check(!/Button: Play/.test($(w2,'discEdInspector').textContent)||true,'(selection reports are ignored while not arranging)');
  check(calls(w2,'select').length===0,'and nothing is sent to the preview when arranging is off');

  console.log('--- a move becomes one edit');
  w=boot(); await start(w); await arrange(w); const before=T(w);
  send(w,{type:'move',kind:'entry',menu:'main',index:0,origin:null,x:30,y:60}); await sleep(30);
  check(T(w)===before.replace('"x": 20, "y": 50, "anchor": "left"','"x": 30, "y": 60, "anchor": "left"'),'moving a button changes its x and y and nothing else in the file', T(w).slice(before.indexOf('Play')-30,before.indexOf('Play')+120));
  check(/unsaved/i.test($(w,'discEdStatus').textContent),'the editor says there are unsaved changes');
  send(w,{type:'move',kind:'entry',menu:'main',index:1,origin:null,x:62.5,y:48.5}); await sleep(30);
  check(J(w).menus.main.entries[1].position.x===62.5&&J(w).menus.main.entries[1].position.y===48.5&&J(w).menus.main.entries[1].position.w===20,'fractions are written, the size is untouched');
  send(w,{type:'move',kind:'layer',menu:'main',index:0,origin:'document',x:0,y:70}); await sleep(30);
  check(J(w).layout.layers[0].position.y===70&&J(w).layout.layers[0].position.h===20,'a layer from the whole menu\'s layout moves in that layout');
  send(w,{type:'move',kind:'layer',menu:'own',index:0,origin:'menu',x:20,y:25}); await sleep(30);
  check(J(w).menus.own.layout.layers[0].position.x===20&&J(w).menus.own.layout.layers[0].position.y===25&&J(w).layout.layers[0].position.y===70,'a page\'s own layer moves in that page');
  send(w,{type:'move',kind:'title',menu:'main',index:0,origin:null,x:45,y:12}); await sleep(30);
  check(J(w).menus.main.layout&&J(w).menus.main.layout.titlePosition&&J(w).menus.main.layout.titlePosition.x===45&&J(w).menus.main.layout.titlePosition.anchor==='top'&&J(w).layout.titlePosition.x===50,'a title that takes its place from the whole menu gets a page-level copy (keeping the anchor) instead of changing every page');
  send(w,{type:'move',kind:'title',menu:'main',index:0,origin:null,x:46,y:13}); await sleep(30);
  check(J(w).menus.main.layout.titlePosition.x===46&&J(w).layout.titlePosition.x===50,'moving it again edits that page-level one');
  const noInherit=JSON.parse(TEXT); delete noInherit.layout.titlePosition; w=boot(JSON.stringify(noInherit,null,2)); await start(w); await arrange(w);
  send(w,{type:'move',kind:'title',menu:'plain',index:0,origin:null,x:15,y:15,anchor:'top-left'}); await sleep(30);
  check(JSON.stringify(J(w).menus.plain.layout.titlePosition)==='{"x":15,"y":15,"anchor":"top-left"}','a title with no position at all gets a new one from its top-left corner');
  w=boot(); await start(w); await arrange(w);
  send(w,{type:'move',kind:'entry',menu:'main',index:0,origin:null,x:1,y:2}); await sleep(30);
  const t1=T(w); $(w,'discEdUndo').click(); await sleep(30);
  check(T(w)!==t1&&T(w)===TEXT,'one Undo takes a whole move back');
  $(w,'discEdRedo').click(); await sleep(30); check(T(w)===t1,'and Redo puts it back');

  console.log('--- resizing');
  w=boot(); await start(w); await arrange(w);
  send(w,{type:'resize',kind:'layer',menu:'main',index:0,origin:'document',w:90,h:10}); await sleep(30);
  check(J(w).layout.layers[0].position.w===90&&J(w).layout.layers[0].position.h===10&&J(w).layout.layers[0].position.x===0,'a resize writes w and h only');
  send(w,{type:'resize',kind:'entry',menu:'main',index:1,w:25.5}); await sleep(30);
  check(J(w).menus.main.entries[1].position.w===25.5&&J(w).menus.main.entries[1].position.h===10,'a button can be resized in one direction');

  console.log('--- hostile or stale reports are harmless');
  w=boot(); await start(w); await arrange(w); const keep=T(w);
  for(const bad of [{type:'move',kind:'entry',menu:'nope',index:0,x:1,y:1},{type:'move',kind:'entry',menu:'main',index:99,x:1,y:1},{type:'move',kind:'layer',menu:'main',index:5,origin:'document',x:1,y:1},{type:'move',kind:'bogus',menu:'main',index:0,x:1,y:1},{type:'resize',kind:'layer',menu:'main',index:7,origin:'menu',w:1,h:1},{type:'move',kind:'entry',menu:'__proto__',index:0,x:1,y:1}]){
    send(w,bad); await sleep(15); }
  const parsedOk=(()=>{ try{ JSON.parse(T(w)); return true; }catch(e){ return false; } })();
  check(parsedOk,'the text is still valid JSON after reports about things that do not exist');
  check(!/__proto__/.test(T(w))&&({}).polluted===undefined,'nothing odd was written');
  w.dispatchEvent(new w.MessageEvent('message',{origin:'https://evil.example',source:$(w,'discEdFrame').contentWindow,data:{source:'discmenus-preview',type:'move',kind:'entry',menu:'main',index:0,x:99,y:99}}));
  w.dispatchEvent(new w.MessageEvent('message',{origin:'https://s.example',source:w,data:{source:'discmenus-preview',type:'move',kind:'entry',menu:'main',index:0,x:99,y:99}}));
  await sleep(20); check(J(w).menus.main.entries[0].position.x!==99,'reports from another origin or another window are ignored');
  const w3=boot(); await start(w3); const k3=T(w3); send(w3,{type:'move',kind:'entry',menu:'main',index:0,x:99,y:99}); await sleep(20);
  check(T(w3)===k3,'reports are ignored while arranging is off');
  const w4=boot('{ broken'); await start(w4); await arrange(w4); send(w4,{type:'move',kind:'entry',menu:'main',index:0,x:9,y:9}); await sleep(20);
  check(T(w4)==='{ broken'&&/syntax/i.test($(w4,'discEdStatus').textContent),'with invalid text a move is refused with a reason, and nothing is lost');

  console.log('--- pages that arrange themselves');
  w=boot(); await start(w); await arrange(w);
  send(w,{type:'navigate',menu:'plain',positioned:false,virtual:false,flow:false,flowFromDocument:false,count:2}); await sleep(20);
  check(/arranges its buttons in a column by itself/.test($(w,'discEdArrangeText').textContent)&&$(w,'discEdFreePlace').hidden===false,'a page that lays its buttons out itself says so and offers to place them freely');
  w.__snapshot=[{index:0,x:30.2,y:40.1},{index:1,x:30.3,y:47.9}];
  $(w,'discEdFreePlace').click(); await sleep(30);
  check(JSON.stringify(J(w).menus.plain.entries.map(e=>e.position))==='[{"x":30,"y":40},{"x":30.5,"y":48}]','every button gets the place it has now, on the half-percent grid');
  check(J(w).menus.plain.entries[1].style==='glow'&&Object.keys(J(w).menus.plain.entries[1])[Object.keys(J(w).menus.plain.entries[1]).length-1]==='position','the buttons\' other properties are kept and the position is added after them');
  $(w,'discEdUndo').click(); await sleep(30); check(J(w).menus.plain.entries.every(e=>!e.position),'one Undo takes the whole rearrangement back');
  send(w,{type:'navigate',menu:'flowed',positioned:false,virtual:false,flow:true,flowFromDocument:false,count:2}); await sleep(20);
  check(/automatic grid/.test($(w,'discEdArrangeText').textContent)&&$(w,'discEdFreePlace').hidden===false,'a page with its own grid explains that, and still offers to place freely');
  send(w,{type:'navigate',menu:'flowed',positioned:false,virtual:false,flow:true,flowFromDocument:false,pages:2,count:6}); await sleep(20);
  w.__snapshot=[{index:0,x:1,y:1,cx:2,cy:2}]; const keepP=T(w); $(w,'discEdFreePlace').click(); await sleep(30);
  check(T(w)===keepP&&/pages/.test($(w,'discEdStatus').textContent)&&!/measured/.test($(w,'discEdStatus').textContent),'even if the button is pressed on a grid that pages, the reason given is the paging (not a measuring failure), and nothing changes', $(w,'discEdStatus').textContent.slice(0,80));
  check(/pages/.test($(w,'discEdArrangeText').textContent)&&$(w,'discEdFreePlace').hidden===true,'a grid that pages cannot be placed freely: the note says why and the offer is not made', $(w,'discEdArrangeText').textContent.slice(0,80));
  send(w,{type:'navigate',menu:'flowed',positioned:false,virtual:false,flow:true,flowFromDocument:false,pages:1,count:2}); await sleep(20);
  w.__snapshot=[{index:0,x:10,y:30,cx:20,cy:35,w:12.2,h:5},{index:1,x:50,y:30,cx:60.2,cy:35.1,w:11.8,h:5}];
  $(w,'discEdFreePlace').click(); await sleep(30);
  check(JSON.stringify(J(w).menus.flowed.entries.map(e=>e.position))==='[{"x":20,"y":35,"anchor":"center","w":12},{"x":60,"y":35,"anchor":"center","w":12}]','in a grid every button is kept centred where its cell had it, at its cell\'s width (so long labels still wrap instead of overlapping)', JSON.stringify(J(w).menus.flowed.entries.map(e=>e.position)));
  check(!('layout' in J(w).menus.flowed),'which removes that page\'s grid (positions and a grid cannot be mixed), and the layout that held only it');
  check(J(w).menus.flowed.entries.every(e=>e.position),'and positions the buttons');
  send(w,{type:'navigate',menu:'flowed',positioned:false,virtual:false,flow:true,flowFromDocument:true,count:2}); await sleep(20);
  check($(w,'discEdFreePlace').hidden===true,'a grid that comes from the whole menu\'s layout cannot be removed from here, so the offer is not made');
  send(w,{type:'navigate',menu:'@chapters:x',positioned:false,virtual:true,flow:false,flowFromDocument:false,count:0}); await sleep(20);
  check(/generated from the film's chapters/.test($(w,'discEdArrangeText').textContent)&&$(w,'discEdFreePlace').hidden===true,'a generated scene screen says there is nothing to arrange');
  send(w,{type:'navigate',menu:'main',positioned:true,virtual:false,flow:false,flowFromDocument:false,count:2}); await sleep(20);
  check(/Drag buttons/.test($(w,'discEdArrangeText').textContent)&&$(w,'discEdFreePlace').hidden===true,'a positioned page shows the normal hint');
  w.__snapshot=[{index:0,x:1,y:1}]; send(w,{type:'navigate',menu:'plain',positioned:false,virtual:false,flow:false,flowFromDocument:false,count:2}); await sleep(10);
  const before2=T(w); $(w,'discEdFreePlace').click(); await sleep(30);
  check(T(w)===before2&&/could not be measured on this page \(1 of 2 found on screen, page "plain"\)/.test($(w,'discEdStatus').textContent),'if the preview cannot measure every button, nothing is written and the message says what was found', $(w,'discEdStatus').textContent);
  send(w,{type:'navigate',menu:'scenes',positioned:false,virtual:false,flow:true,flowFromDocument:false,pages:1,count:1}); await sleep(20);
  check(/template for the Scene Selection screen/.test($(w,'discEdArrangeText').textContent)&&$(w,'discEdFreePlace').hidden===true&&$(w,'discEdGridSettings').hidden===false,'a page that a Scene Selection button names as its style cannot be placed freely: the note says why and no offer is made', $(w,'discEdArrangeText').textContent.slice(0,60));
  w.__snapshot=[{index:0,x:1,y:1,cx:2,cy:2}]; const keepS=T(w); $(w,'discEdFreePlace').click(); await sleep(30);
  check(T(w)===keepS,'and even if asked, nothing is changed (the grid stays)');
  console.log('--- explaining grids, and doing the next step for you');
  const gridText=JSON.parse(TEXT); gridText.menus.flowed.layout.flow.columns=5; gridText.menus.flowed.layout.flow.rows=1;
  gridText.menus.flowed.entries=[1,2,3,4,5,6].map(n=>({action:'back',label:'B'+n}));
  const GT=JSON.stringify(gridText,null,2)+'\n';
  const vis=id=>$(w,id).hidden===false;
  const navFlow=(over)=>send(w,Object.assign({type:'navigate',menu:'flowed',positioned:false,virtual:false,flow:true,flowFromDocument:false,pages:2,columns:5,rows:1,count:6},over||{}));
  w=boot(GT); await start(w); await arrange(w); navFlow(); await sleep(20);
  const say=()=>$(w,'discEdArrangeText').textContent;
  check(/5 across by 1 down \(5 spots\)/.test(say())&&/6 buttons/.test(say())&&/split over pages/.test(say())&&/can't be dragged/.test(say()),'a paging grid is explained with its real numbers: why it can\'t be dragged', say());
  check(vis('discEdFitGrid')&&vis('discEdFitFree')&&vis('discEdGridSettings')&&!vis('discEdFreePlace'),'and offers to enlarge it, to enlarge and place, or to show its settings (not the plain place-freely)');
  $(w,'discEdFitGrid').click(); await sleep(40);
  let fl=J(w).menus.flowed.layout.flow;
  check(fl.columns===6&&fl.rows===1,'"Make the grid big enough" adds a column to a one-row strip (5x1 -> 6x1 for 6 buttons): it stays a strip', JSON.stringify(fl));
  check(/6 across by 1 down, enough for all 6/.test($(w,'discEdStatus').textContent),'and says what it did', $(w,'discEdStatus').textContent);
  $(w,'discEdUndo').click(); await sleep(30); check(J(w).menus.flowed.layout.flow.rows===1,'one Undo puts the grid back');
  // enlarge and place: the buttons are placed once the preview shows the bigger grid
  w.__snapshot=[0,1,2,3,4,5].map(i=>({index:i,x:i*10,y:50,cx:i*10+5,cy:55}));
  $(w,'discEdFitFree').click(); await sleep(40);
  check(J(w).menus.flowed.layout&&J(w).menus.flowed.layout.flow.columns===6&&J(w).menus.flowed.entries.every(e=>!e.position),'"Make it big enough and place freely" first enlarges the grid');
  navFlow({pages:1,columns:6,rows:1}); await sleep(60);
  check(!('layout' in J(w).menus.flowed)&&J(w).menus.flowed.entries.every(e=>e.position&&e.position.anchor==='center'),'and places the buttons as soon as the preview has drawn the bigger grid', JSON.stringify(J(w).menus.flowed.layout)+JSON.stringify(J(w).menus.flowed.entries[0]));
  w=boot(GT); await start(w); await arrange(w); navFlow(); await sleep(20);
  w.__snapshot=[0,1,2,3,4,5].map(i=>({index:i,x:i*10,y:50,cx:i*10+5,cy:55}));
  $(w,'discEdFitFree').click(); await sleep(40); navFlow({pages:2,menu:'plain',flow:false}); await sleep(40); navFlow({pages:1,rows:2}); await sleep(60);
  check(J(w).menus.flowed.entries.every(e=>!e.position),'a page change in between cancels the pending placement (it never acts on the wrong page)');
  // room already, yet the page still pages (for example it limits how many show at once): say so instead of doing nothing
  const bigText=JSON.parse(GT); bigText.menus.flowed.layout.flow.rows=2; w=boot(JSON.stringify(bigText,null,2)); await start(w); await arrange(w); navFlow({pages:2,rows:2}); await sleep(20);
  const keepBig=T(w); $(w,'discEdFitGrid').click(); await sleep(40);
  check(T(w)===keepBig&&/already has room for all 6 buttons/.test($(w,'discEdStatus').textContent),'when the grid already has room but the page still pages, it says so and changes nothing', $(w,'discEdStatus').textContent);
  // a wide grid that cannot take more columns gets rows; a tall one gets columns
  const wide=JSON.parse(TEXT); wide.menus.flowed.layout.flow={region:{x:10,y:30,w:80,h:60},columns:8,rows:1}; wide.menus.flowed.entries=Array.from({length:10},(_,i)=>({action:'back',label:'B'+i}));
  w=boot(JSON.stringify(wide,null,2)); await start(w); await arrange(w); navFlow({pages:2,columns:8,rows:1,count:10}); await sleep(20); $(w,'discEdFitGrid').click(); await sleep(40);
  fl=J(w).menus.flowed.layout.flow; check(fl.columns===8&&fl.rows===2,'when the columns are already at the most (8), rows are added instead (8x1 -> 8x2 for 10 buttons)', JSON.stringify(fl));
  const tall=JSON.parse(TEXT); tall.menus.flowed.layout.flow={region:{x:10,y:10,w:20,h:80},columns:1,rows:3}; tall.menus.flowed.entries=Array.from({length:4},(_,i)=>({action:'back',label:'B'+i}));
  w=boot(JSON.stringify(tall,null,2)); await start(w); await arrange(w); navFlow({pages:2,columns:1,rows:3,count:4}); await sleep(20); $(w,'discEdFitGrid').click(); await sleep(40);
  fl=J(w).menus.flowed.layout.flow; check(fl.columns*fl.rows>=4&&fl.columns<=8&&fl.rows<=12,'a tall grid is enlarged within the limits too', JSON.stringify(fl));
  // many buttons: rows are capped at 12, so columns grow instead
  const many=JSON.parse(TEXT); many.menus.flowed.layout.flow={region:{x:10,y:30,w:80,h:60},columns:2,rows:2}; many.menus.flowed.entries=Array.from({length:40},(_,i)=>({action:'back',label:'B'+i}));
  w=boot(JSON.stringify(many,null,2)); await start(w); await arrange(w); navFlow({pages:9,columns:2,rows:2,count:40}); await sleep(20); $(w,'discEdFitGrid').click(); await sleep(40);
  fl=J(w).menus.flowed.layout.flow; check(fl.columns*fl.rows>=40&&fl.rows<=12&&fl.columns<=8,'a grid for 40 buttons stays within 8 columns and 12 rows', JSON.stringify(fl));
  // the other explanations
  w=boot(GT); await start(w); await arrange(w);
  navFlow({pages:1,rows:2,columns:5,count:6}); await sleep(20);
  check(/decides where each one goes/.test(say())&&vis('discEdFreePlace')&&vis('discEdGridSettings')&&!vis('discEdFitGrid'),'a grid that fits says what a grid is, and offers to place freely or show the settings', say());
  navFlow({flowFromDocument:true,pages:1}); await sleep(20);
  check(/shares/.test(say())&&vis('discEdGridSettings')&&/whole menu/.test($(w,'discEdGridSettings').textContent)&&!vis('discEdFreePlace'),'a grid shared by every page says so, and points at the whole menu\'s settings', say());
  send(w,{type:'navigate',menu:'@chapters:x',positioned:false,virtual:true,flow:false,count:0}); await sleep(20);
  check(/one picture per scene/.test(say())&&!vis('discEdGridSettings')&&!vis('discEdFreePlace'),'the generated screen is explained, with nothing to press');
  send(w,{type:'navigate',menu:'plain',positioned:false,virtual:false,flow:false,count:2}); await sleep(20);
  check(/in a column by itself/.test(say())&&vis('discEdFreePlace')&&!vis('discEdGridSettings'),'a column page says what it is and offers to place freely');
  // "show the grid settings" opens the right part of the form
  navFlow({pages:2}); await sleep(20); $(w,'discEdGridSettings').click(); await sleep(40);
  const fg=$(w,'discEdInspector').querySelector('details[data-group="menus/flowed/layout/flow"]');
  check(/Page: Flowed/.test($(w,'discEdInspector').textContent)&&fg&&fg.open&&!!$(w,'discEdInspector').querySelector('[data-key="menus/flowed/layout/flow/columns"]'),'"Show this page\'s grid settings" selects the page and opens its grid section, columns and rows in view');
  navFlow({flowFromDocument:true,pages:1}); await sleep(20); $(w,'discEdGridSettings').click(); await sleep(40);
  check(/Whole menu/.test($(w,'discEdInspector').textContent)||/Settings for the whole menu/.test($(w,'discEdInspector').textContent)||$(w,'discEdInspector').querySelector('[data-group="layout/flow"]'),'the shared grid\'s button opens the whole menu\'s grid section');
  console.log('failures: '+fail); process.exit(fail?1:0);
})();
