import {bundle} from '@remotion/bundler';
import {openBrowser,selectComposition,renderStill,renderMedia} from '@remotion/renderer';
import {mkdir,writeFile} from 'node:fs/promises';
import {existsSync} from 'node:fs';
import path from 'node:path';
import os from 'node:os';

const root=process.cwd(), output=path.join(root,'output');
await mkdir(output,{recursive:true});
const browserPath=[process.env.NOVAINA_RENDER_BROWSER,'C:/Users/LBD06/AppData/Local/ms-playwright/chromium-1228/chrome-win64/chrome.exe','C:/Program Files/Google/Chrome/Application/chrome.exe','C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find(p=>p&&existsSync(p));
const mode=process.argv[2]||'stills';
console.log(`Bundling Novaina film (${mode}); CPU ${os.availableParallelism()}, RAM ${(os.freemem()/1024**3).toFixed(1)} GiB`);
const serveUrl=await bundle({entryPoint:path.join(root,'src/index.tsx'),publicDir:path.join(root,'public'),onProgress:p=>{if(p===100)console.log('Bundle ready')}});
const browser=await openBrowser('chrome',{browserExecutable:browserPath,chromiumOptions:{gl:'angle'}});
const composition=await selectComposition({serveUrl,id:'Novaina',puppeteerInstance:browser});
let sampledFrames;
try{
 if(mode==='stills'){
  const frames=process.env.NOVAINA_STILL_FRAMES?.split(',').map(Number)||[20,38,55,83,165,285,375,490,605,710,865,950,1055,1150,1240,1350,1420,1520,1600,1665,1715,1770,1850,1910,2020,2100,2190,2290,2355,2430,2630];
  sampledFrames=frames;
  for(const frame of frames){await renderStill({serveUrl,composition,frame,output:path.join(output,`frame-${String(frame).padStart(4,'0')}.png`),puppeteerInstance:browser,imageFormat:'png',logLevel:'error'});console.log(`Still ${frame}/${composition.durationInFrames}`)}
 }else{
  let last=0;const start=Date.now();
  await renderMedia({serveUrl,composition,puppeteerInstance:browser,codec:'h264',audioCodec:'aac',audioBitrate:'320k',crf:18,x264Preset:'fast',pixelFormat:'yuv420p',imageFormat:'jpeg',jpegQuality:95,concurrency:Math.min(6,Math.max(2,Math.floor(os.availableParallelism()/3))),outputLocation:path.join(output,'Novaina-Launch-Film-1080p.mp4'),metadata:{title:'Novaina Launcher — From an idea to a world',comment:'Scripted product interface demonstration. Music supplied by the user.'},onProgress:p=>{const now=Date.now();if(now-last>10000||p.progress===1){console.log(`${(p.progress*100).toFixed(1)}% · ${p.renderedFrames}/${composition.durationInFrames} frames · ${((now-start)/1000).toFixed(0)}s`);last=now}},timeoutInMilliseconds:60000});
  console.log('MP4 render complete');
 }
 await writeFile(path.join(output,`render-${mode}.json`),JSON.stringify({composition:{width:composition.width,height:composition.height,fps:composition.fps,durationInFrames:composition.durationInFrames},renderedAt:new Date().toISOString(),mode,sampledFrames,browser:browserPath},null,2));
}finally{await browser.close({silent:true})}
