const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const context=vm.createContext({});
vm.runInContext(fs.readFileSync('tools/Workbench.MediaProbe/media-timings.js','utf8'),context);
test('browser timing uses a bounded same-clock ledger and matched decoded timestamps',()=>{
  let time=0;const timing=new context.ProbeMediaTimings(()=>time);
  timing.packet(11);time=8;timing.packet(22);time=12;timing.decodedFrame(11);time=15;timing.decodedFrame(22);
  const drawStart=time;time=17;timing.drawn(drawStart);
  const summary=timing.summary();assert.equal(summary.receiveToDecodeMs.samples,2);
  assert.equal(summary.receiveToDecodeMs.p95Ms,12);assert.equal(summary.packetIntervalMs.p50Ms,8);
  assert.equal(summary.drawCallMs.maxMs,2);assert.equal(summary.pending,0);
  assert.throws(()=>timing.decodedFrame(999),/lacks browser receive/);
});
test('browser timing rejects ledger overflow and retains only the latest 256 intervals',()=>{
  let time=0;const timing=new context.ProbeMediaTimings(()=>time);
  for(let id=0;id<300;id++){timing.packet(id);time++;timing.decodedFrame(id);time++;}
  assert.equal(timing.summary().receiveToDecodeMs.samples,256);
  for(let id=300;id<332;id++)timing.packet(id);
  assert.throws(()=>timing.packet(332),/ledger invalid or full/);
  assert.throws(()=>timing.packet(300),/ledger invalid or full/);
});
test('packet arrival timestamp includes time waiting in the browser message chain',()=>{
  let time=40;const timing=new context.ProbeMediaTimings(()=>time);
  timing.packet(7,10);time=50;timing.decodedFrame(7);
  assert.equal(timing.summary().receiveToDecodeMs.p95Ms,40);
});
