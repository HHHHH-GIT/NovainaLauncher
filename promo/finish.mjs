import fs from 'node:fs/promises';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import ffmpeg from 'ffmpeg-static';
import ffprobe from 'ffprobe-static';

const root=process.cwd();
const video=path.join(root,'output/Novaina-Launch-Film-1080p.mp4');
const data=JSON.parse(execFileSync(ffprobe.path,['-v','quiet','-show_streams','-show_format','-of','json',video],{encoding:'utf8'}));
const v=data.streams.find(s=>s.codec_type==='video'),a=data.streams.find(s=>s.codec_type==='audio');
if(v.width!==1920||v.height!==1080||v.avg_frame_rate!=='30/1'||Number(v.nb_frames)!==2700||v.codec_name!=='h264'||a?.codec_name!=='aac'||a?.channels!==2||Math.abs(Number(data.format.duration)-90)>.08)throw new Error('Unexpected output format');
execFileSync(ffmpeg,['-hide_banner','-loglevel','error','-i',video,'-f','null','-'],{stdio:['ignore','pipe','pipe']});
const snapshots=[.65,1.28,2.77,5.5,9.5,12.5,16.33,20.17,23.67,28.83,31.67,35.17,38.33,41.33,45,47.33,50.67,53.33,55.5,57.17,59,61.67,63.67,67.33,70,73,76.33,78.5,81,87.67,89.9];
await fs.mkdir(path.join(root,'output/verified'),{recursive:true});
for(const t of snapshots){execFileSync(ffmpeg,['-hide_banner','-loglevel','error','-y','-ss',String(t),'-i',video,'-frames:v','1',path.join(root,`output/verified/${t.toFixed(2)}.png`)],{stdio:['ignore','pipe','pipe']});}
execFileSync(ffmpeg,['-hide_banner','-loglevel','error','-y','-ss','5.4','-i',video,'-frames:v','1','-q:v','2',path.join(root,'output/Novaina-Promo-Poster.jpg')],{stdio:['ignore','pipe','pipe']});
const released=path.resolve(root,'../bin/Novaina-Promo-1.0.0-1080p.mp4');
await fs.mkdir(path.dirname(released),{recursive:true});
const stat=await fs.stat(video);
function pcm(file,start,duration){
 const bytes=execFileSync(ffmpeg,['-hide_banner','-loglevel','error','-ss',String(start),'-i',file,'-t',String(duration),'-ac','1','-ar','16000','-f','f32le','-'],{maxBuffer:32*1024*1024});
 return new Float32Array(bytes.buffer,bytes.byteOffset,Math.floor(bytes.byteLength/4));
}
function correlation(actual,reference){
 let best=-1,bestOffset=0;
 function score(offset){
  let ab=0,aa=0,bb=0;for(let i=34;i<Math.min(actual.length,reference.length)-34;i++){
   const position=i+offset,lower=Math.floor(position),fraction=position-lower;
   const x=actual[i],y=reference[lower]+(reference[lower+1]-reference[lower])*fraction;ab+=x*y;aa+=x*x;bb+=y*y;
  }
  return ab/Math.sqrt(aa*bb);
 }
 for(let offset=-32;offset<=32;offset++){const value=score(offset);if(value>best){best=value;bestOffset=offset;}}
 // MP3 44.1 kHz -> film AAC 48 kHz can create a fractional-sample phase difference.
 // Refine within a single PCM sample instead of mistaking resampling for a timing error.
 const coarse=bestOffset;for(let step=-100;step<=100;step++){const offset=coarse+step/100,value=score(offset);if(value>best){best=value;bestOffset=offset;}}
 return {coefficient:Number(best.toFixed(5)),offsetMs:Number((bestOffset/16).toFixed(4))};
}
const sfxCorrelation=correlation(pcm(video,.2,2.2),pcm(path.join(root,'public/intro-sfx.wav'),.2,2.2));
const musicCorrelation=correlation(pcm(video,4,4),pcm(path.join(root,'public/assumptions.mp3'),1.2,4));
const tail=pcm(video,89.96,.04);const tailRms=Math.sqrt(tail.reduce((sum,x)=>sum+x*x,0)/tail.length);
if(sfxCorrelation.coefficient<.95||musicCorrelation.coefficient<.95||!Number.isFinite(tailRms)||tailRms>.025)throw new Error('Audio timing/fade verification failed: '+JSON.stringify({sfxCorrelation,musicCorrelation,tailRms}));
await fs.copyFile(video,released);
await fs.writeFile(path.join(root,'output/verification.json'),JSON.stringify({duration:90,width:1920,height:1080,fps:30,frames:2700,videoCodec:v.codec_name,audioCodec:a.codec_name,audioChannels:a.channels,fileBytes:stat.size,fullDecode:'passed',sampledFrames:snapshots,musicStartsAtSeconds:2.8,audioVerification:{sfxCorrelation,musicCorrelation,tailRms},introSound:'Preserved original synthetic stereo sound',interface:'Native WPF captures with isolated scripted demo fixtures; native popup controls; same skinview3d renderer composited into its preview region; not a live API, installation or game test',releasedFile:released},null,2));
console.log(JSON.stringify({file:released,bytes:stat.size,verification:'passed',duration:90,audioVerification:{sfxCorrelation,musicCorrelation,tailRms}}));
