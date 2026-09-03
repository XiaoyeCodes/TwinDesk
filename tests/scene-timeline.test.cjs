const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict'),test=require('node:test');
vm.runInThisContext(fs.readFileSync('tools/Workbench.MediaProbe/scene-timeline.js','utf8'));
const scene=version=>({version,width:1280,height:720,nodeCount:1,contentRect:{x:0,y:0,width:1280,height:720}});
test('delayed old decoder callback is retired, not relabeled',()=>{
  const timeline=new ProbeSceneTimeline(1280,720);timeline.announce(scene(1));timeline.submit(0,1,1);
  timeline.announce(scene(2));timeline.submit(33333,2,2);
  const old=timeline.complete(0);assert.equal(old.present,false);assert.equal(old.scene.version,1);
  const next=timeline.complete(33333);assert.equal(next.present,true);assert.equal(next.scene.version,2);assert.equal(timeline.pending.size,0);
});
test('invalid output, stale announcement and stale submitted frame fail',()=>{
  const timeline=new ProbeSceneTimeline(1280,720);timeline.announce(scene(2));
  assert.throws(()=>timeline.announce(scene(1)));assert.throws(()=>timeline.submit(0,1,1));assert.throws(()=>timeline.complete(0));
});
test('caller mutation cannot alter a queued frame geometry',()=>{
  const config=scene(1),timeline=new ProbeSceneTimeline(1280,720);timeline.announce(config);timeline.submit(0,1,1);
  config.contentRect.x=500;config.version=2;assert.equal(timeline.complete(0).scene.contentRect.x,0);
});
test('invalid geometry and counters fail before allocation',()=>{
  for(const config of [{...scene(1),version:NaN},{...scene(1),nodeCount:9},{...scene(1),contentRect:{x:-1,y:0,width:1,height:1}},{...scene(1),width:1920}]){
    assert.throws(()=>new ProbeSceneTimeline(1280,720).announce(config));
  }
});
test('pending callback map has a hard limit and rejects timestamp reuse',()=>{
  const timeline=new ProbeSceneTimeline(1280,720);timeline.announce(scene(1));
  for(let i=0;i<16;i++)timeline.submit(i,1,i+1);
  assert.throws(()=>timeline.submit(16,1,17));timeline.complete(0);assert.throws(()=>timeline.submit(15,1,17));
});
