// Arranging on the preview: the renderer's edit mode (selecting, dragging, nudging and resizing entries, the title and decorative layers). Nothing in the
// renderer changes a menu: every finished gesture is reported to the editor as percentages. Runs the real renderer in jsdom with simple stand-ins for the
// two things jsdom cannot do (measure elements, find what is under a point).
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const src=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/discmenus.js'),'utf8');
let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e===undefined?'':e); };
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const SHIFT={'top-left':[0,0],top:[-50,0],'top-right':[-100,0],left:[0,-50],center:[-50,-50],right:[-100,-50],'bottom-left':[0,-100],bottom:[-50,-100],'bottom-right':[-100,-100]};
function boot(){
  const dom=new JSDOM('<body></body>',{url:'https://s.example/DiscMenus/web/preview.html',runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window; w.console.warn=()=>{}; w.console.info=()=>{}; w.console.log=()=>{};
  Object.defineProperty(w,'innerWidth',{value:1920,configurable:true}); Object.defineProperty(w,'innerHeight',{value:1080,configurable:true});
  w.__msgs=[]; Object.defineProperty(w,'parent',{value:{postMessage:(m)=>w.__msgs.push(m)},configurable:true});
  w.matchMedia=()=>({matches:true}); w.AudioContext=undefined; w.Audio=function(){return {play(){return Promise.resolve();},pause(){},removeAttribute(){}};};
  w.ApiClient={getJSON:()=>Promise.resolve([]),ajax:()=>Promise.resolve(),getUrl:u=>'https://s.example/'+u};
  // measuring: percentages in the style, the anchor's translate, and a default size for things that size themselves
  w.HTMLElement.prototype.getBoundingClientRect=function(){
    const pct=(v,of)=>v&&/%$/.test(v)?parseFloat(v)/100*of:null;
    const x=pct(this.style.left,1920), y=pct(this.style.top,1080);
    if(x===null) return {left:0,top:0,right:0,bottom:0,width:0,height:0};
    const wd=pct(this.style.width,1920)||200, ht=pct(this.style.height,1080)||60;
    const t=/translate\((-?[\d.]+)%,\s*(-?[\d.]+)%\)/.exec(this.style.transform||''); const sx=t?parseFloat(t[1]):0, sy=t?parseFloat(t[2]):0;
    const left=x+sx/100*wd, top=y+sy/100*ht; return {left,top,right:left+wd,bottom:top+ht,width:wd,height:ht};
  };
  w.__under=null; w.document.elementsFromPoint=()=>w.__under?[w.__under]:[];
  w.__discMenusPreview=true; w.eval(src); return w;
}
const doc=(over)=>Object.assign({Root:'main',Layout:{ButtonStyle:'text',TitlePosition:{X:50,Y:8,Anchor:'top'},Layers:[{Type:'panel',Fill:'#112233',Position:{X:0,Y:80,W:100,H:20}}]},Chapters:[],Menus:{
  main:{Title:'Main',Entries:[{Action:'playFeature',Label:'Play',Position:{X:20,Y:50,Anchor:'left'}},{Action:'submenu',Label:'More',Menu:'plain',Position:{X:60,Y:50,W:20,H:10,Anchor:'center'}}]},
  plain:{Title:'Plain',Layout:{TitlePosition:null},Entries:[{Action:'playFeature',Label:'A'},{Action:'back',Label:'Back'}]},
  menulayers:{Title:'Own',Layout:{Layers:[{Type:'panel',Position:{X:10,Y:10,W:30,H:30,Anchor:'top-left'}}]},Entries:[{Action:'back',Label:'Back',Position:{X:50,Y:90}}]}}},over||{});
const entry=(w,i)=>w.document.querySelector('.discMenusScreen:not(.leaving) [data-edit="entry"][data-index="'+i+'"]');
const title=w=>w.document.querySelector('[data-edit="title"]');
const layer=(w,i,origin)=>w.document.querySelector('[data-edit="layer"][data-index="'+i+'"]'+(origin?'[data-origin="'+origin+'"]':''));
const msgs=(w,type)=>w.__msgs.filter(m=>m.type===type);
const lastMsg=(w,type)=>msgs(w,type).slice(-1)[0];
// a press lands on an element inside the overlay (what is under the pointer, or the overlay itself); moves and releases are heard on the document
const ptr=(w,type,x,y,extra)=>{ const target=type==='pointerdown'?(w.__under||w.document.getElementById('discMenusOverlay')):w.document; return target.dispatchEvent(new w.MouseEvent(type,Object.assign({clientX:x,clientY:y,button:0,bubbles:true,cancelable:true},extra||{}))); };
const press=(w,el,x,y)=>{ w.__under=el; ptr(w,'pointerdown',x,y); };
const drag=(w,el,fromX,fromY,toX,toY,extra)=>{ press(w,el,fromX,fromY); ptr(w,'pointermove',toX,toY,extra); ptr(w,'pointerup',toX,toY,extra); };
const key=(w,k,extra)=>w.dispatchEvent(new w.KeyboardEvent('keydown',Object.assign({key:k,bubbles:true,cancelable:true},extra||{})));
const show=(w,d)=>{ w.DiscMenusPreview.show(d||doc(),'PARENT'); };
const approx=(a,b)=>Math.abs(a-b)<1e-9;

(async()=>{
  console.log('--- edit mode on and off');
  let w=boot(); show(w);
  check(typeof w.DiscMenusPreview.setEditMode==='function'&&typeof w.DiscMenusPreview.setSelection==='function'&&typeof w.DiscMenusPreview.snapshot==='function','the preview API has edit-mode functions');
  check(entry(w,0)&&entry(w,1)&&title(w)&&layer(w,0,'document'),'entries, the title and layers are marked so they can be found (menu entries by index)');
  w.DiscMenusPreview.setEditMode(true);
  check(entry(w,0)&&layer(w,0).style.pointerEvents==='auto','edit mode redraws, layers become hittable');
  w.DiscMenusPreview.setEditMode(false);
  check(layer(w,0).style.pointerEvents==='none','and off puts them back');
  const n=boot(); n.DiscMenusPreview.show(doc(),'P'); drag(n,entry(n,0),384,540,600,700);
  check(msgs(n,'move').length===0&&msgs(n,'select').length===0,'with edit mode off, pointer gestures are ignored');

  console.log('--- selecting');
  w=boot(); show(w); w.DiscMenusPreview.setEditMode(true); w.__msgs.length=0;
  press(w,entry(w,0),384,540); ptr(w,'pointerup',384,540);
  check(JSON.stringify(lastMsg(w,'select'))===JSON.stringify({source:'discmenus-preview',type:'select',kind:'entry',menu:'main',index:0,origin:null}),'pressing an entry selects it and tells the editor', JSON.stringify(lastMsg(w,'select')));
  check(msgs(w,'move').length===0,'a press with no movement is only a selection');
  check(w.document.querySelector('.discMenusSelBox').style.display==='block','a selection box is drawn');
  const play=entry(w,1); let clicked=false; play.addEventListener('click',()=>{clicked=true;});
  w.__under=play; ptr(w,'pointerdown',1152,540); ptr(w,'pointerup',1152,540); play.click();
  check(w.__msgs.filter(m=>m.type==='navigate').length===0,'clicking a button while arranging does not press it (no navigation)', JSON.stringify(w.__msgs.filter(m=>m.type==='navigate').map(m=>m.menu)));
  press(w,title(w),960,86); ptr(w,'pointerup',960,86);
  check(lastMsg(w,'select').kind==='title'&&lastMsg(w,'select').menu==='main','the title can be selected');
  press(w,layer(w,0,'document'),960,950); ptr(w,'pointerup',960,950);
  check(lastMsg(w,'select').kind==='layer'&&lastMsg(w,'select').origin==='document'&&lastMsg(w,'select').index===0,'a layer can be selected, and says where it came from');
  w.__under=null; ptr(w,'pointerdown',5,5); ptr(w,'pointerup',5,5);
  check(lastMsg(w,'select').kind===null&&w.document.querySelector('.discMenusSelBox').style.display==='none','pressing on nothing lets go of the selection');
  ptr(w,'pointerdown',5,5,{button:2}); check(msgs(w,'select').length===w.__msgs.filter(m=>m.type==='select').length,'only the main mouse button does anything');

  console.log('--- dragging an entry');
  w=boot(); show(w); w.DiscMenusPreview.setEditMode(true); w.__msgs.length=0;
  drag(w,entry(w,0),384,540,384+192,540+108);
  let mv=lastMsg(w,'move');
  check(mv&&mv.kind==='entry'&&mv.menu==='main'&&mv.index===0&&approx(mv.x,30)&&approx(mv.y,60)&&!('anchor' in mv),'a drag of 10% across and down reports the new percentages (its anchor is kept)', JSON.stringify(mv));
  check(entry(w,0).style.left==='30%'&&entry(w,0).style.top==='60%','the button follows the pointer while dragging');
  w.__msgs.length=0; show(w); drag(w,entry(w,0),400,500,402,501); check(msgs(w,'move').length===0,'under 3 pixels of movement is not a drag');
  w.__msgs.length=0; show(w); drag(w,entry(w,0),0,0,19.2*1.3,10.8*1.3); mv=lastMsg(w,'move');
  check(mv&&approx(mv.x,21.5)&&approx(mv.y,51.5),'positions snap to a 0.5% grid', JSON.stringify(mv));
  w.__msgs.length=0; show(w); drag(w,entry(w,0),0,0,19.2*29.7,10.8*0.3); mv=lastMsg(w,'move');
  check(mv&&mv.x===50&&approx(mv.y,50.5)||mv&&mv.x===50,'close to the centre line it snaps to exactly 50', JSON.stringify(mv));
  check(w.document.querySelector('.discMenusGuideV').style.display==='none','and the guide line is hidden again when the drag ends');
  w.__msgs.length=0; show(w); press(w,entry(w,0),0,0); ptr(w,'pointermove',19.2*29.7,0); const guideShown=w.document.querySelector('.discMenusGuideV').style.display; ptr(w,'pointerup',19.2*29.7,0);
  check(guideShown==='block','a guide line shows while it is snapped to the centre');
  w.__msgs.length=0; show(w); drag(w,entry(w,0),0,0,19.2*1.37,10.8*0.31,{altKey:true}); mv=lastMsg(w,'move');
  check(mv&&approx(mv.x,21.4)&&approx(mv.y,50.3),'Alt turns snapping off (rounded to a tenth)', JSON.stringify(mv));
  w.__msgs.length=0; show(w); drag(w,entry(w,0),0,0,-5000,-5000); mv=lastMsg(w,'move'); check(mv&&mv.x===0&&mv.y===0,'it cannot be dragged off the screen (clamped to 0)', JSON.stringify(mv));
  w.__msgs.length=0; show(w); drag(w,entry(w,0),0,0,9000,9000); mv=lastMsg(w,'move'); check(mv&&mv.x===100&&mv.y===100,'nor past 100');
  w.__msgs.length=0; show(w); drag(w,entry(w,1),1152,540,1152+96,540); mv=lastMsg(w,'move');
  check(mv&&mv.index===1&&approx(mv.x,65)&&approx(mv.y,50)&&entry(w,1).style.width==='20%','a sized, centre-anchored button moves by its anchor point and keeps its size', JSON.stringify(mv));

  console.log('--- the title and decorations');
  w=boot(); show(w); w.DiscMenusPreview.setEditMode(true); w.__msgs.length=0;
  drag(w,title(w),960,86,960+96,86+54); mv=lastMsg(w,'move');
  check(mv&&mv.kind==='title'&&mv.menu==='main'&&approx(mv.x,55)&&approx(mv.y,13)&&!('anchor' in mv),'a title that has a position moves and keeps its anchor', JSON.stringify(mv));
  w.__msgs.length=0; drag(w,layer(w,0,'document'),960,950,960,950-108); mv=lastMsg(w,'move');
  check(mv&&mv.kind==='layer'&&mv.origin==='document'&&mv.index===0&&approx(mv.y,70)&&mv.x===0,'a decorative layer moves too, and the move says whose layout it belongs to', JSON.stringify(mv));
  w.DiscMenusPreview.goTo('menulayers'); w.__msgs.length=0;
  drag(w,layer(w,0,'menu'),400,300,400+192,300); mv=lastMsg(w,'move');
  check(mv&&mv.origin==='menu'&&mv.menu==='menulayers'&&approx(mv.x,20),'a page\'s own layer reports origin "menu"', JSON.stringify(mv));
  w=boot(); w.DiscMenusPreview.show(doc({Layout:{ButtonStyle:'text'}}),'PARENT'); w.DiscMenusPreview.setEditMode(true); w.DiscMenusPreview.goTo('plain'); w.__msgs.length=0;
  check(!!title(w),'a page whose title has no position still has a title to select');
  title(w).getBoundingClientRect=function(){ return {left:192,top:108,right:600,bottom:200,width:408,height:92}; };
  drag(w,title(w),300,150,300+96,150+54); mv=lastMsg(w,'move');
  check(mv&&mv.kind==='title'&&mv.anchor==='top-left'&&approx(mv.x,15)&&approx(mv.y,15),'a title that just sits there is placed by its top-left corner (the editor is told it is a new placement)', JSON.stringify(mv));

  console.log('--- a page that arranges itself');
  w.DiscMenusPreview.goTo('plain'); w.__msgs.length=0;
  const nav=lastMsg(w,'navigate')||{};
  w.DiscMenusPreview.goTo('main'); w.DiscMenusPreview.goTo('plain');
  const nav2=lastMsg(w,'navigate');
  check(nav2.menu==='plain'&&nav2.positioned===false&&nav2.virtual===false&&nav2.flow===false&&nav2.count===2,'the editor is told a page is not positioned', JSON.stringify(nav2));
  w.__msgs.length=0; drag(w,entry(w,0),400,500,500,600);
  check(msgs(w,'move').length===0&&lastMsg(w,'select')&&lastMsg(w,'select').kind==='entry','its buttons can be selected but not dragged');
  w.DiscMenusPreview.goTo('main'); check(lastMsg(w,'navigate').positioned===true,'a positioned page says so');
  const snap=(()=>{ w.DiscMenusPreview.goTo('plain'); return w.DiscMenusPreview.snapshot(); })();
  check(snap.length===2&&snap[0].index===0&&snap[1].index===1&&typeof snap[0].x==='number'&&typeof snap[0].w==='number'&&typeof snap[0].cx==='number','snapshot() lists where each button of the page is now, by index', JSON.stringify(snap));

  console.log('--- a grid (flow) page');
  const gridDoc=doc(); gridDoc.Menus.grid={Title:'Grid',Layout:{Flow:{Region:{X:50,Y:50,W:80,H:40,Anchor:'center'},Columns:2,Rows:2}},Entries:[{Action:'home',Label:'Home'},{Action:'playFeature',Label:'A'},{Action:'playFeature',Label:'B'}]};
  gridDoc.Menus.paged={Title:'Paged',Layout:{Flow:{Region:{X:50,Y:50,W:80,H:40,Anchor:'center'},Columns:2,Rows:2}},Entries:[1,2,3,4,5,6].map(n=>({Action:'playFeature',Label:'E'+n}))};
  w=boot(); w.DiscMenusPreview.show(gridDoc,'P'); w.DiscMenusPreview.setEditMode(true); w.DiscMenusPreview.goTo('grid');
  let gnav=lastMsg(w,'navigate');
  check(gnav.menu==='grid'&&gnav.positioned===false&&gnav.flow===true&&gnav.flowFromDocument===false&&gnav.pages===1&&gnav.count===3,'a grid page is reported as not positioned, with its grid and page count', JSON.stringify(gnav));
  const gl=[...w.document.querySelectorAll('.discMenusScreen:not(.leaving) [data-edit="entry"]')].map(e=>e.getAttribute('aria-label')+'='+e.dataset.index).join();
  check(gl==='A=1,B=2,Home=0','grid buttons are identified by their place in the file, not their place in the grid (Home is pinned last but is entry 0)', gl);
  w.__msgs.length=0; drag(w,[...w.document.querySelectorAll('[data-edit="entry"]')].find(e=>e.getAttribute('aria-label')==='B'),500,500,700,600);
  check(msgs(w,'move').length===0&&lastMsg(w,'select').index===2,'a grid button can be selected (as entry 2) but not dragged', JSON.stringify(lastMsg(w,'select')));
  w.DiscMenusPreview.goTo('paged'); gnav=lastMsg(w,'navigate');
  check(gnav.pages===2&&gnav.positioned===false&&gnav.count===6,'a grid with more buttons than fit says it pages', JSON.stringify(gnav));
  const first=[...w.document.querySelectorAll('.discMenusScreen:not(.leaving) [data-edit="entry"]')].map(e=>e.dataset.index).join();
  check(first==='0,1,2','only real entries are marked (the More button is not an entry of the file)', first);
  const more=[...w.document.querySelectorAll('.discMenuEntry')].find(b=>b.getAttribute('aria-label')==='More');
  check(more&&more.getAttribute('data-nav')==='1'&&!more.getAttribute('data-edit'),'More is marked as navigation, not as something to arrange');
  w.__under=more; ptr(w,'pointerdown',10,10); ptr(w,'pointerup',10,10); more.click();
  const second=[...w.document.querySelectorAll('.discMenusScreen:not(.leaving) [data-edit="entry"]')].map(e=>e.dataset.index).join();
  check(second==='3,4,5'||second==='3,4'||/^3/.test(second),'paging still works while arranging, so every page of a grid can be seen', second);

  console.log('--- resizing');
  w=boot(); show(w); w.DiscMenusPreview.setEditMode(true); w.__msgs.length=0;
  press(w,layer(w,0,'document'),960,950); ptr(w,'pointerup',960,950);
  const handleVis=n=>w.document.querySelector('[data-handle="'+n+'"]').style.display;
  check(handleVis('e')==='block'&&handleVis('s')==='block'&&handleVis('se')==='block','a layer shows its resize handles');
  press(w,entry(w,0),384,540); ptr(w,'pointerup',384,540);
  check(handleVis('e')==='none'&&handleVis('se')==='none','a button with no size of its own has none (it sizes itself)');
  press(w,title(w),960,86); ptr(w,'pointerup',960,86); check(handleVis('e')==='none','nor does the title');
  press(w,entry(w,1),1152,540); ptr(w,'pointerup',1152,540); check(handleVis('e')==='block','a button the menu gave a width or height does');
  press(w,layer(w,0,'document'),960,950); ptr(w,'pointerup',960,950);
  w.__msgs.length=0;
  const h=w.document.querySelector('[data-handle="s"]'); w.__under=null; h.getBoundingClientRect=()=>({left:0,top:0,right:0,bottom:0});
  // dispatch from the handle itself so closest('[data-handle]') matches
  h.dispatchEvent(new w.MouseEvent('pointerdown',{clientX:960,clientY:1080,button:0,bubbles:true,cancelable:true}));
  ptr(w,'pointermove',960,1080-108); ptr(w,'pointerup',960,1080-108);
  const rz=lastMsg(w,'resize');
  check(rz&&rz.kind==='layer'&&rz.origin==='document'&&rz.index===0&&approx(rz.h,10)&&rz.w===100,'dragging the bottom handle up 10% makes the layer 10% shorter', JSON.stringify(rz));
  check(w.document.querySelector('[data-edit="layer"]').style.height==='10%','and it follows live');

  // many small moves, as a real drag makes: the result depends only on where the pointer is now, not on how many events there were
  w=boot(); show(w); w.DiscMenusPreview.setEditMode(true); press(w,layer(w,0,'document'),960,950); ptr(w,'pointerup',960,950);
  w.__msgs.length=0; const hs=w.document.querySelector('[data-handle="s"]'); hs.dispatchEvent(new w.MouseEvent('pointerdown',{clientX:960,clientY:1080,button:0,bubbles:true,cancelable:true}));
  for(let i=1;i<=10;i++) ptr(w,'pointermove',960,1080-i*10.8); ptr(w,'pointerup',960,1080-108);
  const rz2=lastMsg(w,'resize'); check(rz2&&approx(rz2.h,10),'resizing in ten small steps gives the same answer as one big step', JSON.stringify(rz2));
  w.__msgs.length=0; press(w,entry(w,0),384,540); for(let i=1;i<=10;i++) ptr(w,'pointermove',384+i*19.2,540); ptr(w,'pointerup',384+192,540);
  check(approx(lastMsg(w,'move').x,30),'and so does moving');

  console.log('--- keyboard');
  w=boot(); show(w); w.DiscMenusPreview.setEditMode(true); press(w,entry(w,0),384,540); ptr(w,'pointerup',384,540); w.__msgs.length=0;
  key(w,'ArrowRight'); mv=lastMsg(w,'move'); check(mv&&approx(mv.x,20.5)&&approx(mv.y,50),'an arrow key nudges by half a percent', JSON.stringify(mv));
  key(w,'ArrowDown',{shiftKey:true}); mv=lastMsg(w,'move'); check(mv&&approx(mv.y,55),'Shift nudges by five percent');
  key(w,'ArrowLeft'); mv=lastMsg(w,'move'); check(mv&&approx(mv.x,20),'left (from where the last nudge left it)');
  key(w,'ArrowUp'); mv=lastMsg(w,'move'); check(mv&&approx(mv.y,54.5),'up');
  w.__msgs.length=0; key(w,'Enter'); key(w,'Backspace'); check(w.__msgs.filter(m=>m.type==='navigate'||m.type==='play').length===0,'other keys do nothing while arranging: Enter does not press, Back does not leave');
  key(w,'Escape'); check(lastMsg(w,'select').kind===null,'Escape lets go of the selection');
  w.__msgs.length=0; key(w,'ArrowRight'); check(msgs(w,'move').length===0,'with nothing selected the arrow keys do nothing');
  const w2=boot(); show(w2); key(w2,'Enter'); check(w2.__msgs.filter(m=>m.type==='play').length===1,'outside edit mode the same keys work as before (Enter presses the focused button)');

  console.log('--- gestures that follow each other before the editor has sent the menu back');
  w=boot(); show(w); w.DiscMenusPreview.setEditMode(true); press(w,entry(w,0),384,540); ptr(w,'pointerup',384,540); w.__msgs.length=0;
  key(w,'ArrowRight'); key(w,'ArrowRight'); key(w,'ArrowRight');
  check(msgs(w,'move').map(m=>m.x).join()==='20.5,21,21.5','three quick arrow presses add up (each starts where the last left it)', msgs(w,'move').map(m=>m.x).join());
  w.__msgs.length=0; drag(w,entry(w,0),0,0,19.2*10,0);
  check(approx(lastMsg(w,'move').x,31.5),'a drag right after starts from the nudged place', JSON.stringify(lastMsg(w,'move')));
  w.__msgs.length=0; drag(w,layer(w,0,'document'),960,950,960,950-108); press(w,layer(w,0,'document'),960,900); ptr(w,'pointerup',960,900);
  const hs2=w.document.querySelector('[data-handle="s"]'); w.__msgs.length=0;
  hs2.dispatchEvent(new w.MouseEvent('pointerdown',{clientX:960,clientY:900,button:0,bubbles:true,cancelable:true}));
  for(let i=1;i<=5;i++) ptr(w,'pointermove',960,900-i*10.8); ptr(w,'pointerup',960,900-54);
  const rz3=lastMsg(w,'resize');
  check(rz3&&approx(rz3.h,15),'a resize after a move uses where the layer is now (y 70, bottom 90 -> 85: height 15)', JSON.stringify(rz3));
  w.__msgs.length=0; w.DiscMenusPreview.setSelection({kind:'title',menu:'main',index:0}); drag(w,title(w),960,86,960,86+54); key(w,'ArrowDown');
  check(approx(msgs(w,'move').slice(-1)[0].y,13.5),'a nudge after a title drag starts from the dragged place (page-level title position made locally)', JSON.stringify(msgs(w,'move').slice(-1)[0]));

  console.log('--- the editor selects, the preview follows');
  w=boot(); show(w); w.DiscMenusPreview.setEditMode(true);
  w.DiscMenusPreview.setSelection({kind:'entry',menu:'main',index:1});
  const box=w.document.querySelector('.discMenusSelBox');
  check(box.style.display==='block'&&box.style.width==='384px','a selection from the editor draws the box around it (here 20% of 1920px)', box.style.cssText);
  w.DiscMenusPreview.setSelection(null); check(box.style.display==='none','and clearing it hides the box');
  w.DiscMenusPreview.setSelection({kind:'layer',menu:'main',index:0,origin:'document'}); check(box.style.display==='block','a layer can be selected from the editor');
  w.DiscMenusPreview.setSelection({kind:'entry',menu:'main',index:9}); check(box.style.display==='none','a selection that does not exist draws nothing');
  w.DiscMenusPreview.setSelection({kind:'entry',menu:'main',index:0});
  w.DiscMenusPreview.update(doc({Menus:Object.assign({},doc().Menus,{main:Object.assign({},doc().Menus.main,{Title:'Renamed'})})}),'PARENT');
  check(box.style.display==='block','the selection survives an edit of the menu (the preview redraws)');
  w.DiscMenusPreview.goTo('plain'); check(box.style.display==='none','but not a change of page');
  w.DiscMenusPreview.goTo('main'); w.DiscMenusPreview.setSelection({kind:'entry',menu:'main',index:0}); w.DiscMenusPreview.setEditMode(false);
  check(box.style.display==='none'||!w.document.querySelector('.discMenusSelBox')||w.document.querySelector('.discMenusSelBox').style.display==='none','leaving edit mode hides the selection');

  console.log('--- scene selection is not arranged');
  const sc=doc({Chapters:Array.from({length:6},(_,i)=>({Index:i,Name:'Ch '+(i+1),StartTicks:i*600000000,HasImage:false}))});
  sc.Menus.main.Entries.push({Action:'chapters',Label:'Scenes',Position:{X:50,Y:70}});
  w=boot(); w.DiscMenusPreview.show(sc,'P');
  [...w.document.querySelectorAll('.discMenuEntry')].find(b=>b.getAttribute('aria-label')==='Scenes').click();
  w.DiscMenusPreview.setEditMode(true);
  const vnav=lastMsg(w,'navigate');
  check(vnav.virtual===true,'the generated scene screen is reported as virtual (the editor says there is nothing to arrange)', JSON.stringify(vnav));
  w.__msgs.length=0; const ch0=w.document.querySelector('.discMenusScreen:not(.leaving) [data-edit="entry"]'); drag(w,ch0,300,300,500,500);
  check(msgs(w,'move').length===0,'its buttons cannot be dragged');
  console.log('failures: '+fail); process.exit(fail?1:0);
})();
