const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const context=vm.createContext({});
vm.runInContext(fs.readFileSync('tools/Workbench.MediaProbe/input-client.js','utf8')+';this.Queue=InputMoveQueue',context);
function fixture(){
  const sent=[],errors=[];let time=0,valid=true;
  const q=new context.Queue(e=>{sent.push(e);return sent.length;},()=>valid,()=>time,e=>errors.push(e));
  const event=(kind='Move',n=0,boundary='scene1')=>({kind,payload:{u:n/1000},boundary,at:time,release:['ButtonUp','KeyUp','ReleaseAll'].includes(kind)});
  return {q,sent,errors,event,setTime:t=>time=t,invalidate:()=>valid=false};
}
test('1000 unsent moves become latest coordinate without flooding the wire',()=>{
  const f=fixture();for(let i=0;i<1000;i++)f.q.enqueue(f.event('Move',i));
  assert.equal(f.sent.length,1);assert.equal(f.q.pending.length,1);assert.equal(f.q.merged,998);
  f.q.acknowledge(1);assert.equal(f.sent[1].payload.u,.999);assert.equal(f.q.pending.length,0);
});
test('button key wheel and scene changes split movement batches and preserve order',()=>{
  const f=fixture();const kinds=['Move','Move','ButtonDown','Move','KeyDown','Move','Wheel','Move','ButtonUp','KeyUp'];
  kinds.forEach((kind,i)=>f.q.enqueue(f.event(kind,i)));
  for(let i=1;i<=kinds.length;i++)f.q.acknowledge(i);
  assert.deepEqual(f.sent.map(e=>e.kind),kinds);
  f.q.enqueue(f.event('Move',1,'scene1'));f.q.enqueue(f.event('Move',2,'scene2'));f.q.enqueue(f.event('Move',3,'scene3'));
  assert.equal(f.q.pending.length,2);
});
test('release all discards pending editing and late prior acknowledgment cannot flush new work',()=>{
  const f=fixture();f.q.enqueue(f.event('ButtonDown'));f.q.enqueue(f.event('Move'));f.q.enqueue(f.event('KeyDown'));
  f.q.enqueue(f.event('ReleaseAll'));assert.deepEqual(f.sent.map(e=>e.kind),['ButtonDown','ReleaseAll']);
  f.q.enqueue(f.event('Move',9));f.q.acknowledge(1);assert.equal(f.sent.length,2);
  f.q.acknowledge(2);assert.equal(f.sent.length,3);
});
test('scene freeze drops pending input and stale actions are never resumed',()=>{
  const f=fixture();f.q.enqueue(f.event());f.q.enqueue(f.event('ButtonDown'));f.q.clear();f.invalidate();f.q.acknowledge(1);
  assert.equal(f.sent.length,1);assert.equal(f.q.pending.length,0);
});
test('expired edges fail closed, overflow bounded, releases are still admissible',()=>{
  const f=fixture();f.q.enqueue(f.event());f.q.enqueue(f.event('ButtonDown'));f.setTime(250);f.q.acknowledge(1);
  assert.equal(f.errors.length,1);assert.equal(f.sent.length,1);
  const g=fixture();g.q.enqueue(g.event());for(let i=0;i<65;i++)g.q.enqueue(g.event('KeyDown'));
  assert.equal(g.errors.length,1);assert.ok(g.q.pending.length<=64);
  g.q.enqueue(g.event('ReleaseAll'));assert.equal(g.sent.at(-1).kind,'ReleaseAll');
});
test('synthetic 1kHz motion and 50ms replies bound queue; not NX latency evidence',()=>{
  const f=fixture();for(let t=0;t<1000;t++){f.setTime(t);f.q.enqueue(f.event('Move',t));if(t%50===49)f.q.acknowledge(f.sent.length);}
  assert.ok(f.sent.length<=21);assert.equal(f.sent.at(-1).payload.u,.999);assert.ok(f.q.maxDepth<=1);
  assert.ok(f.q.summary().browserQueueWait.p95Ms<=1);
});
