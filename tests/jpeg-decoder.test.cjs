const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict'),test=require('node:test');
vm.runInThisContext(fs.readFileSync('tools/Workbench.MediaProbe/video-profile.js','utf8'));
vm.runInThisContext(fs.readFileSync('tools/Workbench.MediaProbe/jpeg-decoder.js','utf8'));
const chunk=()=>({timestamp:123,data:new Uint8Array([255,216,255,217])});
test('JPEG closes decoded image after synchronous presentation without VideoDecoder',async()=>{
  let closed=0,shown=0;global.createImageBitmap=async()=>({width:1280,height:720,close(){closed++;}});
  const decoder=new ProbeJpegDecoder({output:f=>{shown++;assert.equal(f.timestamp,123);assert.ok(f.image);f.close();},error:e=>assert.fail(e)});
  decoder.decode(chunk());await decoder.flush();assert.equal(shown,1);assert.equal(closed,1);assert.equal(decoder.decodeQueueSize,0);
});
test('close during decode retires and disposes late bitmap',async()=>{
  let resolve,closed=0,shown=0;global.createImageBitmap=()=>new Promise(r=>resolve=r);
  const decoder=new ProbeJpegDecoder({output:()=>shown++,error:e=>assert.fail(e)});
  decoder.decode(chunk());await Promise.resolve();decoder.close();resolve({width:1280,height:720,close(){closed++;}});
  await decoder.flush();assert.equal(shown,0);assert.equal(closed,1);
});
test('wrong decoded size fails closed and releases bitmap',async()=>{
  let closed=0,errors=0;global.createImageBitmap=async()=>({width:1920,height:1080,close(){closed++;}});
  const decoder=new ProbeJpegDecoder({output:()=>assert.fail(),error:()=>errors++});decoder.decode(chunk());await decoder.flush();
  assert.equal(errors,1);assert.equal(closed,1);assert.equal(decoder.state,'closed');
});
test('JPEG backlog and payload are bounded before decoding',async()=>{
  global.createImageBitmap=async()=>({width:1280,height:720,close(){}});
  const decoder=new ProbeJpegDecoder({output:f=>f.close(),error:e=>assert.fail(e)});
  assert.throws(()=>decoder.decode({...chunk(),data:new Uint8Array(0)}));assert.throws(()=>decoder.decode({...chunk(),timestamp:-1}));
  decoder.decode(chunk());decoder.decode(chunk());assert.throws(()=>decoder.decode(chunk()));await decoder.flush();
});
test('decode failure and output failure cannot leak or present further frames',async()=>{
  let errors=0,closed=0;global.createImageBitmap=async()=>({width:1280,height:720,close(){closed++;}});
  const decoder=new ProbeJpegDecoder({output(){throw Error('draw failure');},error:()=>errors++});
  decoder.decode(chunk());decoder.decode(chunk());await decoder.flush();assert.equal(errors,1);assert.equal(closed,1);assert.equal(decoder.decodeQueueSize,0);
});
test('explicit 1080p JPEG is accepted without accepting a mixed or arbitrary size',async()=>{
  let shown=0,closed=0;global.createImageBitmap=async()=>({width:1920,height:1080,close(){closed++;}});
  const decoder=new ProbeJpegDecoder({width:1920,height:1080,output(f){shown++;f.close();},error:e=>assert.fail(e)});
  decoder.decode(chunk());await decoder.flush();assert.equal(shown,1);assert.equal(closed,1);
  assert.throws(()=>new ProbeJpegDecoder({width:1920,height:720}));assert.throws(()=>probeVideoSize(3840,2160));
  assert.throws(()=>probeVideoSize('1920',1080));assert.ok(Object.isFrozen(probeVideoSize(1920,1080)));
});
test('JPEG ownership survives a delayed presentation and disposal is idempotent',async()=>{
  let held,closed=0;global.createImageBitmap=async()=>({width:1280,height:720,close(){closed++;}});
  const decoder=new ProbeJpegDecoder({output:f=>held=f,error:e=>assert.fail(e)});
  decoder.decode(chunk());await decoder.flush();assert.equal(closed,0);decoder.close();assert.equal(closed,0);
  held.close();held.close();assert.equal(closed,1);
});
