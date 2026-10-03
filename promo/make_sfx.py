"""Original short supernova sound: crystalline arrival, flicker and expanding burst."""
import math, random, struct, wave
from pathlib import Path

SR=48000
LENGTH=3.34
random.seed(731)
samples=[]
low=0.0
for n in range(int(SR*LENGTH)):
    t=n/SR
    noise=random.uniform(-1,1)
    low=low*.92+noise*.08
    arrival=0.0
    for start,hz in [(0.12,880),(0.20,1320),(0.29,1760),(.64,2093)]:
        dt=t-start
        if dt>=0:
            arrival+=.035*math.sin(2*math.pi*hz*dt)*math.exp(-dt*5)*min(1,dt*100)
    rise=.036*(noise-low)*max(0,min(1,(t-.68)/.36)) if t<1.08 else 0
    dt=t-1.05
    burst=0
    if dt>=0:
        phase=2*math.pi*(44*dt+76*.17*(1-math.exp(-dt/.17)))
        boom=.29*math.sin(phase)*math.exp(-dt*2.3)*min(1,dt*160)
        air=.19*low*math.exp(-dt*1.8)*min(1,dt*130)
        snap=.065*(noise-low)*math.exp(-dt*10)
        crystal=.020*(math.sin(dt*2*math.pi*1568)+math.sin(dt*2*math.pi*2349))*math.exp(-dt*2.5)
        burst=boom+air+snap+crystal
    value=(arrival+rise+burst)*min(1,t*60)*max(0,min(1,(LENGTH-t)/.32))
    samples.append(value)
peak=max(abs(x) for x in samples)
with wave.open(str(Path(__file__).parent/'public/intro-sfx.wav'),'wb') as w:
    w.setnchannels(2);w.setsampwidth(2);w.setframerate(SR)
    data=bytearray()
    for i,x in enumerate(samples):
        delayed=samples[max(0,i-int(SR*.013))]
        l=(x*.94+delayed*.06)/peak*.77
        r=(x*.87+delayed*.13)/peak*.77
        data.extend(struct.pack('<hh',int(l*32767),int(r*32767)))
    w.writeframes(data)
print('Original intro sound created: 3.34s, 48kHz stereo WAV')
