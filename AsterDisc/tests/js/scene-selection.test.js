// Scene selection generated from chapters: paging, thumbnails, playing from a chapter.
// Runs the real browser code in a simulated browser (jsdom) with scripted server responses.
const path = require('path');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom'); const fs=require('fs');
const src=fs.readFileSync(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/discmenus.js'),'utf8');
let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e===undefined?'':e); };
const PARENT='4cf4efb9cb2c96b4ec157568bcccdf7f';
function boot(doc,alerts){
  const dom=new JSDOM('<body></body>',{url:'https://s.example/web/#/details?id='+PARENT,runScripts:'outside-only',pretendToBeVisual:true});
  const w=dom.window; w.console.warn=()=>{}; w.console.info=()=>{};
  w.__calls=[]; w.alert=(m)=>alerts&&alerts.push(m);
  w.ApiClient={
    getJSON:(url)=>url.includes('Sessions')?Promise.resolve([{Id:'sess1'}]):Promise.resolve(doc),
    ajax:(o)=>{ w.__calls.push(o.url); return Promise.resolve(); },
    getUrl:(u,p)=>'https://s.example/'+u+(p?'?'+Object.entries(p).filter(([,v])=>v!==undefined).map(([k,v])=>k+'='+v).join('&'):''),
    getImageUrl:()=>'x',deviceId:()=>'d',accessToken:()=>'T'};
  w.eval(src); return w;
}
const chapters=(n,withImages)=>Array.from({length:n},(_,i)=>({Index:i,Name:'Chapter '+(i+1),StartTicks:i*47000000000/10*10,HasImage:!!withImages,ImageStamp:withImages?111:null}));
const feat=[{Action:'playFeature',Label:'Play',Position:{X:30,Y:80,W:18}},{Action:'chapters',Label:'Scene Selection',PerPage:6,Position:{X:70,Y:80,W:18}}];
const mk=(entries,extra)=>Object.assign({Root:'main',Layout:{ButtonStyle:'frame'},Theme:{Id:'x',Accent:'#f0f'},Chapters:chapters(18,false),Menus:{main:{Title:'Main',Entries:entries}}},extra||{});
const names=(w)=>[...w.document.querySelectorAll('.discMenuEntry')].map(b=>b.getAttribute('aria-label'));
const click=(w,l)=>{ const b=[...w.document.querySelectorAll('.discMenuEntry')].find(x=>x.getAttribute('aria-label')===l); if(!b) throw new Error('no "'+l+'" in '+names(w)); b.click(); };
const open=async(w)=>{ await new Promise(r=>setTimeout(r,60)); w.document.getElementById('discMenusButton').click(); };
(async()=>{
  // 1) default scene selection, text-only (Thor: chapters but no thumbnails)
  let w=boot(mk(feat)); await open(w); click(w,'Scene Selection');
  check(names(w).join()==='Chapter 1,Chapter 2,Chapter 3,Chapter 4,Chapter 5,Chapter 6,Back,More','page 1: six chapters, then Back and More', names(w).join());
  const b1=[...w.document.querySelectorAll('.discMenuEntry')][0];
  check(b1.textContent.includes('Chapter 1')&&b1.textContent.includes('0:00'),'text button shows name + start time on its own line', JSON.stringify(b1.textContent));
  const pos=[...w.document.querySelectorAll('.discMenuEntry')].map(b=>[parseFloat(b.style.left),parseFloat(b.style.top)]);
  const rows=new Set(pos.slice(0,6).map(p=>Math.round(p[1]))), navRow=Math.round(pos[6][1]);
  check(rows.size===2 && navRow>Math.max(...rows),'six thumbnails fill two rows; navigation sits in a third row below', [...rows].join('/')+' then '+navRow);
  click(w,'More'); check(names(w).join()==='Chapter 7,Chapter 8,Chapter 9,Chapter 10,Chapter 11,Chapter 12,Back,Previous,More','page 2: chapters 7-12 with Back, Previous, More', names(w).join());
  click(w,'More'); check(names(w).join()==='Chapter 13,Chapter 14,Chapter 15,Chapter 16,Chapter 17,Chapter 18,Back,Previous','page 3 (last): chapters 13-18, no More', names(w).join());
  // 2) clicking a chapter plays the feature from that chapter's start
  click(w,'Chapter 14'); await new Promise(r=>setTimeout(r,30));
  const play=w.__calls.find(u=>u.includes('Sessions/sess1/Playing'));
  const expectTicks=chapters(18)[13].StartTicks;
  check(!!play&&play.includes('PlayCommand=PlayNow')&&play.includes('ItemIds='+PARENT)&&play.includes('StartPositionTicks='+expectTicks),'chapter button sends PlayNow for the feature with that chapter\'s StartPositionTicks', play&&play.split('?')[1]);
  // 3) Play Movie with startChapter
  w=boot(mk([{Action:'playFeature',Label:'From 3',StartChapter:3,Position:{X:50,Y:50,W:20}}])); await open(w); click(w,'From 3'); await new Promise(r=>setTimeout(r,30));
  const p3=w.__calls.find(u=>u.includes('/Playing'));
  check(!!p3&&p3.includes('StartPositionTicks='+chapters(18)[2].StartTicks),'playFeature startChapter:3 starts at chapter 3 (previously ignored)', p3&&p3.split('?')[1]);
  w=boot(mk([{Action:'playFeature',Label:'Play',Position:{X:50,Y:50,W:20}}])); await open(w); click(w,'Play'); await new Promise(r=>setTimeout(r,30));
  const p4=w.__calls.find(u=>u.includes('/Playing')); check(!!p4&&!p4.includes('StartPositionTicks'),'plain Play Movie sends no start position');
  // 4) thumbnails when Jellyfin has chapter images
  w=boot(mk(feat,{Chapters:chapters(18,true)})); await open(w); click(w,'Scene Selection');
  const t1=[...w.document.querySelectorAll('.discMenuEntry')][0]; const im=t1.querySelector('img');
  check(!!im&&im.src==='https://s.example/Items/'+PARENT+'/Images/Chapter/0?maxWidth=480&tag=111','chapter thumbnail uses the server chapter-image URL', im&&im.src);
  check(t1.textContent.includes('Chapter 1')&&t1.textContent.includes('0:00'),'thumbnail button is captioned with name and time');
  check(w.document.querySelectorAll('.discMenuEntry img').length===6,'six thumbnails on the page');
  // 5) styling menu: own title, flow grid, pinned Home; thumbnails text-only
  const styled=mk([{Action:'playFeature',Label:'Play',Position:{X:30,Y:80,W:18}},{Action:'chapters',Label:'Scenes',PerPage:4,Menu:'scenes',Position:{X:70,Y:80,W:18}}],
    {Menus:{main:{Title:'Main',Entries:[{Action:'playFeature',Label:'Play',Position:{X:30,Y:80,W:18}},{Action:'chapters',Label:'Scenes',PerPage:4,Menu:'scenes',Position:{X:70,Y:80,W:18}}]},
      scenes:{Title:'Pick a Scene',Layout:{HideTitle:false,Flow:{Region:{X:50,Y:50,W:80,H:30,Anchor:'center'},Columns:4,Rows:2,MoreLabel:'More Scenes'}},Entries:[{Action:'home',Label:'Main Menu'}]}}});
  w=boot(styled); await open(w); click(w,'Scenes');
  check(w.document.querySelector('h1')&&w.document.querySelector('h1').textContent==='Pick a Scene','styling menu supplies the screen title');
  check(names(w).join()==='Chapter 1,Chapter 2,Chapter 3,Chapter 4,Main Menu,More Scenes','styling menu: perPage 4 chapters, its own Home pinned, its custom More label', names(w).join());
  click(w,'Main Menu'); check(names(w).join()==='Play,Scenes','pinned Home from the scene screen returns to the root');
  // 6) Back key / Back button from scene selection
  w=boot(mk(feat)); await open(w); click(w,'Scene Selection'); click(w,'More');
  w.document.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}));
  w.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}));
  check(names(w).includes('Chapter 1')||names(w).join()==='Play,Scene Selection','Back key steps out of the second page', names(w).join());
  // 7) no chapters: tells the user instead of opening an empty screen
  const alerts=[]; w=boot(mk(feat,{Chapters:[]}),alerts); await open(w); click(w,'Scene Selection');
  check(alerts.length===1&&/no chapter/i.test(alerts[0])&&names(w).join()==='Play,Scene Selection','title with no chapters: message shown, stays on the menu', alerts[0]);
  console.log('failures:',fail); process.exit(0);
})();
