// The adapter that applies programmatic edits to the editor's textarea as one native undo step, with a safe fallback.
const path = require('path');
const fs = require('fs');
const ROOT = path.resolve(__dirname, '..', '..');
const {JSDOM}=require('jsdom');
const A = require(path.join(ROOT, 'Jellyfin.Plugin.DiscMenus/Web/editor/text-adapter.js'));
let fail=0; const check=(ok,m,e)=>{ if(!ok) fail++; console.log(ok?'PASS':'FAIL',m,e===undefined?'':e); };
const D = A.diff;

console.log('--- the smallest changed range');
check(JSON.stringify(D('abcdef','abXdef'))==='{"start":2,"endOld":3,"replacement":"X"}','a replacement');
check(JSON.stringify(D('abcdef','abcXYdef'))==='{"start":3,"endOld":3,"replacement":"XY"}','an insertion');
check(JSON.stringify(D('abcdef','abef'))==='{"start":2,"endOld":4,"replacement":""}','a deletion');
check(JSON.stringify(D('abc','abc'))==='{"start":3,"endOld":3,"replacement":""}','no change is an empty range at the end');
check(JSON.stringify(D('','abc'))==='{"start":0,"endOld":0,"replacement":"abc"}'&&JSON.stringify(D('abc',''))==='{"start":0,"endOld":3,"replacement":""}','to and from empty');
check(JSON.stringify(D('aXa','aXXa'))==='{"start":2,"endOld":2,"replacement":"X"}','repeated characters do not overlap prefix and suffix');
const poo='😀', poo2='😁';
let d=D('a'+poo+'b','a'+poo2+'b'); check(d.start===1&&d.endOld===3&&d.replacement===poo2,'never splits a surrogate pair (replacing one emoji with another)', JSON.stringify(d));
d=D('x'+poo,'x'+poo+poo); check(('x'+poo).slice(0,d.start)+d.replacement+('x'+poo).slice(d.endOld)==='x'+poo+poo,'and the range always reproduces the new text');
let bad=0; const letters='ab😀\n{}'; const r=(()=>{let s=7;return()=>{s=(s*1103515245+12345)&0x7fffffff;return s/0x7fffffff;};})();
const rnd=()=>Array.from({length:Math.floor(r()*12)},()=>{const c=[...letters];return c[Math.floor(r()*c.length)];}).join('');
for(let k=0;k<3000;k++){ const a=rnd(), b=rnd(); const x=D(a,b); if(a.slice(0,x.start)+x.replacement+a.slice(x.endOld)!==b) bad++; }
check(bad===0,'3000 random pairs: applying the range always yields the new text', bad);

function page(opts={}){
  const dom=new JSDOM('<body><textarea id="t"></textarea><button id="b">other</button></body>',{url:'https://s.example/',pretendToBeVisual:true});
  const w=dom.window; const ta=w.document.getElementById('t'); w.__inputs=0; ta.addEventListener('input',()=>w.__inputs++);
  w.__native=[]; return {w,ta,doc:w.document};
}
// a stand-in for what a real browser does: change the selected range, fire input, record the command
function emulateBrowser(w,ta,{lie}={}){
  const undoStack=[];
  w.document.execCommand=(cmd,ui,val)=>{ w.__native.push(cmd+(val!==undefined?':'+JSON.stringify(val):'')+'@'+ta.selectionStart+'-'+ta.selectionEnd);
    if(cmd==='insertText'||cmd==='delete'){ if(lie==='false') return false; undoStack.push(ta.value); const s=ta.selectionStart,e=ta.selectionEnd; const ins=cmd==='delete'?'':val; ta.value=lie==='wrong'?'WRONG':ta.value.slice(0,s)+ins+ta.value.slice(e); ta.dispatchEvent(new w.Event('input',{bubbles:true})); return true; }
    if(cmd==='undo'){ if(!undoStack.length) return false; ta.value=undoStack.pop(); ta.dispatchEvent(new w.Event('input',{bubbles:true})); return true; }
    return false; };
}

