'use strict';
globalThis.probeVideoSize=function(width,height){
  if(!((width===1280&&height===720)||(width===1920&&height===1080)))throw Error('Unsupported or mismatched video profile');
  return Object.freeze({width,height});
};
