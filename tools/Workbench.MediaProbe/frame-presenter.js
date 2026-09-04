'use strict';
// A run owns every decoded frame until presentation or retirement, including fault-delayed callbacks.
globalThis.ProbeFramePresenter = class {
  constructor({timeline,present,error,delayFirstMs=0,schedule=(fn,ms)=>globalThis.setTimeout(fn,ms),unschedule=id=>globalThis.clearTimeout(id),now=()=>performance.now()}) {
    if(![0,3000].includes(delayFirstMs))throw Error('Unsupported bounded callback delay');
    Object.assign(this,{timeline,present,error,delayFirstMs,schedule,unschedule,now});
    this.closed=false;this.held=null;this.first=true;this.waiters=[];
    this.stats={received:0,presented:0,staleScenes:0,staleSequences:0,delayed:0,closedHeld:0,lateCallbacks:0};
  }
  accept(frame) {
    if(this.closed){this.stats.lateCallbacks++;frame.close();return;}
    this.releaseDue();
    if(this.closed){this.stats.lateCallbacks++;frame.close();return;}
    this.stats.received++;
    if(this.first && this.delayFirstMs){
      this.first=false;this.stats.delayed++;
      // At most one retained VideoFrame/ImageBitmap. Ordinary decoding keeps running.
      const held={frame,timer:null,due:this.now()+this.delayFirstMs};this.held=held;
      held.timer=this.schedule(()=>this.releaseHeld(held),this.delayFirstMs);return;
    }
    this.first=false;this.deliver(frame);
  }
  deliver(frame) {
    try {
      if(this.closed)return;
      const metadata=this.timeline.complete(frame.timestamp);
      if(!metadata.present){
        if(metadata.scene.version!==this.timeline.version)this.stats.staleScenes++;
        else this.stats.staleSequences++;
        return;
      }
      this.present(frame,metadata);this.stats.presented++;
    }catch(e){this.error(e);}finally{frame.close();}
  }
  releaseHeld(held) {
    if(this.held!==held)return;
    this.held=null;this.unschedule(held.timer);this.deliver(held.frame);this.finishWaiters();
  }
  // Background tabs may throttle timers: incoming decoder outputs and end-of-stream also reap the frame.
  releaseDue() { if(this.held && this.now()>=this.held.due)this.releaseHeld(this.held); }
  settled() { this.releaseDue();return this.held?new Promise(resolve=>this.waiters.push(resolve)):Promise.resolve(); }
  finishWaiters() { for(const resolve of this.waiters.splice(0))resolve(); }
  close() {
    if(this.closed)return;
    this.closed=true;
    if(this.held){this.unschedule(this.held.timer);this.held.frame.close();this.held=null;this.stats.closedHeld++;}
    this.timeline.close();this.finishWaiters();
  }
};
