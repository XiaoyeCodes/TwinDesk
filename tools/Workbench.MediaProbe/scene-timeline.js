'use strict';
// Metadata belongs to submitted chunks, never to the mutable scene at decoder callback time.
globalThis.ProbeSceneTimeline = class {
  constructor(width, height) { this.width=width;this.height=height;this.version=0;this.pending=new Map();this.lastTimestamp=-1;this.lastSequence=0;this.presentedSequence=0;this.closed=false; }
  announce(scene) {
    const r=scene?.contentRect;
    if(this.closed || !scene || !Number.isInteger(scene.version) || scene.version<=this.version || scene.version>0xffffffff
      || scene.width!==this.width || scene.height!==this.height || !Number.isInteger(scene.nodeCount) || scene.nodeCount<1 || scene.nodeCount>8
      || !r || ![r.x,r.y,r.width,r.height].every(Number.isInteger) || r.x<0 || r.y<0 || r.width<=0 || r.height<=0
      || r.x+r.width>this.width || r.y+r.height>this.height) throw Error('Invalid scene configuration');
    this.version=scene.version;this.scene=Object.freeze({...scene,contentRect:Object.freeze({...r})});
  }
  submit(timestamp, version, sequence) {
    if(this.closed || !Number.isSafeInteger(timestamp) || timestamp<=this.lastTimestamp || version!==this.version || !this.scene
      || !Number.isInteger(sequence) || sequence!==this.lastSequence+1 || sequence>0xffffffff || this.pending.size>=16) throw Error('Invalid or over-budget frame association');
    this.pending.set(timestamp,Object.freeze({scene:this.scene,sequence}));this.lastTimestamp=timestamp;this.lastSequence=sequence;
  }
  complete(timestamp) {
    const metadata=this.pending.get(timestamp);
    if(this.closed || !metadata)throw Error('Decoded frame has no submitted metadata');
    this.pending.delete(timestamp);
    const present=metadata.scene.version===this.version && metadata.sequence>this.presentedSequence;
    if(present)this.presentedSequence=metadata.sequence;
    return {...metadata,present};
  }
  close() { this.closed=true;this.pending.clear(); }
};
