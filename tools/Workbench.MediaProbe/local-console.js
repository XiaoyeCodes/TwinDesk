'use strict';
// Local physical input bridge. Crosshair is the browser's requested position, not native response evidence.
class ProbeLocalConsole {
  constructor(input) {
    this.input=input;this.pending=false;this.active=false;this.requested=false;this.u=0.5;this.v=0.5;this.events=0;
    this.status=document.querySelector('#localStatus');this.cursor=document.querySelector('#localCursor');
  }
  start() {
    if(!this.input.identity?.localConsole||!this.input.ready||this.requested)return;
    this.input.release();this.pending=true;this.requested=true;this.status.textContent='正在连接并接管 NX，请松开鼠标按钮…';
    this.input.send({type:'localStart'});
  }
  state(message) {
    this.lastState=message.state;this.lastReason=message.reason;
    this.pending=message.state==='ARMING';this.active=message.state==='ACTIVE';
    this.status.textContent=message.reason;this.paint();
  }
  receive(events) {
    if(!this.active||!Array.isArray(events)||events.length>128)throw Error('Invalid local device batch');
    for(const e of events) {
      this.events++;
      // A physical delta is bound to the capture generation at hook reception, never relabeled.
      if(e.scene&&e.scene!==this.input.scene?.version&&!(e.up&&(e.kind==='Button'||e.kind==='Key')))continue;
      if(e.kind==='Move') {
        if(!Number.isFinite(e.dx)||!Number.isFinite(e.dy)||Math.abs(e.dx)>100000||Math.abs(e.dy)>100000)throw Error('Invalid local motion');
        const rect=this.input.canvas.getBoundingClientRect(),scene=this.input.scene;
        if(!scene)continue;
        const width=rect.width*scene.contentRect.width/this.input.canvas.width;
        const height=rect.height*scene.contentRect.height/this.input.canvas.height;
        if(width<=0||height<=0)throw Error('Local canvas unavailable');
        this.u=Math.min(0.999999,Math.max(0,this.u+e.dx/width));
        this.v=Math.min(0.999999,Math.max(0,this.v+e.dy/height));
        this.input.command('Move',{u:this.u,v:this.v});
      } else if(e.kind==='Button') {
        if(!['Left','Middle','Right'].includes(e.button))throw Error('Invalid local button');
        if(e.up) {if(this.input.buttons.delete(e.button))this.input.command('ButtonUp',{button:e.button},true);}
        else if(this.input.ready&&!this.input.buttons.has(e.button)) {
          this.input.buttons.add(e.button);this.input.command('ButtonDown',{button:e.button,u:this.u,v:this.v});
        }
      } else if(e.kind==='Wheel') {
        if(!Number.isInteger(e.wheelX)||!Number.isInteger(e.wheelY))throw Error('Invalid local wheel');
        this.input.command('Wheel',{u:this.u,v:this.v,wheelX:e.wheelX,wheelY:e.wheelY});
      } else if(e.kind==='Key') {
        if(typeof e.code!=='string'||e.code.length>32||e.code==='F12')throw Error('Invalid local key');
        if(e.up) {if(this.input.keys.delete(e.code))this.input.command('KeyUp',{key:e.code},true);}
        else if(this.input.ready) {
          const repeat=this.input.keys.has(e.code);this.input.keys.add(e.code);
          this.input.command('KeyDown',{key:e.code,repeat});
        }
      } else throw Error('Unknown local event');
    }
    this.paint();
  }
  paint() {
    const s=this.input.scene;this.cursor.hidden=!this.active||!s;
    if(!s)return;
    this.cursor.style.left=(s.contentRect.x+this.u*s.contentRect.width)/this.input.canvas.width*100+'%';
    this.cursor.style.top=(s.contentRect.y+this.v*s.contentRect.height)/this.input.canvas.height*100+'%';
  }
  close() {this.pending=false;this.active=false;this.cursor.hidden=true;this.status.textContent=this.lastState==='FAILED'?this.lastReason:'本机接管已结束；实体键鼠恢复正常';}
}
