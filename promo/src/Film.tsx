import React, {useEffect, useLayoutEffect, useRef, useState} from 'react';
import {AbsoluteFill, Img, Sequence, Audio, staticFile, useCurrentFrame, delayRender, continueRender, cancelRender} from 'remotion';
import {SkinViewer} from 'skinview3d';

export const FPS=30, DURATION=2700;
const BLUE='#007aff', PURPLE='#af52de';
const clamp=(x:number)=>Math.max(0,Math.min(1,x));
const out=(x:number)=>1-Math.pow(1-clamp(x),4);
const smooth=(x:number)=>{x=clamp(x);return x*x*(3-2*x)};
const lerp=(a:number,b:number,p:number)=>a+(b-a)*p;
const at=(f:number,start:number,end:number)=>clamp((f-start)/(end-start));
const kf=(p:number,keys:number[][])=>{for(let i=1;i<keys.length;i++){if(p<=keys[i][0])return lerp(keys[i-1][1],keys[i][1],at(p,keys[i-1][0],keys[i][0]));}return keys[keys.length-1][1]};
const reveal=(f:number,start=0,offset=35)=>({opacity:out(at(f,start,start+28)),transform:`translateY(${(1-out(at(f,start,start+34)))*offset}px)`});
export const Logo=({size=36,style={}}:{size?:number;style?:React.CSSProperties})=><Img src={staticFile('logo.png')} style={{width:size,height:size,objectFit:'contain',...style}}/>;
import {Downloads, Games, Skins, Logs, AiReveal, BasicPrompt, Questions, Execution, Context, WorkbenchReveal, Development, Build, Delivery, Outro} from './ProductScenes';

export function Background({light=false,f=0}:{light?:boolean;f?:number}){
 return <AbsoluteFill style={{background:light?'#f4f6fa':'#070a12'}}><AbsoluteFill style={{background:`radial-gradient(ellipse at ${lerp(20,38,clamp(f/220))}% 75%,${light?'#dfeaff':'#08244d'} 0,transparent 55%),radial-gradient(ellipse at 83% 25%,${light?'#eee5ff':'#211238'} 0,transparent 55%)`,opacity:light?.74:.8}}/></AbsoluteFill>
}
export function Intro({f}:{f:number}){
 const p=f/84;const expansion=out(at(p,.355,.95));const flash=kf(p,[[0,0],[.34,0],[.385,.9],[.50,0]]);
 return <AbsoluteFill style={{background:'#090d17'}}><AbsoluteFill style={{background:'radial-gradient(ellipse at center,#13233a 0,transparent 65%)',opacity:.35}}/>
 {[BLUE,PURPLE].map((c,i)=><div key={c} style={{position:'absolute',width:500,height:500,left:710,top:290,borderRadius:'50%',background:`radial-gradient(ellipse at ${i?60:42}% 50%,${c}b0,${c}50 30%,${c}00 68%)`,opacity:kf(p,i?[[0,0],[.21,.12],[.34,.18],[.50,.95],[.78,.35],[1,0]]:[[0,0],[.16,.18],[.34,.22],[.47,1],[.72,.48],[1,0]]),transform:`scale(${lerp(.3,7.4,expansion)})`}}/>)}
 <div style={{position:'absolute',width:250,height:250,left:835,top:415,borderRadius:'50%',border:'2px solid #7e7aff',opacity:kf(p,[[0,0],[.36,0],[.395,.6],[.62,.2],[.88,0]]),transform:`scale(${lerp(.7,12,out(at(p,.36,.9)))})`}}/>
 <div style={{position:'absolute',left:830,top:410,width:260,height:260,borderRadius:'50%',background:'radial-gradient(circle,#effaff,#7e9eff77 30%,#af52de00 65%)',opacity:flash,transform:`scale(${lerp(.3,2.7,out(at(p,.345,.57)))})`}}/>
 <Logo size={245} style={{position:'absolute',left:837.5,top:417.5,opacity:kf(p,[[0,0],[.16,1],[.21,1],[.24,.38],[.275,1],[.36,1],[.395,0]]),transform:`scale(${kf(p,[[0,.86],[.18,1.025],[.31,1],[.37,1.08],[.44,1.32]])})`}}/>
 {Array.from({length:64},(_,i)=>{const x=i%8,y=Math.floor(i/8),dx=x-3.5,dy=y-3.5,ang=Math.atan2(dy,dx)+Math.sin(i*7.31)*.16,spread=Math.abs(Math.sin(i*2.3)),dist=2203*(.38+spread*.24)*out(at(p,.38,.95)),a=kf(p,[[0,0],[.365,0],[.38,1],[.46,1],[.78+spread*.15,0]]);return <div key={i} style={{position:'absolute',left:837.5+x*30.625,top:417.5+y*30.625,width:30.9,height:30.9,overflow:'hidden',opacity:a,transform:`translate(${Math.cos(ang)*dist}px,${Math.sin(ang)*dist}px) rotate(${dist/30*Math.sin(i)}deg)`}}><Logo size={245} style={{position:'absolute',left:-x*30.625,top:-y*30.625}}/></div>})}
 </AbsoluteFill>
}
export function Hero({f}:{f:number}){return <AbsoluteFill><Background f={f}/><div style={{position:'absolute',left:0,top:95,width:'100%',textAlign:'center'}}><Logo size={230} style={{...reveal(f,0,45),transform:`translateY(${lerp(45,0,out(at(f,0,35)))}px) scale(${lerp(.85,1,out(at(f,0,42)))})`}}/><div style={{fontSize:174,letterSpacing:-9,fontWeight:650,marginTop:10,...reveal(f,9)}}>Novaina</div><div style={{fontSize:31,letterSpacing:11,color:'#93a8d1',margin:'8px 0 37px',...reveal(f,18)}}>LAUNCHER</div><div style={{fontSize:54,letterSpacing:-2,fontWeight:600,...reveal(f,28)}}>从一句想法，到整个世界。</div><div className="gradient" style={{fontSize:23,letterSpacing:3,marginTop:36,...reveal(f,45)}}>INTRODUCING NOVAINA 1.0</div></div><div style={{position:'absolute',bottom:0,width:'100%',height:170,background:'linear-gradient(0deg,#14245155,transparent)'}}/></AbsoluteFill>}
export function Skin({f,width=510,height=620}:{f:number;width?:number;height?:number}){
 const canvas=useRef<HTMLCanvasElement>(null);const viewer=useRef<SkinViewer|null>(null);const [ready,setReady]=useState(false);const [handle]=useState(()=>delayRender('Loading bundled Steve skin'));
 useEffect(()=>{const v=new SkinViewer({canvas:canvas.current!,width,height,renderPaused:true,enableControls:false,zoom:.86,fov:40});viewer.current=v;v.camera.position.set(14,7,57);v.camera.lookAt(0,0,0);v.loadSkin(staticFile('steve.png'),{model:'default'}).then(()=>{setReady(true);continueRender(handle)}).catch(e=>cancelRender(e));return ()=>{v.dispose();viewer.current=null}},[]);
 useLayoutEffect(()=>{if(!ready||!viewer.current)return;const v=viewer.current;v.playerObject.rotation.y=lerp(-.55,1.65,smooth(at(f,0,189)));v.playerObject.skin.head.rotation.y=Math.sin(f/65)*.13;v.playerObject.skin.rightArm.rotation.z=.1;v.playerObject.skin.leftArm.rotation.z=-.1;v.playerObject.position.y=Math.sin(f/45)*.3;v.render();},[ready,f]);
 return <canvas ref={canvas} style={{width,height,position:'relative'}}/>;
}

