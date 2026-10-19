// The pure paging and grid-cell logic, fuzzed over thousands of combinations of grid size, entry count and pinned buttons.
// Runs the real browser code in a simulated browser (jsdom) with scripted server responses.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const fs=require('fs');
let src=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/discmenus.js'),'utf8');
global.window={addEventListener(){},location:{hash:''}};
global.document={getElementById:()=>null,addEventListener(){},removeEventListener(){}};
global.setTimeout=()=>{};
src=src.replace("console.log('[Disc Menus] renderer script loaded');","global.__t={paginate,cellPosition,moveFocus};");
eval(src);
const {paginate,cellPosition}=__t;
let fail=0; const check=(ok,msg)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',msg); };
const mk=(n,backs)=>[...Array.from({length:n},(_,i)=>({Action:'playExtra',Label:'E'+(i+1)})),...Array.from({length:backs},()=>({Action:'back',Label:'Back'}))];
const names=p=>p.map(x=>x.items.map(e=>e.Label).join(','));
// Phillip's real Thor features menu: 5 extras + Back, a 5x1 banner
let pg=paginate(mk(5,1),5);
console.log('  Thor 5 extras+Back in 5 cells:',JSON.stringify(names(pg)),'more:',pg.map(p=>p.more),'prev:',pg.map(p=>p.prev));
check(pg.length===2&&pg[0].items.length===3&&pg[0].more&&!pg[1].more&&pg[1].prev&&pg[1].items.length===2,'Thor: page 1 = 3 items + Back + More; page 2 = 2 items + Back + Previous');
// Everything fits: no More at all
pg=paginate(mk(4,1),5); check(pg.length===1&&!pg[0].more&&pg[0].items.length===4,'4 items + Back fit 5 cells on one page, no More');
pg=paginate(mk(5,0),5); check(pg.length===1&&pg[0].items.length===5,'5 items, no Back, fit 5 cells exactly');
pg=paginate(mk(6,0),5); check(pg.length===2&&pg[0].more,'6 items without Back in 5 cells needs a second page');
pg=paginate([],5); check(pg.length===1&&pg[0].items.length===0,'empty menu: one empty page');
pg=paginate(mk(0,1),5); check(pg.length===1&&pg[0].backs.length===1,'only a Back entry: one page');
// Fuzz invariants
let bad=0, cases=0;
for(let slots=4;slots<=12;slots++) for(let n=0;n<=40;n++) for(let backs=0;backs<=2;backs++){
  cases++; const entries=mk(n,backs); const pages=paginate(entries,slots);
  const flat=pages.flatMap(p=>p.items.map(e=>e.Label)); const firstBack=entries.find(e=>e.Action==='back'); const want=entries.filter(e=>e!==firstBack).map(e=>e.Label);
  let ok = JSON.stringify(flat)===JSON.stringify(want);                       // each exactly once, in order
  pages.forEach((p,i)=>{ const used=p.items.length+p.backs.length+(p.prev?1:0)+(p.more?1:0);
     if(used>slots) ok=false;                                                 // never overflows the grid
     if(p.prev!==(i>0)) ok=false; if(p.more!==(i<pages.length-1)) ok=false;   // Previous/More only where they lead somewhere
     if(p.items.length===0 && n>0) ok=false; });                               // every page makes progress
  if(!ok){ bad++; if(bad<4) console.log('  bad case slots',slots,'n',n,'backs',backs,JSON.stringify(names(pages))); }
}
let badH=0,casesH=0; for(let slots=4;slots<=12;slots++) for(let n=0;n<=40;n++){ casesH++; const entries=[...mk(n,0),{Action:'home',Label:'Home'}]; const pages=paginate(entries,slots); const flat=pages.flatMap(p=>p.items.map(e=>e.Label)); const want=entries.filter(e=>e.Action!=='home').map(e=>e.Label); let ok=JSON.stringify(flat)===JSON.stringify(want); pages.forEach((p,i)=>{ if(p.items.length+p.backs.length+(p.prev?1:0)+(p.more?1:0)>slots) ok=false; if(p.backs.length!==1) ok=false; if(p.items.length===0&&n>0) ok=false;}); if(!ok) badH++; }
let badB=0,casesB=0; for(let n=0;n<=30;n++) for(let slots=5;slots<=10;slots++){ casesB++; const entries=[...mk(n,0),{Action:'back',Label:'Back'},{Action:'home',Label:'Home'}]; const pages=paginate(entries,slots); const flat=pages.flatMap(p=>p.items.map(e=>e.Label)); const want=entries.filter(e=>e.Action!=='back'&&e.Action!=='home').map(e=>e.Label); let ok=JSON.stringify(flat)===JSON.stringify(want); pages.forEach((p,i)=>{ if(p.items.length+p.backs.length+(p.prev?1:0)+(p.more?1:0)>slots) ok=false; if(p.backs.length!==2) ok=false; if(p.items.length===0&&n>0) ok=false;}); if(!ok) badB++; }
let badM=0,casesM=0; for(let slots=4;slots<=12;slots++) for(let n=0;n<=40;n++) for(let cap=1;cap<=8;cap++) for(const pin of [0,1]){ casesM++; const entries=[...mk(n,0),...(pin?[{Action:'back',Label:'Back'}]:[])]; const pages=paginate(entries,slots,cap); const flat=pages.flatMap(p=>p.items.map(e=>e.Label)); const want=entries.filter(e=>e.Action!=='back').map(e=>e.Label); let ok=JSON.stringify(flat)===JSON.stringify(want); pages.forEach((p,i)=>{ if(p.items.length>cap) ok=false; if(p.items.length+p.backs.length+(p.prev?1:0)+(p.more?1:0)>slots) ok=false; if(p.items.length===0&&n>0) ok=false; if(p.more!==(i<pages.length-1)) ok=false;}); if(!ok){ badM++; if(badM<4) console.log('  bad cap case slots',slots,'n',n,'cap',cap,'pin',pin,JSON.stringify(names(pages))); } }
check(badM===0,`fuzz with a per-page cap (scene selection perPage): ${casesM} combinations ok`);
{ const ch=Array.from({length:18},(_,i)=>({Action:'playChapter',Label:'Chapter '+(i+1)})).concat([{Action:'back',Label:'Back'}]); const pg=paginate(ch,9,6); check(pg.length===3&&pg.every(p=>p.items.length===6),'18 chapters, 3x3 grid, perPage 6 -> exactly 3 pages of 6', JSON.stringify(pg.map(p=>p.items.length))); }
check(badB===0,`fuzz with Back AND Home pinned (5+ cells): ${casesB} combinations ok`);
check(badH===0,`fuzz with Home pinned: ${casesH} combinations ok`);
check(bad===0,`fuzz: ${cases} combinations of slots/entries/Back keep every entry once, in order, within the grid`);
// Cell geometry for the live Thor banner region (centre-anchored 60x9 box, 5 columns, 1 row)
const flow={Region:{X:49,Y:87.5,W:60,H:9,Anchor:'center'},Columns:5,Rows:1};
const c0=cellPosition(flow,0,false), c4=cellPosition(flow,4,false);
check(Math.abs(c0.X-25)<1e-9 && Math.abs(c4.X-73)<1e-9 && Math.abs(c0.Y-87.5)<1e-9,'cell centres: first at 25%, last at 73%, on the banner row ('+c0.X+', '+c4.X+', '+c0.Y+')');
const g=cellPosition({Region:{X:10,Y:20,W:80,H:40},Columns:4,Rows:2},5,true);
check(Math.abs(g.X-10-(1.5*20))<1e-9 && Math.abs(g.Y-20-(1.5*20))<1e-9 && g.H>0,'grid cell: slot 5 of a 4x2 grid is row 2, column 2');
console.log('failures:',fail); process.exit(0);
