'use strict';
// No VideoDecoder dependency. One independent JPEG image at a time, finite queue and deterministic disposal.
globalThis.ProbeJpegDecoder=class {
  constructor({output,error,width=1280,height=720}){
    this.size=probeVideoSize(width,height);this.output=output;this.error=error;this.state='configured';this.decodeQueueSize=0;this.chain=Promise.resolve();
  }
  decode(chunk){
    if(this.state!=='configured'||this.decodeQueueSize>=2)throw Error('JPEG decoder closed or backlogged');
    if(!Number.isSafeInteger(chunk.timestamp)||chunk.timestamp<0||!(chunk.data instanceof Uint8Array)||chunk.data.byteLength<4||chunk.data.byteLength>8*1024*1024)throw Error('Invalid JPEG packet');
    // Copy before asynchronous decoding; no borrowing mutable WS buffers.
    const blob=new Blob([chunk.data.slice()],{type:'image/jpeg'}),timestamp=chunk.timestamp;
    this.decodeQueueSize++;
    this.chain=this.chain.then(async()=>{
      if(this.state==='closed')return;
      const bitmap=await createImageBitmap(blob);
      let transferred=false,closed=false;
      const frame={image:bitmap,timestamp,close(){if(!closed){closed=true;bitmap.close();}}};
      try {
        if(this.state==='closed')return;
        if(bitmap.width!==this.size.width||bitmap.height!==this.size.height)throw Error('JPEG dimensions do not match stream');
        this.output(frame);transferred=true;
      }finally{if(!transferred)frame.close();}
    }).catch(error=>{if(this.state!=='closed'){this.state='closed';this.error(error);}}).finally(()=>{this.decodeQueueSize--;});
  }
  flush(){return this.chain;}
  close(){this.state='closed';}
};
