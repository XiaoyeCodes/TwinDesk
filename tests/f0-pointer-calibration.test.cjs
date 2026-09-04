const test=require('node:test'),assert=require('node:assert/strict'),vm=require('node:vm'),fs=require('node:fs');
const context=vm.createContext({});vm.runInContext(fs.readFileSync('tools/Workbench.MediaProbe/f0-pointer-calibration.js','utf8'),context);
const Calibration=context.F0PointerCalibration;
function image(size=129,center=null){
  const result={width:size,height:size,data:new Uint8ClampedArray(size*size*4)};
  if(center)for(let y=center.y-2;y<center.y+2;y++)for(let x=center.x-2;x<center.x+2;x++)result.data.set([255,165,0,255],4*(y*size+x));
  return result;
}
function fixture(){let pixels=image();const calibration=new Calibration({width:1280,height:720,getContext(){return {getImageData(){return pixels;}};}});
  const scene={version:1,nodeCount:1,sourceWidth:640,sourceHeight:360,contentRect:{x:0,y:0,width:1280,height:720}};
  calibration.begin(scene,10,5,{u:0.5,v:0.5},{});return {calibration,scene,setPixels(value){pixels=value;}};
}
test('marker absent and isolated compression speck are not a match',()=>{
  assert.equal(Calibration.centroid(image()),null);const value=image();value.data.set([255,165,0,255],0);assert.equal(Calibration.centroid(value),null);
});
test('native marker centroid converts independent decoded offset into source-pixel error',()=>{
  const f=fixture();f.setPixels(image(129,{x:67,y:64}));f.calibration.acknowledge(5,true);f.calibration.observe(f.scene,11);
  const result=f.calibration.results[0];assert.equal(result.status,'WITHIN_2_SOURCE_PIXELS');assert.equal(result.errorX,1.5);assert.equal(result.errorY,0);
});
test('large actual marker displacement is reported outside limit',()=>{
  const f=fixture();f.setPixels(image(129,{x:74,y:64}));f.calibration.observe(f.scene,11);
  assert.equal(f.calibration.results.length,0);f.calibration.acknowledge(5,true);
  assert.equal(f.calibration.results[0].status,'OUTSIDE_2_SOURCE_PIXELS');assert.equal(f.calibration.results[0].sourceError,5);
});
test('rejected input, old frame and changed scene cannot become a measured pass',()=>{
  const f=fixture();f.setPixels(image(129,{x:64,y:64}));f.calibration.observe(f.scene,10);assert.equal(f.calibration.pending.observation,undefined);
  f.calibration.observe(f.scene,11);f.calibration.acknowledge(5,false);assert.equal(f.calibration.results[0].status,'INPUT_REJECTED_NOT_MEASURED');
  const other=fixture();other.calibration.observe({...other.scene,version:2},11);assert.equal(other.calibration.results[0].status,'SCENE_CHANGED_NOT_MEASURED');
});
