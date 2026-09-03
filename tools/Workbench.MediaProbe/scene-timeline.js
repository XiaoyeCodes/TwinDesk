'use strict';
// Metadata belongs to submitted chunks, never to the mutable scene at decoder callback time.
globalThis.ProbeSceneTimeline = class {
  constructor(width, height) { this.width=width;this.height=height;this.version=0;this.pending=new Map();this.lastTimestamp=-1; }
  announce(scene) {
    const r=scene?.contentRect;
    if(!scene || !Number.isInteger(scene.version) || scene.version<=this.version || scene.version>0xffffffff
      || scene.width!==this.width || scene.height!==this.height || !Number.isInteger(scene.nodeCount) || scene.nodeCount<1 || scene.nodeCount>8
      || !r || ![r.x,r.y,r.width,r.height].every(Number.isInteger) || r.x<0 || r.y<0 || r.width<=0 || r.height<=0
      || r.x+r.width>this.width || r.y+r.height>this.height) throw Error('Invalid scene configuration');
    this.version=scene.version;this.scene=Object.freeze({...scene,contentRect:Object.freeze({...r})});
  }
  submit(timestamp, version, sequence) {
    if(!Number.isSafeInteger(timestamp) || timestamp<=this.lastTimestamp || version!==this.version || !this.scene
      || !Number.isInteger(sequence) || sequence<1 || this.pending.size>=16) throw Error('Invalid or over-budget frame association');
    this.pending.set(timestamp,Object.freeze({scene:this.scene,sequence}));this.lastTimestamp=timestamp;
  }
  complete(timestamp) {
    const metadata=this.pending.get(timestamp);
    if(!metadata)throw Error('Decoded frame has no submitted metadata');
    this.pending.delete(timestamp);
    return {...metadata,present:metadata.scene.version===this.version};
  }
};
