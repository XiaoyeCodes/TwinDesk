const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict'),test=require('node:test');
for(const name of ['scene-timeline','frame-presenter'])vm.runInThisContext(fs.readFileSync(`tools/Workbench.MediaProbe/${name}.js`,'utf8'));
const scene=version=>({version,width:1280,height:720,nodeCount:1,contentRect:{x:0,y:0,width:1280,height:720}});
function setup(delayFirstMs=3000){
  const timeline=new ProbeSceneTimeline(1280,720),draws=[],errors=[],timers=[];
  timeline.announce(scene(1));
  const presenter=new ProbeFramePresenter({timeline,delayFirstMs,present:(f,m)=>draws.push(m),error:e=>errors.push(e),
    schedule:fn=>{timers.push(fn);return timers.length;},unschedule:id=>{timers[id-1].cancelled=true;}});
  const frame=(timestamp,version,sequence)=>{timeline.submit(timestamp,version,sequence);return {timestamp,closed:0,close(){this.closed++;}};};
  return {timeline,presenter,draws,errors,timers,frame};
}
test('delayed decoded old scene never draws or acknowledges the new scene',async()=>{
  const t=setup(),old=t.frame(0,1,1);t.presenter.accept(old);
  t.timeline.announce(scene(2));const next=t.frame(10,2,2);t.presenter.accept(next);
  let drained=false;const settling=t.presenter.settled().then(()=>drained=true);await Promise.resolve();assert.equal(drained,false);
  t.timers[0]();await settling;
  assert.deepEqual(t.draws.map(m=>m.sequence),[2]);assert.equal(t.presenter.stats.staleScenes,1);
  assert.equal(old.closed,1);assert.equal(next.closed,1);assert.equal(t.timeline.pending.size,0);assert.equal(t.errors.length,0);
});
test('same scene delayed output cannot rewind a newer displayed frame',()=>{
  const t=setup(),old=t.frame(0,1,1),next=t.frame(10,1,2);
  t.presenter.accept(old);t.presenter.accept(next);t.timers[0]();
  assert.deepEqual(t.draws.map(m=>m.sequence),[2]);assert.equal(t.presenter.stats.staleSequences,1);assert.equal(old.closed,1);
});
test('cancel releases held frames, retires metadata, and stale timers cannot touch a replacement run',async()=>{
  const old=setup(),frame=old.frame(0,1,1);old.presenter.accept(frame);const pending=old.presenter.settled();
  old.presenter.close();old.presenter.close();await pending;
  const replacement=setup(0),fresh=replacement.frame(0,1,1);replacement.presenter.accept(fresh);
  old.timers[0](); // A queued event can still fire despite clearTimeout; ownership check must reject it.
  assert.equal(old.timers[0].cancelled,true);assert.equal(frame.closed,1);assert.equal(old.presenter.stats.closedHeld,1);
  assert.equal(old.timeline.pending.size,0);assert.equal(old.draws.length,0);assert.equal(replacement.draws.length,1);
  assert.throws(()=>old.timeline.submit(10,1,2));assert.throws(()=>old.timeline.announce(scene(2)));
});
test('late native decoder callback after close is released without presentation or error',()=>{
  const t=setup(0),frame=t.frame(0,1,1);t.presenter.close();t.presenter.accept(frame);
  assert.equal(frame.closed,1);assert.equal(t.presenter.stats.lateCallbacks,1);assert.equal(t.draws.length,0);assert.equal(t.errors.length,0);
});
test('normal mode retains no frame and closes even if drawing throws',()=>{
  const t=setup(0),frame=t.frame(0,1,1);t.presenter.present=()=>{throw Error('draw failed');};t.presenter.accept(frame);
  assert.equal(frame.closed,1);assert.equal(t.presenter.held,null);assert.equal(t.errors.length,1);
});
test('missing metadata releases the actual output and reports the association failure',()=>{
  const t=setup(0),frame={timestamp:123,closed:0,close(){this.closed++;}};t.presenter.accept(frame);
  assert.equal(frame.closed,1);assert.equal(t.errors.length,1);assert.equal(t.draws.length,0);
});
test('unsupported delay cannot expand the diagnostic resource budget',()=>{
  for(const delayFirstMs of [-1,1,3001,Infinity,NaN])assert.throws(()=>new ProbeFramePresenter({delayFirstMs}));
});
test('throttled background timer cannot strand a held output at end of stream',async()=>{
  const t=setup();let now=0;t.presenter.now=()=>now;const frame=t.frame(0,1,1);t.presenter.accept(frame);
  now=3100;await t.presenter.settled();assert.equal(frame.closed,1);assert.equal(t.presenter.held,null);assert.equal(t.draws.length,1);
  t.timers[0]();assert.equal(frame.closed,1);
});
test('incoming outputs reap overdue frames even when the browser timer never fires',()=>{
  const t=setup();let now=0;t.presenter.now=()=>now;const old=t.frame(0,1,1);t.presenter.accept(old);
  t.timeline.announce(scene(2));now=3100;t.presenter.accept(t.frame(10,2,2));
  assert.equal(old.closed,1);assert.equal(t.presenter.stats.staleScenes,1);assert.equal(t.presenter.held,null);
});
test('default timer wrappers preserve the browser Window receiver',()=>{
  const originalSet=globalThis.setTimeout,originalClear=globalThis.clearTimeout;let scheduled=0,cleared=0;
  try {
    globalThis.setTimeout=function(){assert.equal(this,globalThis);scheduled++;return 1;};
    globalThis.clearTimeout=function(){assert.equal(this,globalThis);cleared++;};
    const timeline=new ProbeSceneTimeline(1280,720);timeline.announce(scene(1));timeline.submit(0,1,1);
    const presenter=new ProbeFramePresenter({timeline,present(){},error:e=>assert.fail(e),delayFirstMs:3000});
    presenter.accept({timestamp:0,close(){}});presenter.close();assert.equal(scheduled,1);assert.equal(cleared,1);
  }finally{globalThis.setTimeout=originalSet;globalThis.clearTimeout=originalClear;}
});
