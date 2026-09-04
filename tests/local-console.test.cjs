const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
function fixture(){
  const elements=new Map(['#localStatus','#localCursor'].map(x=>[x,{style:{},hidden:true}]));
  const context=vm.createContext({document:{querySelector:id=>elements.get(id)}});
  vm.runInContext(fs.readFileSync('tools/Workbench.MediaProbe/local-console.js','utf8')+';this.Local=ProbeLocalConsole',context);
  const commands=[],messages=[];
  const input={identity:{localConsole:true},ready:true,keys:new Set(),buttons:new Set(),
    scene:{contentRect:{x:128,y:0,width:1024,height:720}},
    canvas:{width:1280,height:720,getBoundingClientRect:()=>({width:640,height:360})},
    send:m=>messages.push(m),release(){this.keys.clear();this.buttons.clear();},
    command(kind,payload={},release=false){if(this.ready||release)commands.push({kind,...payload});}};
  const local=new context.Local(input);return {input,local,commands,messages,elements};
}
test('local console is explicit and unavailable without locally enabled profile',()=>{
  const f=fixture();f.input.identity.localConsole=false;f.local.start();assert.equal(f.messages.length,0);
  f.input.identity.localConsole=true;f.local.start();f.local.start();assert.equal(f.messages.length,1);
  assert.equal(f.local.pending,true);assert.equal(f.local.active,false);
});
test('physical movement passes through browser scaling and bounded source coordinates',()=>{
  const f=fixture();f.local.state({state:'ACTIVE',reason:'test'});
  f.local.receive([{kind:'Move',dx:128,dy:-90}]);assert.equal(f.commands[0].u,.75);assert.equal(f.commands[0].v,.25);
  f.local.receive([{kind:'Move',dx:10000,dy:-10000}]);assert.equal(f.commands[1].u,.999999);assert.equal(f.commands[1].v,0);
});
test('button drag, physical repeat and releases preserve ordinary input ownership',()=>{
  const f=fixture();f.local.state({state:'ACTIVE'});
  f.local.receive([{kind:'Button',button:'Middle',up:false},{kind:'Move',dx:10,dy:5},
    {kind:'Key',code:'ShiftLeft'},{kind:'Key',code:'ShiftLeft'},{kind:'Key',code:'ShiftLeft',up:true},{kind:'Button',button:'Middle',up:true}]);
  assert.deepEqual(f.commands.map(x=>x.kind),['ButtonDown','Move','KeyDown','KeyDown','KeyUp','ButtonUp']);
  assert.equal(f.commands[3].repeat,true);assert.equal(f.input.keys.size+f.input.buttons.size,0);
});
test('unready scene cannot inject downs; existing held inputs may still release',()=>{
  const f=fixture();f.local.state({state:'ACTIVE'});f.input.buttons.add('Left');f.input.ready=false;
  f.local.receive([{kind:'Button',button:'Right'},{kind:'Key',code:'KeyA'},{kind:'Button',button:'Left',up:true}]);
  assert.deepEqual(f.commands.map(x=>x.kind),['ButtonUp']);assert.equal(f.input.buttons.size,0);
});
test('F12 cannot be forwarded to NX and closed mode accepts no further batch',()=>{
  const f=fixture();f.local.state({state:'ACTIVE'});
  assert.throws(()=>f.local.receive([{kind:'Key',code:'F12'}]));assert.equal(f.commands.length,0);
  f.local.close();assert.throws(()=>f.local.receive([{kind:'Move',dx:1,dy:1}]));assert.equal(f.elements.get('#localCursor').hidden,true);
});
