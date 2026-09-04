'use strict';
// Measures the actual orange marker drawn by the native F0 window, after WGC/encoding/decoding.
// No synthetic marker is drawn over the video. Opt-in own-fixture clicks only; not NX/P05 acceptance.
globalThis.F0PointerCalibration=class {
  constructor(canvas){this.canvas=canvas;this.context=canvas.getContext('2d');this.pending=null;this.results=[];}
  static centroid(image){
    let count=0,x=0,y=0,minX=image.width,minY=image.height,maxX=-1,maxY=-1;
    for(let i=0;i<image.data.length;i+=4){
      const r=image.data[i],g=image.data[i+1],b=image.data[i+2];
      if(r<200||g<90||g>215||b>85)continue;
      const px=(i/4)%image.width,py=Math.floor(i/4/image.width);count++;x+=px+0.5;y+=py+0.5;
      minX=Math.min(minX,px);maxX=Math.max(maxX,px);minY=Math.min(minY,py);maxY=Math.max(maxY,py);
    }
    return count>=8&&count<=2500&&maxX-minX<=64&&maxY-minY<=64?{x:x/count,y:y/count,count}:null;
  }
  finish(status,extra={}){
    if(!this.pending)return;
    if(this.results.length===32)this.results.shift();
    this.results.push({...this.pending,status,...extra});this.pending=null;
  }
  begin(scene,frame,sequence,point,client){
    this.finish('SUPERSEDED_NOT_MEASURED');
    if(![scene.sourceWidth,scene.sourceHeight].every(v=>Number.isInteger(v)&&v>0&&v<=8192)||scene.nodeCount!==1)return;
    const r=scene.contentRect,x=r.x+point.u*r.width,y=r.y+point.v*r.height;
    const left=Math.max(0,Math.floor(x)-64),top=Math.max(0,Math.floor(y)-64);
    this.pending={scene:scene.version,afterFrame:frame,sequence,inputAccepted:false,expected:{x,y},client,
      sourcePerOutputX:scene.sourceWidth/r.width,sourcePerOutputY:scene.sourceHeight/r.height,
      region:{left,top,width:Math.min(129,this.canvas.width-left),height:Math.min(129,this.canvas.height-top)}};
    if(this.sample())this.finish('EXISTING_MARKER_NEAR_TARGET_NOT_MEASURED');
  }
  sample(){const r=this.pending.region;return F0PointerCalibration.centroid(this.context.getImageData(r.left,r.top,r.width,r.height));}
  observe(scene,frame){
    if(!this.pending)return;
    if(scene.version!==this.pending.scene){this.finish('SCENE_CHANGED_NOT_MEASURED');return;}
    if(frame<=this.pending.afterFrame)return;
    const found=this.sample();if(!found)return;
    const p=this.pending,observed={x:p.region.left+found.x,y:p.region.top+found.y};
    const errorX=(observed.x-p.expected.x)*p.sourcePerOutputX,errorY=(observed.y-p.expected.y)*p.sourcePerOutputY;
    const sourceError=Math.hypot(errorX,errorY);
    p.observation={observed,frame,pixels:found.count,errorX,errorY,sourceError};
    if(p.inputAccepted)this.finish(sourceError<=2?'WITHIN_2_SOURCE_PIXELS':'OUTSIDE_2_SOURCE_PIXELS',p.observation);
  }
  acknowledge(sequence,accepted){
    if(this.pending?.sequence!==sequence)return;
    if(!accepted){this.finish('INPUT_REJECTED_NOT_MEASURED');return;}
    this.pending.inputAccepted=true;
    const observation=this.pending.observation;
    if(observation)this.finish(observation.sourceError<=2?'WITHIN_2_SOURCE_PIXELS':'OUTSIDE_2_SOURCE_PIXELS',observation);
  }
  summary(){return {scope:'Opt-in F0 decoded orange-marker click measurement, no drawn overlay; not 100-target/P05 acceptance or latency',results:this.results,pending:this.pending};}
};
