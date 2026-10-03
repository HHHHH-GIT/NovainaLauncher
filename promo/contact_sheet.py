from pathlib import Path
from PIL import Image, ImageDraw
import wave
import numpy as np

root=Path(__file__).parent/'output'
frames=sorted(root.glob('frame-*.png'))
canvas=Image.new('RGB',(1440,((len(frames)+2)//3)*296),'#10151f')
draw=ImageDraw.Draw(canvas)
for i,p in enumerate(frames):
    im=Image.open(p).convert('RGB');im.thumbnail((470,264))
    x=(i%3)*480+5;y=(i//3)*296+5
    canvas.paste(im,(x,y));draw.text((x,y+269),f'{int(p.stem.split("-")[1])/30:.2f}s',fill='#b5c5dc')
canvas.save(root/'storyboard.jpg',quality=94)
with wave.open(str(root/'music-analysis.wav'),'rb') as w:
    a=np.frombuffer(w.readframes(w.getnframes()),dtype=np.int16).astype(float)/32768
en=np.sqrt((a[:len(a)//160*160].reshape(-1,160)**2).mean(axis=1))
on=np.maximum(np.diff(en,prepend=en[0]),0)
scores=[]
for bpm in np.arange(90,150,.1):
    lag=60/bpm*50
    shifted=np.interp(np.arange(len(on))-lag,np.arange(len(on)),on,left=0,right=0)
    scores.append((float(np.dot(on,shifted)),round(float(bpm),1)))
print('Strongest beat periods:', sorted(scores,reverse=True)[:5])