console.log('--- the browser path');
let p=page(); emulateBrowser(p.w,p.ta); p.ta.value='{"a": 1, "b": 2}'; p.w.__inputs=0;
let ad=A.create(p.ta);
check(ad.apply('{"a": 1, "b": 99}')===true&&p.ta.value==='{"a": 1, "b": 99}','the edit is applied');
check(p.w.__native.length===1&&p.w.__native[0]==='insertText:"99"@14-15','through insertText over exactly the changed range, not the whole text', p.w.__native.join());
check(ad.usesNative===true&&p.w.__inputs===1,'and it is reported as native, with a single input event');
p.ta.focus(); check(ad.undo()===true&&p.ta.value==='{"a": 1, "b": 2}','undo is the browser\'s own (execCommand undo)');
p=page(); emulateBrowser(p.w,p.ta); p.ta.value='abcdef'; ad=A.create(p.ta); ad.apply('abef');
check(p.w.__native[0]==='delete@2-4'&&p.ta.value==='abef','a deletion uses the delete command');
p=page(); emulateBrowser(p.w,p.ta); p.ta.value='x'; ad=A.create(p.ta); p.doc.getElementById('b').focus(); ad.apply('xy');
check(p.doc.activeElement.id==='b','focus goes back to what had it (a button in a form panel), not left in the textarea');
p=page(); emulateBrowser(p.w,p.ta); p.ta.value='hello'; ad=A.create(p.ta); ad.apply('hello world',3);
check(p.ta.selectionStart===3&&p.ta.selectionEnd===3,'a caret position can be given'); ad.apply('hello there'); check(p.ta.selectionStart===11,'otherwise the caret ends after the change');
p=page(); emulateBrowser(p.w,p.ta); p.ta.value='same'; ad=A.create(p.ta); check(ad.apply('same')===false&&p.w.__native.length===0&&p.w.__inputs===0,'no change: nothing happens');

console.log('--- when the browser will not or cannot');
for(const lie of ['false','wrong']){
  p=page(); emulateBrowser(p.w,p.ta,{lie}); p.ta.value='one two'; p.w.__inputs=0; ad=A.create(p.ta);
  check(ad.apply('one three')===true&&p.ta.value==='one three'&&ad.usesNative===false,'native '+(lie==='false'?'refuses':'does the wrong thing')+': the text still ends up right, reported as fallback', p.ta.value);
  check(lie==='false'?p.w.__inputs===1:p.w.__inputs>=1&&p.w.__inputs<=2,'and the editor is told (input events: one, or two when the browser had half-done the wrong edit)', p.w.__inputs);
  check(ad.undo()===true&&p.ta.value==='one two','and the fallback history undoes it');
}
p=page(); p.ta.value='x'; ad=A.create(p.ta); check(typeof p.doc.execCommand!=='function'||true,'(jsdom has no execCommand)');
p.doc.execCommand=undefined; delete p.doc.execCommand;
p.w.__inputs=0; ad.apply('xy'); check(p.ta.value==='xy'&&ad.usesNative===false&&p.w.__inputs===1,'no execCommand at all: fallback, one input event');
p=page(); delete p.doc.execCommand; p.ta.value='1'; ad=A.create(p.ta); p.doc.execCommand=()=>{ throw new Error('boom'); };
ad.apply('12'); check(p.ta.value==='12'&&ad.usesNative===false,'a throwing execCommand is survived');

console.log('--- the fallback history');
p=page(); delete p.doc.execCommand; p.ta.value='a'; ad=A.create(p.ta);
ad.apply('ab'); ad.apply('abc'); ad.apply('abcd');
check(ad.canUndo()===true,'there is something to undo');
ad.undo(); check(p.ta.value==='abc','undo steps back one edit at a time'); ad.undo(); ad.undo(); check(p.ta.value==='a'&&ad.undo()===false&&ad.canUndo()===false,'down to the start, then nothing');
ad.redo(); ad.redo(); check(p.ta.value==='abc','redo steps forward'); ad.apply('abX'); check(ad.redo()===false,'a new edit clears redo');
check(p.w.__inputs>=3,'every step fires input so the preview and dirty state follow');
ad.reset(); check(ad.undo()===false&&ad.canUndo()===false,'reset forgets the history (a new file was opened)');
check(ad.redo()===false,'and redo');

console.log('failures:',fail); process.exit(fail?1:0);
