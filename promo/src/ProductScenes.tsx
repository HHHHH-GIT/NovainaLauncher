import React from 'react';
import {AbsoluteFill, Img, staticFile} from 'remotion';
import {Background, Logo, Skin} from './Film';
import skinRegion from '../public/native/skin-region.json';

const c=(n:number)=>Math.max(0,Math.min(1,n));
const ease=(n:number)=>{n=c(n);return n*n*(3-2*n)};
const out=(n:number)=>1-(1-c(n))**4;
const p=(f:number,a:number,b:number)=>c((f-a)/(b-a));
const mix=(a:number,b:number,t:number)=>a+(b-a)*t;
const fade=(f:number,a=0)=>({opacity:out(p(f,a,a+24)),transform:`translateY(${mix(24,0,out(p(f,a,a+35)))}px)`});
const asset=(name:string)=>staticFile(`native/${name}.png`);

// All launcher chrome, controls and text are native WPF captures. Camera transforms never reflow them.
export function NativeWindow({name,next,blend=0,width=1260,style={},children}:{name:string;next?:string;blend?:number;width?:number;style?:React.CSSProperties;children?:React.ReactNode}){
 return <div className="native-window" style={{width,height:width*700/1080,...style}}><Img src={asset(name)} style={{width:'100%',height:'100%',position:'absolute',inset:0}}/>{next&&<Img src={asset(next)} style={{width:'100%',height:'100%',position:'absolute',inset:0,opacity:c(blend)}}/>}{children}</div>;
}
function Label({f,kicker,title,light=false,subtitle,side=false}:{f:number;kicker:string;title:string;light?:boolean;subtitle?:string;side?:boolean}){
 return <div style={{position:'absolute',left:side?110:130,top:side?235:58,color:light?'#171c29':'#f7f9ff',maxWidth:side?500:1700,zIndex:3}}><div className="eyebrow" style={{...fade(f),marginBottom:side?27:12}}>{kicker}</div><div style={{fontSize:side?86:76,fontWeight:700,letterSpacing:-3,lineHeight:1.22,whiteSpace:'pre-line',...fade(f,5)}}>{title}</div>{subtitle&&<div style={{fontSize:25,lineHeight:1.6,color:light?'#68758c':'#9faeca',marginTop:24,whiteSpace:'pre-line',...fade(f,18)}}>{subtitle}</div>}</div>;
}
function Footer({text='产品流程演示 · 时间压缩',light=false}:{text?:string;light?:boolean}){return <div style={{position:'absolute',bottom:23,right:85,fontSize:15,letterSpacing:.2,color:light?'#6f7d93':'#7f8eab'}}>{text}</div>}
function Closeup({name,next,blend=0,f,width=1350,x=525,y=188,origin='60% 45%',zoom=1}:{name:string;next?:string;blend?:number;f:number;width?:number;x?:number;y?:number;origin?:string;zoom?:number}){const t=ease(p(f,0,200));return <NativeWindow name={name} next={next} blend={blend} width={width} style={{position:'absolute',left:x,top:y,transformOrigin:origin,opacity:out(p(f,0,22)),transform:`perspective(2800px) rotateY(${mix(-5,1,t)}deg) rotateX(${mix(2,0,t)}deg) scale(${mix(.98,zoom,t)})`}}/>}