export const scenes=[
 {start:0,end:84,Comp:Intro,label:'超新星开场'},
 {start:84,end:210,Comp:Hero,label:'品牌揭示'},
 {start:210,end:420,Comp:Downloads,label:'下载资源'},
 {start:420,end:630,Comp:Games,label:'游戏管理'},
 {start:630,end:810,Comp:Skins,label:'皮肤预览'},
 {start:810,end:990,Comp:Logs,label:'弹幕日志'},
 {start:990,end:1080,Comp:AiReveal,label:'AI 揭示'},
 {start:1080,end:1290,Comp:BasicPrompt,label:'基础模式'},
 {start:1290,end:1470,Comp:Questions,label:'偏好提问'},
 {start:1470,end:1680,Comp:Execution,label:'工具调用与执行'},
 {start:1680,end:1800,Comp:Context,label:'目标、引用与上下文'},
 {start:1800,end:1950,Comp:WorkbenchReveal,label:'工作台与会话'},
 {start:1950,end:2130,Comp:Development,label:'Mod 开发计划'},
 {start:2130,end:2370,Comp:Build,label:'编辑、编译与验证'},
 {start:2370,end:2520,Comp:Delivery,label:'JAR 交付'},
 {start:2520,end:2700,Comp:Outro,label:'品牌收尾'}
];
export function Film(){
 const frame=useCurrentFrame();const scene=scenes.find(s=>frame>=s.start&&frame<s.end)!;const Component=scene.Comp;
 return <AbsoluteFill className="film"><Component f={frame-scene.start}/><Sequence from={0} durationInFrames={100}><Audio src={staticFile('intro-sfx.wav')} volume={.85}/></Sequence><Sequence from={84} durationInFrames={DURATION-84}><Audio src={staticFile('assumptions.mp3')} volume={f=>.78*Math.min(1,f/9)*Math.min(1,(DURATION-84-f)/75)}/></Sequence></AbsoluteFill>;
}
