const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');

// L0 browser event/gating tests, deliberately no real DOM, WS or native application.
function fixture(){
  class Element {
    constructor(){this.handlers=new Map();this.value='';this.width=1280;this.height=720;}
    addEventListener(type,fn){this.handlers.set(type,fn);}
    removeEventListener(type){this.handlers.delete(type);}
    emit(type,event={}){this.handlers.get(type)?.({preventDefault(){},...event});}
    focus(){} setPointerCapture(){} hasPointerCapture(){return false;}
    getBoundingClientRect(){return {left:0,top:0,width:1280,height:720};}
  }
  class Socket {
    static OPEN=1;
    constructor(){this.readyState=1;this.bufferedAmount=0;this.messages=[];Socket.last=this;}
    send(data){this.messages.push(JSON.parse(data));}
    close(){this.readyState=3;this.onclose?.();}
    receive(data){this.onmessage({data:JSON.stringify(data)});}
  }
  const elements=new Map(['#inputStatus','#sendText','#inputText','#pointerMode','#disconnectDiagnostic'].map(id=>[id,new Element()]));
  const document=new Element();document.querySelector=id=>elements.get(id);
  const canvas=new Element(),window=new Element(),errors=[];
  const context=vm.createContext({WebSocket:Socket,location:{host:'127.0.0.1:8091'},document,window,
    setTimeout:()=>1,clearTimeout(){},setInterval:()=>2,clearInterval(){}});
  vm.runInContext(fs.readFileSync('tools/Workbench.MediaProbe/input-client.js','utf8')+'\nthis.Client=ProbeInputClient;',context);
  const client=new context.Client(canvas,error=>errors.push(String(error)));
  Socket.last.receive({type:'inputHello',lease:{id:'test',generation:1},hostInstanceId:'host',streamId:1,epoch:1});
  const scene={version:1,width:1280,height:720,contentRect:{x:128,y:0,width:1024,height:720}};
  function ready(){client.displayed(scene,1);Socket.last.receive({type:'displayAck',accepted:true,stamp:client.stamp(),frame:1});}
  return {client,canvas,elements,document,window,socket:Socket.last,scene,errors,ready};
}
test('no input before exact presented-frame acknowledgment',()=>{
  const f=fixture();f.client.command('KeyDown',{key:'KeyA'});assert.equal(f.client.submitted,0);
  f.client.displayed(f.scene,1);f.socket.receive({type:'displayAck',accepted:true,stamp:{scene:1},frame:2});
  assert.equal(f.client.ready,false);f.ready();f.client.command('KeyDown',{key:'KeyA'});
  assert.equal(f.socket.messages.at(-1).command.kind,'KeyDown');f.client.close();
});
test('local console profile never forwards ordinary browser DOM mouse or keys before physical takeover',()=>{
  const f=fixture();f.client.identity.localConsole=true;f.ready();
  f.canvas.emit('pointermove',{clientX:640,clientY:360});
  f.canvas.emit('pointerdown',{button:0,pointerId:1,clientX:640,clientY:360});
  f.canvas.emit('wheel',{clientX:640,clientY:360,deltaY:120,deltaX:0});
  f.canvas.emit('keydown',{code:'KeyA'});
  assert.equal(f.client.submitted,0);assert.equal(f.client.keys.size+f.client.buttons.size,0);f.client.close();
});
test('an outside-target hover rejection permits a fresh move but never replays the rejected event',()=>{
  const f=fixture();f.ready();f.client.command('Move',{u:.5,v:.5});
  f.socket.receive({type:'inputResult',sequence:1,outcome:{accepted:false,code:'POINTER_OUTSIDE_TARGET'}});
  assert.equal(f.client.ready,true);assert.equal(f.client.submitted,1);
  f.client.command('Move',{u:.6,v:.6});assert.equal(f.client.submitted,2);
  f.socket.receive({type:'inputResult',sequence:2,outcome:{accepted:false,code:'FOCUS_OR_DESKTOP_DENIED'}});
  assert.equal(f.client.ready,false);f.client.close();
});
test('input commands preserve the locally negotiated second stream identity',()=>{
  const f=fixture();f.client.identity.streamId=2;f.ready();f.client.command('KeyDown',{key:'KeyA'});
  assert.equal(f.socket.messages.at(-1).command.stamp.stream,2);
  assert.equal(f.socket.messages.at(-1).command.stamp.epoch,1);f.client.close();
});
test('scene update pauses and releases the gesture without closing or replaying',()=>{
  const f=fixture();f.ready();f.canvas.emit('keydown',{code:'ControlLeft'});
  f.socket.receive({type:'inputResult',sequence:1,outcome:{accepted:false,code:'SCENE_UPDATING'}});
  assert.equal(f.client.ready,false);assert.equal(f.client.keys.size,0);assert.equal(f.socket.readyState,1);
  assert.deepEqual(f.socket.messages.filter(m=>m.type==='input').map(m=>m.command.kind),['KeyDown','ReleaseAll']);
  const next={...f.scene,version:2};f.client.displayed(next,2);
  f.socket.receive({type:'displayAck',accepted:true,stamp:f.client.stamp(),frame:2});
  assert.equal(f.client.ready,true);assert.equal(f.client.submitted,2);assert.equal(f.errors.length,0);f.client.close();
});
test('terminal control message and close preserve the actual server reason',()=>{
  const f=fixture();f.ready();
  f.socket.receive({type:'inputTerminated',reason:'FOCUS_OR_DESKTOP_DENIED',nativeCode:'FOCUS_DENIED'});
  assert.equal(f.client.ready,false);assert.match(f.errors[0],/FOCUS_DENIED/);
  f.socket.onclose({code:1008,reason:'INPUT_STOPPED'});
  assert.match(f.errors.at(-1),/FOCUS_DENIED/);assert.equal(f.client.submitted,0);f.client.close();
});
test('another stream display acknowledgment cannot enable this canvas',()=>{
  const f=fixture();f.client.displayed(f.scene,1);
  f.socket.receive({type:'displayAck',accepted:true,stamp:{...f.client.stamp(),stream:2},frame:1});
  assert.equal(f.client.ready,false);f.ready();assert.equal(f.client.ready,true);f.client.close();
});
test('old ack cannot reenable input after scene config freeze',()=>{
  const f=fixture();f.ready();f.client.freeze();
  f.socket.receive({type:'displayAck',accepted:true,stamp:{scene:1},frame:1});
  assert.equal(f.client.ready,false);assert.equal(f.client.scene,null);f.client.close();
});
test('letterbox excluded and coordinates map only to visible content',()=>{
  const f=fixture();f.ready();assert.equal(f.client.point({clientX:20,clientY:10}),null);
  const point=f.client.point({clientX:640,clientY:360});assert.equal(point.u,0.5);assert.equal(point.v,0.5);f.client.close();
});
test('lost pointer capture sends release once with no target move',()=>{
  const f=fixture();f.ready();f.canvas.emit('pointerdown',{button:1,pointerId:4,clientX:640,clientY:360});
  f.canvas.emit('lostpointercapture');f.canvas.emit('lostpointercapture');
  const commands=f.socket.messages.filter(m=>m.type==='input').map(m=>m.command);
  assert.deepEqual(commands.map(m=>m.kind),['ButtonDown','ReleaseAll']);assert.equal(commands[0].button,'Middle');
  assert.equal(commands[1].u,undefined);f.client.close();
});
test('physical key repeat and blur cleanup do not replay text',()=>{
  const f=fixture();f.ready();f.canvas.emit('keydown',{code:'ControlLeft'});f.canvas.emit('keydown',{code:'ControlLeft'});
  f.window.emit('blur');f.window.emit('blur');
  const commands=f.socket.messages.filter(m=>m.type==='input').map(m=>m.command);
  assert.deepEqual(commands.map(m=>m.kind),['KeyDown','KeyDown','ReleaseAll']);assert.equal(commands[1].repeat,true);f.client.close();
});
test('composition and reserved keys do not send duplicate physical characters',()=>{
  const f=fixture();f.ready();f.canvas.emit('keydown',{code:'KeyA',isComposing:true});f.canvas.emit('keydown',{code:'MetaLeft'});
  assert.equal(f.client.submitted,0);f.elements.get('#inputText').value='测试';f.elements.get('#sendText').emit('click');
  assert.equal(f.client.submitted,1);assert.equal(f.socket.messages.at(-1).command.kind,'Text');f.client.close();
});
test('close releases held input and removes all listeners without reconnecting',()=>{
  const f=fixture();f.ready();f.canvas.emit('keydown',{code:'ShiftLeft'});f.client.close();f.client.close();
  assert.deepEqual(f.socket.messages.slice(-2).map(m=>m.type),['input','stop']);assert.equal(f.canvas.handlers.size,0);assert.equal(f.errors.length,0);
});
test('send backlog fails instead of queuing unbounded input',()=>{
  const f=fixture();f.ready();f.socket.bufferedAmount=65537;const before=f.socket.messages.length;
  f.client.command('Move',{u:0.5,v:0.5});assert.equal(f.socket.messages.length,before);assert.equal(f.errors.length,1);
  f.socket.bufferedAmount=0;f.client.close();
});
test('mapped middle drag releases the native down even when selector changes',()=>{
  const f=fixture();f.ready();const mode=f.elements.get('#pointerMode');mode.value='middle';
  f.canvas.emit('pointerdown',{button:0,pointerId:4,clientX:640,clientY:360});
  f.canvas.emit('pointermove',{clientX:660,clientY:380});
  mode.value='native';mode.emit('change');
  f.canvas.emit('pointerup',{button:0,pointerId:4});
  const commands=f.socket.messages.filter(m=>m.type==='input').map(m=>m.command);
  assert.deepEqual(commands.map(m=>m.kind),['ButtonDown','Move','ReleaseAll']);
  assert.equal(commands[0].button,'Middle');assert.equal(f.client.buttons.size,0);assert.equal(f.client.pointerButtons.size,0);f.client.close();
});
test('mapped and physical middle do not duplicate native button ownership',()=>{
  const f=fixture();f.ready();f.elements.get('#pointerMode').value='middle';
  f.canvas.emit('pointerdown',{button:0,pointerId:1,clientX:640,clientY:360});
  f.canvas.emit('pointerdown',{button:1,pointerId:1,clientX:640,clientY:360});
  f.canvas.emit('pointerup',{button:1,pointerId:1});
  assert.equal(f.client.buttons.size,1);
  f.canvas.emit('pointerup',{button:0,pointerId:1});
  const commands=f.socket.messages.filter(m=>m.type==='input').map(m=>m.command);
  assert.deepEqual(commands.map(m=>m.kind),['ButtonDown','ButtonUp']);assert.equal(commands[1].button,'Middle');f.client.close();
});
test('explicit disconnect fault is F0-only and omits client release after acknowledged down',()=>{
  const f=fixture();f.ready();const button=f.elements.get('#disconnectDiagnostic');button.emit('click');assert.equal(f.client.submitted,0);
  f.client.identity.scope='F0 loopback input only';button.emit('click');
  assert.equal(f.socket.messages.at(-1).command.key,'ShiftLeft');assert.equal(f.client.ready,false);
  f.socket.receive({type:'inputResult',sequence:1,outcome:{accepted:true,code:'SUBMITTED_NOT_APPLICATION_ACK'}});
  assert.equal(f.socket.readyState,3);assert.equal(f.client.closed,true);
  assert.equal(f.socket.messages.filter(m=>m.type==='input').length,1);assert.equal(f.errors.length,0);
});