export function Downloads({f}:{f:number}){
 const second=f>=111;
 return <AbsoluteFill><Background light f={f}/><Label f={f} light side kicker="GAMES. MODS. MODPACKS." title={'下一段冒险。\n从这里开始。'} subtitle={'游戏 · 模组 · 整合包\n国内镜像优先，官方源回退。'}/><Closeup f={f} name="downloads" next="loaders" blend={ease(p(f,111,127))} x={655} y={140} width={1190} zoom={1.04}/><div style={{position:'absolute',left:116,top:696,display:'flex',gap:12,...fade(f,50)}}>{['原版','Forge','Fabric','NeoForge'].map((x,i)=><span key={x} className="feature-chip" style={{background:i===2?'#007aff18':'#ffffff77',color:i===2?'#007aff':'#64728b',borderColor:'#b9c9df'}}>{x}</span>)}</div><Footer light text={second?'实际加载器选择页 · 演示流程':'实际下载页面'}/></AbsoluteFill>;
}
export function Games({f}:{f:number}){
 let name='versions',next='version-overview',blend=ease(p(f,75,91)); if(f>=145){name='version-overview';next='mods';blend=ease(p(f,145,161));}
 return <AbsoluteFill><Background light f={f}/><Label f={f} light kicker="YOUR WORLDS. IN ONE PLACE." title="每个世界，都井然有序。"/><NativeWindow name={name} next={next} blend={blend} width={1245} style={{position:'absolute',left:338,top:227,opacity:out(p(f,0,25)),transform:`perspective(2800px) rotateY(${mix(3,-2,ease(f/220))}deg) scale(${mix(.985,1.025,ease(f/220))})`}}/><div style={{position:'absolute',left:131,top:475,width:205,...fade(f,35)}}>{['版本与实例','Mod 与存档','资源包与光影','导入与导出'].map((x,i)=><div key={x} style={{fontSize:25,color:'#6a7890',lineHeight:2.7,...fade(f,35+i*7)}}>{x}</div>)}</div><Footer light/></AbsoluteFill>;
}
export function Skins({f}:{f:number}){
 const w=1170, scale=w/1080;
 return <AbsoluteFill><Background light f={f}/><Label f={f} light side kicker="LIVE 3D SKIN PREVIEW." title={'你的形象。\n立体呈现。'} subtitle={'Microsoft · LittleSkin · 离线账户\n旋转、缩放，实时预览。'}/><NativeWindow name="accounts" width={w} style={{position:'absolute',left:690,top:147,opacity:out(p(f,0,26)),transform:`perspective(2700px) rotateY(${mix(-6,-1,ease(f/180))}deg)`}}><div style={{position:'absolute',left:skinRegion.x*scale,top:skinRegion.y*scale,width:skinRegion.width*scale,height:skinRegion.height*scale,overflow:'hidden',display:'grid',placeItems:'center'}}><div style={{position:'absolute',width:225,height:22,bottom:12,borderRadius:'50%',background:'radial-gradient(ellipse,#899caf55,transparent 70%)'}}/><Skin f={f} width={Math.round(skinRegion.width*scale)} height={Math.round(skinRegion.height*scale)}/></div></NativeWindow><Footer light text="实际账户页面 · 同款 skinview3d 渲染器"/></AbsoluteFill>;
}
export function Logs({f}:{f:number}){
 const entries=['Java 21 · 运行环境就绪','Fabric Loader 初始化完成','资源完整性校验通过','正在启动「新星冒险」','游戏进程已启动'];
 return <AbsoluteFill><Background f={f}/><Label f={f} kicker="LOGS, IN MOTION." title="每一步，都看得见。"/><NativeWindow name="danmaku-background" width={1280} style={{position:'absolute',left:320,top:211,opacity:out(p(f,0,25)),transform:`perspective(2800px) rotateX(1deg) rotateY(${mix(-2,2,ease(f/180))}deg)`}}><div style={{position:'absolute',inset:'60px 0 0',overflow:'hidden'}}>{entries.map((text,i)=>{const start=10+i*18;return <div key={text} className="real-danmaku" style={{position:'absolute',top:70+(i%3)*41,left:mix(1280,-400,p(f,start,start+145)),opacity:out(p(f,start,start+10)),fontSize:14.3,padding:'6px 14px',borderRadius:14}}>{text}</div>})}</div></NativeWindow><div style={{position:'absolute',bottom:88,left:130,fontSize:27,color:'#a8b8d2',...fade(f,42)}}>运行日志，化作流动的弹幕。</div><Footer/></AbsoluteFill>;
}
export function AiReveal({f}:{f:number}){
 const next=f>=45;
 return <AbsoluteFill><Background f={f}/><AbsoluteFill style={{background:'radial-gradient(ellipse at 24% 70%,#007aff44,transparent 58%),radial-gradient(ellipse at 80% 25%,#af52de4f,transparent 55%)',opacity:out(f/30)}}/><div style={{position:'absolute',width:'100%',top:177,textAlign:'center'}}><div className="gradient" style={{fontSize:28,letterSpacing:7,...fade(f)}}>AI MODE</div><div style={{fontSize:146,fontWeight:700,letterSpacing:-7,marginTop:92,opacity:next?out(p(f,45,60)):1-p(f,31,44),transform:`translateY(${next?mix(40,0,out(p(f,45,73))):mix(35,-15,out(f/44))}px)`}}>{next?'开始，指挥。':'不止启动。'}</div><div style={{fontSize:35,color:'#c0c4df',marginTop:42,...fade(f,53)}}>你的想法，成为下一步行动。</div></div></AbsoluteFill>;
}
export function BasicPrompt({f}:{f:number}){
 const send=f>=124;
 return <AbsoluteFill><Background f={f}/><Label f={f} kicker="BASIC MODE." title="你说一句。剩下的，交给它。"/><NativeWindow name={send?'basic-questions':'basic-empty'} next={send?undefined:'basic-draft'} blend={ease(p(f,37,60))} width={1245} style={{position:'absolute',left:338,top:223,opacity:out(p(f,0,24)),transform:`perspective(2900px) rotateY(${mix(-3,1,ease(f/210))}deg) scale(${mix(.99,1.03,ease(f/210))})`}}/><Footer/></AbsoluteFill>;
}
export function Questions({f}:{f:number}){
 let name='basic-questions',next='basic-selected',blend=ease(p(f,46,61));if(f>=111){name='basic-selected';next='basic-answered';blend=ease(p(f,111,127));}
 return <AbsoluteFill><Background f={f}/><Label f={f} kicker="UNDERSTAND. THEN ACT." title="先理解，再行动。"/><NativeWindow name={name} next={next} blend={blend} width={1290} style={{position:'absolute',left:315,top:204,opacity:out(p(f,0,20)),transform:`perspective(3100px) rotateX(${mix(2,0,ease(f/180))}deg)`}}/><div style={{position:'absolute',left:132,right:132,bottom:35,fontSize:22,color:'#9facca',...fade(f,35)}}>选项或自定义回答。确认后，再继续。</div></AbsoluteFill>;
}
export function Execution({f}:{f:number}){
 let name='basic-running',next='basic-collapsed',blend=ease(p(f,65,80)); if(f>=116){name='basic-collapsed';next='basic-details';blend=ease(p(f,116,131));}if(f>=169){name='basic-details';next='basic-result';blend=ease(p(f,169,185));}
 return <AbsoluteFill><Background f={f}/><Label f={f} kicker="TOOLS. TASKS. RESULTS." title="它来执行。你来期待。"/><NativeWindow name={name} next={next} blend={blend} width={1250} style={{position:'absolute',left:335,top:221,opacity:out(p(f,0,22)),transform:`perspective(3100px) rotateY(${mix(3,-1,ease(f/220))}deg)`}}/><Footer text="工具调用自动归组 · 完成后折叠 · 结果可展开"/></AbsoluteFill>;
}
export function Context({f}:{f:number}){
 const next=f>68;
 return <AbsoluteFill><Background f={f}/><Label f={f} side kicker="A CONVERSATION THAT CONTINUES." title={'目标不丢。\n思路不断。'} subtitle={'/goal 持续目标\n@ 游戏、Java 与账户\n/compact 手动压缩'}/><NativeWindow name={next?'compacted':'context-ring'} width={1185} style={{position:'absolute',left:665,top:143,opacity:out(p(f,0,20)),transform:'perspective(2800px) rotateY(-3deg)'}}/>{!next&&<Img src={asset('context-popup')} style={{position:'absolute',left:1100,top:337,width:430,height:'auto',boxShadow:'0 25px 75px #0008',borderRadius:20,...fade(f,13)}}/>}<Footer text="上下文分类用量 · 完整操作结束后自动压缩"/></AbsoluteFill>;
}
export function WorkbenchReveal({f}:{f:number}){
 return <AbsoluteFill><Background f={f}/><Label f={f} side kicker="MEET THE WORKBENCH." title={'从玩家。\n到创造者。'} subtitle={'基础模式：游玩、安装与管理\n工作台：创建、编译与交付 Mod'}/><NativeWindow name="workbench-empty" next="workbench-draft" blend={ease(p(f,74,94))} width={1185} style={{position:'absolute',left:665,top:143,opacity:out(p(f,0,25)),transform:`perspective(2800px) rotateY(${mix(-5,1,ease(f/150))}deg)`}}/><Footer text="独立模式 · 独立会话 · 本地加密保存"/></AbsoluteFill>;
}
export function Development({f}:{f:number}){
 const name=f>94?'workbench-running':'workbench-plan';
 return <AbsoluteFill><Background f={f}/><Label f={f} kicker="ONE IDEA. YOUR FIRST MOD." title="第一款 Mod，从一个想法开始。"/><NativeWindow name={name} width={1240} style={{position:'absolute',left:340,top:226,opacity:out(p(f,0,25)),transform:`perspective(2900px) rotateY(${mix(-3,1,ease(f/180))}deg)`}}/><Footer text="核对官方流程 · 选择模板与 JDK · 编辑源码与资源"/></AbsoluteFill>;
}
export function Build({f}:{f:number}){
 let name='workbench-running',next='workbench-code',blend=ease(p(f,36,52)); if(f>=112){name='workbench-code';next='workbench-build';blend=ease(p(f,112,132));} if(f>=192){name='workbench-build';next='workbench-collapsed';blend=ease(p(f,192,208));}
 return <AbsoluteFill><Background f={f}/><Label f={f} side kicker="READ. WRITE. BUILD." title={'写下创意。\n编译成真。'} subtitle={'源码 · 中文资源 · 合成配方\n读取结果，修正，再验证。'}/><NativeWindow name={name} next={next} blend={blend} width={1185} style={{position:'absolute',left:665,top:143,opacity:out(p(f,0,23)),transform:`perspective(3000px) rotateY(${mix(-4,1,ease(f/240))}deg)`}}/>{f>=142&&<div style={{position:'absolute',left:118,top:725,fontSize:27,color:'#69e2b9',...fade(f,142)}}>BUILD SUCCESSFUL</div>}<Footer text="模组开发流程演示 · 编译通过与游戏内验证分开报告"/></AbsoluteFill>;
}
export function Delivery({f}:{f:number}){
 return <AbsoluteFill><Background f={f}/><Label f={f} kicker="FROM AN IDEA TO A JAR." title="交付的不只是回答。"/><NativeWindow name="workbench-delivery" width={1210} style={{position:'absolute',left:355,top:237,opacity:out(p(f,0,24)),transform:'perspective(3000px) rotateY(-2deg)'}}/><div className="artifact-card" style={{position:'absolute',left:1120,top:675,...fade(f,39)}}><div className="artifact-icon">JAR</div><div><div style={{fontSize:23,fontWeight:600}}>starberry-1.0.0.jar</div><div style={{fontSize:17,color:'#a6b1c9',marginTop:9}}>代码、资源与验证清单。</div></div></div><Footer text="编译与打包已验证 · 游戏体验仍需实际测试"/></AbsoluteFill>;
}
export function Outro({f}:{f:number}){
 return <AbsoluteFill><Background f={f}/><div style={{position:'absolute',width:'100%',textAlign:'center',top:143}}><Logo size={205} style={fade(f)}/><div style={{fontSize:91,letterSpacing:-4,fontWeight:650,marginTop:28,...fade(f,13)}}>Novaina <span className="gradient">Launcher</span></div><div style={{fontSize:47,fontWeight:600,marginTop:43,...fade(f,24)}}>把想法，交给新星。</div><div style={{fontSize:23,color:'#94a8c9',letterSpacing:3,marginTop:36,...fade(f,39)}}>游玩 · 管理 · 创造</div></div><div style={{position:'absolute',bottom:65,width:'100%',textAlign:'center',fontSize:19,color:'#8e9eb9',...fade(f,64)}}>github.com/HHHHH-GIT/NovainaLauncher</div><div style={{position:'absolute',bottom:29,width:'100%',textAlign:'center',fontSize:14,color:'#65768f',...fade(f,66)}}>产品流程演示 · Sam Gellaitry — Assumptions</div><AbsoluteFill style={{background:'#05070c',opacity:p(f,163,179)}}/></AbsoluteFill>;
}
