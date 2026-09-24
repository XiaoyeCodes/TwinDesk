'use strict';
// Browser-only monotonic intervals. Media timestamps identify frames; they are not wall clocks.
globalThis.ProbeMediaTimings=class {
  constructor(now=()=>performance.now()){
    this.now=now;this.pending=new Map();this.previousPacket=null;
    this.packetIntervals=[];this.receiveToDecode=[];this.drawCalls=[];
    this.received=0;this.decoded=0;this.presented=0;this.maximumPending=0;
  }
  record(list,value){
    if(!Number.isFinite(value)||value<0)throw Error('Invalid local timing interval');
    list.push(value);if(list.length>256)list.shift();
  }
  packet(timestamp,at=this.now()){
    if(!Number.isSafeInteger(timestamp)||timestamp<0||!Number.isFinite(at)||at<0||this.pending.has(timestamp)||this.pending.size>=32)throw Error('Media timing ledger invalid or full');
    if(this.previousPacket!==null)this.record(this.packetIntervals,at-this.previousPacket);
    this.previousPacket=at;this.pending.set(timestamp,at);this.received++;
    this.maximumPending=Math.max(this.maximumPending,this.pending.size);
  }
  decodedFrame(timestamp){
    const at=this.pending.get(timestamp);
    if(at===undefined)throw Error('Decoded frame lacks browser receive timestamp');
    this.pending.delete(timestamp);this.record(this.receiveToDecode,this.now()-at);this.decoded++;
  }
  drawn(start){this.record(this.drawCalls,this.now()-start);this.presented++;}
  distribution(list){
    const values=[...list].sort((a,b)=>a-b),index=q=>values.length?values[Math.ceil(values.length*q)-1]:null;
    return {samples:values.length,p50Ms:index(.5),p95Ms:index(.95),maxMs:index(1)};
  }
  summary(){return {clock:'browser performance.now; not comparable by subtraction with host Stopwatch',
    received:this.received,decoded:this.decoded,presented:this.presented,pending:this.pending.size,maximumPending:this.maximumPending,
    packetIntervalMs:this.distribution(this.packetIntervals),receiveToDecodeMs:this.distribution(this.receiveToDecode),drawCallMs:this.distribution(this.drawCalls)};}
};
