import fs from 'node:fs/promises';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import ffmpeg from 'ffmpeg-static';
import ffprobe from 'ffprobe-static';

const root=process.cwd();
const video=path.join(root,'output/Novaina-Launch-Film-1080p.mp4');
const data=JSON.parse(execFileSync(ffprobe.path,['-v','quiet','-show_streams','-show_format','-of','json',video],{encoding:'utf8'}));
const v=data.streams.find(s=>s.codec_type==='video'),a=data.streams.find(s=>s.codec_type==='audio');
if(v.width!==1920||v.height!==1080||v.avg_frame_rate!=='30/1'||Number(v.nb_frames)!==1950||!a||Math.abs(Number(data.format.duration)-65)>.08)throw new Error('Unexpected output format');
execFileSync(ffmpeg,['-hide_banner','-loglevel','error','-i',video,'-f','null','-'],{stdio:['ignore','pipe','pipe']});
const snapshots=[.65,1.28,5.3,10.5,16.6,21.3,26.3,28.5,32.2,36.3,39.1,42.1,44.5,48.8,53.8,57.8,62.3];
await fs.mkdir(path.join(root,'output/verified'),{recursive:true});
for(const t of snapshots){execFileSync(ffmpeg,['-hide_banner','-loglevel','error','-y','-ss',String(t),'-i',video,'-frames:v','1',path.join(root,`output/verified/${t.toFixed(2)}.png`)],{stdio:['ignore','pipe','pipe']});}
execFileSync(ffmpeg,['-hide_banner','-loglevel','error','-y','-ss','5.4','-i',video,'-frames:v','1','-q:v','2',path.join(root,'output/Novaina-Promo-Poster.jpg')],{stdio:['ignore','pipe','pipe']});
const released=path.resolve(root,'../bin/Novaina-Promo-1.0.0-1080p.mp4');
await fs.copyFile(video,released);
const stat=await fs.stat(video);
await fs.writeFile(path.join(root,'output/verification.json'),JSON.stringify({duration:65,width:1920,height:1080,fps:30,frames:1950,videoCodec:v.codec_name,audioCodec:a.codec_name,audioChannels:a.channels,fileBytes:stat.size,fullDecode:'passed',sampledFrames:snapshots,musicStartsAtSeconds:2.8,introSound:'Original synthetic stereo sound',interface:'Scripted HTML demonstration; not a live installation recording',releasedFile:released},null,2));
console.log(JSON.stringify({file:released,bytes:stat.size,verification:'passed',duration:65}));
