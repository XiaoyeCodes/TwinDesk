'use strict';
// Original implementation informed by Sunshine/noVNC scheduling patterns; no upstream code copied.
// One outstanding action; merge only the adjacent unsent Move in an unchanged gesture/scene.
class InputMoveQueue {
  constructor(send,valid,now,fail){this.send=send;this.valid=valid;this.now=now;this.fail=fail;this.pending=[];this.inflight=null;this.merged=0;this.dropped=0;this.maxDepth=0;this.waits=[];}
  enqueue(entry){
    if(entry.kind==='ReleaseAll'){
      this.clear();this.inflight=this.send(entry);return;
    }
    const last=this.pending.at(-1);
    if(entry.kind==='Move'&&last?.kind==='Move'&&last.boundary===entry.boundary){
      this.pending[this.pending.length-1]=entry;this.merged++;
    } else {
      if(this.pending.length>=64){this.clear();this.fail('本机输入队列已满；停止且不重放');return;}
      this.pending.push(entry);this.maxDepth=Math.max(this.maxDepth,this.pending.length);
    }
    this.pump();
  }
  pump(){
    if(this.inflight!==null)return;
    while(this.pending.length){
      const entry=this.pending.shift(),age=this.now()-entry.at;
      if(!entry.release&&(!this.valid(entry)||age>=250)){
        this.dropped++;
        if(age>=250&&entry.kind!=='Move'){this.clear();this.fail('本机输入等待过久；手势已停止且不重放');return;}
        continue;
      }
      this.waits.push(age);if(this.waits.length>256)this.waits.shift();
      this.inflight=this.send(entry);return;
    }
  }
  acknowledge(sequence){if(this.inflight!==sequence)return;this.inflight=null;this.pump();}
  clear(){this.dropped+=this.pending.length;this.pending=[];}
  summary(){
    const a=[...this.waits].sort((x,y)=>x-y),p=q=>a.length?a[Math.ceil(a.length*q)-1]:null;
    return {merged:this.merged,dropped:this.dropped,maxDepth:this.maxDepth,pending:this.pending.length,
      browserQueueWait:{samples:a.length,p50Ms:p(.5),p95Ms:p(.95),maxMs:p(1)}};
  }
}
// M1 probe input adapter. No hidden retries, no clipboard, no remote HWND/absolute desktop coordinates.
class ProbeInputClient {
  static async open(canvas, fail) {
    const client = new ProbeInputClient(canvas, fail);
    try { await client.hello; } catch(error) { client.close(); throw error; }
    return client;
  }
  constructor(canvas, fail) {
    this.canvas=canvas; this.fail=fail; this.sequence=0; this.frame=0; this.scene=null; this.ready=false;
    this.keys=new Set(); this.buttons=new Set(); this.pointerButtons=new Map(); this.results=[]; this.submitted=0; this.accepted=0; this.rejected=0;
    this.listeners=[]; this.closed=false;
    this.pendingTimes=new Map();this.roundTrips=[];
    this.local=typeof ProbeLocalConsole==='function'?new ProbeLocalConsole(this):null;
    this.calibration=typeof F0PointerCalibration==='function'?new F0PointerCalibration(canvas):null;
    const route=location.pathname??"/"; this.socket=new WebSocket(`ws://${location.host}${route.endsWith("/")?route:route+"/"}control`);
    this.hello=new Promise((resolve,reject)=>{
      const deadline=setTimeout(()=>reject(Error('Input handshake timeout')),3000);
      this.socket.onmessage=event=>{
        try {
          const message=JSON.parse(event.data);
          if(message.type==='inputHello') {
            if(!Number.isInteger(message.streamId)||message.streamId<1||message.streamId>2||message.epoch!==1)throw Error('Invalid input stream identity');
            this.identity=message; clearTimeout(deadline);resolve();
            if(message.localConsole)this.moveQueue=new InputMoveQueue(e=>this.sendCommand(e.kind,e.payload,e.release),
              e=>this.ready&&!this.closed&&e.scene===this.scene?.version,()=>this.now(),error=>this.fail(error));
            this.timer=setInterval(()=>this.send({type:'heartbeat'}),2000);
          } else if(message.type==='localConsoleState') {
            this.local?.state(message);
          } else if(message.type==='localDevices') {
            if(!this.local)throw Error('Local input not configured');this.local.receive(message.events);
          } else if(message.type==='displayAck') {
            const expected=this.stamp();
            if(message.accepted && this.scene && ['host','stream','epoch','scene'].every(key=>message.stamp[key]===expected[key]) && message.frame===this.frame)this.ready=true;
            if(this.ready&&this.identity?.localConsole)this.local?.start();
            this.moveQueue?.pump();
          } else if(message.type==='inputState') {
            document.querySelector('#inputStatus').textContent=`输入 ${message.status.session.reason}；本机 ${message.nativeCode}`;
            if(!message.status.session.active || !message.status.session.ready)this.ready=false;
            if(!message.status.session.active)this.terminalReason=`${message.status.session.reason} / ${message.nativeCode}`;
            if(message.status.session.reason==='SCENE_UPDATING')this.release();
          } else if(message.type==='inputTerminated') {
            this.ready=false;this.terminalReason=`${message.reason} / ${message.nativeCode}`;
            this.fail(`输入已停止：${this.terminalReason}；未重放操作`);
          } else if(message.type==='inputResult') {
            const sentAt=this.pendingTimes.get(message.sequence);this.pendingTimes.delete(message.sequence);
            if(sentAt!==undefined){
              this.roundTrips.push({roundTripMs:this.now()-sentAt,dispatchMs:message.dispatchMs??null});
              if(this.roundTrips.length>64)this.roundTrips.shift();
              const sorted=this.roundTrips.map(x=>x.roundTripMs).sort((a,b)=>a-b),label=document.querySelector('#latencyStatus');
              if(label)label.textContent=`输入往返 P50 ${Math.round(sorted[Math.ceil(sorted.length*.5)-1])} / P95 ${Math.round(sorted[Math.ceil(sorted.length*.95)-1])} ms · 本次派发 ${Math.round(message.dispatchMs??0)} ms · 合并移动 ${this.moveQueue?.merged??0}（不含画面回显）`;
            }
            if(this.results.length===64)this.results.shift();
            this.results.push(message);message.outcome.accepted?this.accepted++:this.rejected++;
            document.querySelector('#inputStatus').textContent=`序号 ${message.sequence}：${message.outcome.code}（不是应用响应证明）`;
            if(!message.outcome.accepted && message.outcome.code!=='POINTER_OUTSIDE_TARGET')this.ready=false;
            if(message.outcome.code==='SCENE_UPDATING')this.release();
            if(!message.outcome.accepted&&message.outcome.code!=='POINTER_OUTSIDE_TARGET')this.moveQueue?.clear();
            this.moveQueue?.acknowledge(message.sequence);
            this.calibration?.acknowledge(message.sequence,message.outcome.accepted);
            if(message.sequence===this.disconnectSequence){
              this.disconnectSequence=null;
              if(message.outcome.accepted){
                // Explicit own-fixture fault: no client key-up or stop; the server must release on WS close.
                this.disconnectDiagnostic='accepted-down-then-control-close-without-client-keyup';
                this.closed=true;this.ready=false;clearInterval(this.timer);this.listeners.forEach(remove=>remove());
                this.socket.close();document.querySelector('#inputStatus').textContent='F0 诊断控制已断开；核对真实 key-up 与持键状态，不以应答判定通过';
              }
            }
          } else throw Error('Unknown input response');
        } catch(error){reject(error);this.fail(error);}
      };
      this.socket.onerror=()=>{clearTimeout(deadline);reject(Error('Input WebSocket error'));if(!this.closed)this.fail('Input WebSocket error');};
      this.socket.onclose=event=>{clearTimeout(deadline);reject(Error('Input connection closed'));if(!this.closed)this.fail(this.terminalReason?`输入已停止：${this.terminalReason}；未重放操作`:`输入连接已关闭（${event?.code??'未知'} ${event?.reason??''}）；未重放操作`);};
    });
    this.listen(canvas,'pointerdown',event=>{
      if(this.identity?.localConsole||this.local?.pending||this.local?.active||!this.ready || ![0,1,2].includes(event.button))return;
      const point=this.point(event);if(!point)return;
      event.preventDefault();canvas.focus();canvas.setPointerCapture(event.pointerId);
      const button=event.button===0&&document.querySelector('#pointerMode').value==='middle'?'Middle':['Left','Middle','Right'][event.button];
      if(this.buttons.has(button))return; // Two physical buttons must not own the same mapped native down.
      this.pointerButtons.set(event.button,button);this.buttons.add(button);
      if(button==='Left'&&this.identity?.scope?.startsWith('F0 ')&&document.querySelector('#calibratePointer')?.checked)
        this.calibration?.begin(this.scene,this.frame,this.sequence+1,point,{x:event.clientX,y:event.clientY,rect:this.canvas.getBoundingClientRect().toJSON()});
      this.command('ButtonDown',{button,...point});
    });
    this.listen(canvas,'pointermove',event=>{
      if(this.identity?.localConsole||this.local?.pending||this.local?.active||!this.ready)return;const point=this.point(event);if(point)this.command('Move',point);
    });
    this.listen(canvas,'pointerup',event=>{
      if(this.identity?.localConsole||this.local?.pending||this.local?.active)return;
      const button=this.pointerButtons.get(event.button);this.pointerButtons.delete(event.button);
      if(!button||!this.buttons.delete(button))return;
      event.preventDefault();this.command('ButtonUp',{button},true);
      if(this.buttons.size===0&&canvas.hasPointerCapture(event.pointerId))canvas.releasePointerCapture(event.pointerId);
    });
    this.listen(canvas,'pointercancel',()=>this.release());
    this.listen(canvas,'lostpointercapture',()=>{if(this.buttons.size)this.release();});
    this.listen(canvas,'contextmenu',event=>event.preventDefault());
    this.listen(canvas,'wheel',event=>{
      event.preventDefault();if(this.identity?.localConsole||this.local?.pending||this.local?.active||!this.ready)return;const point=this.point(event);if(!point)return;
      this.command('Wheel',{...point,wheelY:event.deltaY===0?0:-Math.sign(event.deltaY)*120,wheelX:event.deltaX===0?0:Math.sign(event.deltaX)*120});
    },{passive:false});
    this.listen(canvas,'keydown',event=>{
      if(this.identity?.localConsole||this.local?.pending||this.local?.active||event.isComposing||event.key==='Process'||!this.ready)return;
      if(event.code.startsWith('Meta')||['PrintScreen','Pause'].includes(event.code))return;
      event.preventDefault();const repeat=this.keys.has(event.code);this.keys.add(event.code);
      this.command('KeyDown',{key:event.code,repeat});
    });
    this.listen(canvas,'keyup',event=>{
      if(this.identity?.localConsole||this.local?.pending||this.local?.active)return;
      if(!this.keys.delete(event.code))return;event.preventDefault();this.command('KeyUp',{key:event.code},true);
    });
    this.listen(canvas,'blur',()=>this.release());
    this.listen(window,'blur',()=>this.release());
    this.listen(document,'visibilitychange',()=>{if(document.hidden)this.release();});
    if(this.local) {
      this.listen(document.querySelector('#startLocal'),'click',()=>this.local.start());
      this.listen(document.querySelector('#stopLocal'),'click',()=>{this.release();this.send({type:'localStop'});});
      this.listen(document,'visibilitychange',()=>{if(document.hidden&&(this.local.active||this.local.pending))this.send({type:'localStop'});});
    }
    this.listen(document.querySelector('#pointerMode'),'change',()=>this.release());
    this.listen(document.querySelector('#disconnectDiagnostic'),'click',()=>{
      if(!this.ready||this.closed||!this.identity?.scope?.startsWith('F0 ')||this.keys.size||this.buttons.size)return;
      this.disconnectSequence=this.sequence+1;this.keys.add('ShiftLeft');this.command('KeyDown',{key:'ShiftLeft'});this.ready=false;
    });
    this.listen(document.querySelector('#sendText'),'click',()=>{
      if(this.ready)this.command('Text',{text:document.querySelector('#inputText').value});
    });
  }
  listen(target,type,handler,options){target.addEventListener(type,handler,options);this.listeners.push(()=>target.removeEventListener(type,handler,options));}
  now(){return globalThis.performance?.now?.()??Date.now();}
  send(message){if(this.socket.readyState!==WebSocket.OPEN)return; if(this.socket.bufferedAmount>65536){this.fail('Input send backlog; stopped without replay');return;}this.socket.send(JSON.stringify(message));}
  stamp(){return {host:this.identity.hostInstanceId,stream:this.identity.streamId,epoch:this.identity.epoch,scene:this.scene?.version??0};}
  command(kind,payload={},release=false){
    if((!this.ready&&!release)||!this.identity||this.closed)return;
    if(this.moveQueue){
      this.moveQueue.enqueue({kind,payload:{...payload},release,scene:this.scene?.version,at:this.now(),
        boundary:JSON.stringify([this.stamp(),[...this.keys].sort(),[...this.buttons].sort()])});return;
    }
    this.sendCommand(kind,payload,release);
  }
  sendCommand(kind,payload={},release=false){
    if((!this.ready&&!release)||!this.identity||this.closed)return null;
    this.submitted++;const sequence=++this.sequence;
    if(this.pendingTimes.size>=128)this.pendingTimes.delete(this.pendingTimes.keys().next().value);
    this.pendingTimes.set(sequence,this.now());
    this.send({type:'input',command:{lease:this.identity.lease,sequence,stamp:this.stamp(),displayedFrame:this.frame,kind,...payload}});
    return sequence;
  }
  point(event){
    if(!this.scene)return null;
    const rect=this.canvas.getBoundingClientRect(), content=this.scene.contentRect;
    const x=(event.clientX-rect.left)*this.canvas.width/rect.width-content.x;
    const y=(event.clientY-rect.top)*this.canvas.height/rect.height-content.y;
    if(x<0||y<0||x>=content.width||y>=content.height)return null;
    return {u:x/content.width,v:y/content.height};
  }
  freeze(){this.moveQueue?.clear();this.ready=false;this.scene=null;this.frame=0;this.keys.clear();this.buttons.clear();this.pointerButtons.clear();}
  displayed(scene,sequence){
    this.calibration?.observe(scene,sequence);
    if(this.scene?.version!==scene.version)this.freeze();
    this.scene=scene;this.frame=sequence;
    this.local?.paint();
    this.send({type:'displayed',stamp:this.stamp(),frame:sequence});
  }
  release(){this.moveQueue?.clear();if(this.keys.size||this.buttons.size)this.command('ReleaseAll',{},true);this.keys.clear();this.buttons.clear();this.pointerButtons.clear();}
  summary(){return {scope:'Submission replies only; verify visible application effects separately, not P04 latency',inputScheduling:this.moveQueue?.summary()??null,disconnectDiagnostic:this.disconnectDiagnostic??null,inputRoundTrips:this.roundTrips,localConsole:this.local?{physicalEventsReceived:this.local.events,active:this.local.active}:null,pointerCalibration:this.calibration?.summary()??null,submitted:this.submitted,accepted:this.accepted,rejected:this.rejected,recent:this.results};}
  close(){if(this.closed)return;this.release();this.local?.close();this.send({type:'stop'});this.closed=true;clearInterval(this.timer);this.listeners.forEach(remove=>remove());this.socket.close();}
}
