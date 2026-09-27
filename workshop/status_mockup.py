from PIL import Image, ImageDraw, ImageFont
BG=(8,18,24); PANEL=(14,32,42); GRID=(32,78,96); FAINT=(22,52,64); CYAN=(70,205,235); TEXT=(215,240,250); DIM=(120,160,175)
ROUTE=(255,190,60); WARN=(255,110,80); BAT=(100,220,140); H2=(150,200,255); JUMP=(190,130,255); URAN=(160,255,60)
ORE={"Cobalt":(90,140,255),"Iron":(225,140,95),"Nickel":(130,215,130)}
W,H=1024,512; u=H/256
img=Image.new("RGB",(W,H),BG); d=ImageDraw.Draw(img)
F="/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
def font(s): return ImageFont.truetype(F,int(22*s*u))
def R(x,y,w,h,c): d.rectangle([x*u,y*u,(x+w)*u,(y+h)*u],fill=c)
def B(x,y,w,h,c): d.rectangle([x*u,y*u,(x+w)*u,(y+h)*u],outline=c,width=max(1,int(u)))
def T(t,x,y,s,c,a="l"):
    f=font(s); tw=d.textlength(t,font=f)
    X=x*u-(tw if a=="r" else tw/2 if a=="c" else 0); d.text((X,y*u),t,font=f,fill=c)
def M(t,s): return d.textlength(t,font=font(s))/u
def card(x,y,w,h,t): R(x,y,w,h,PANEL); B(x,y,w,h,GRID); T(t,x+10,y+8,0.55,DIM)
cw=W/u/2-9
# cargo
x,y,w,h=6,6,cw,244; card(x,y,w,h,"CARGO")
T("19%",x+w-10,y+2,0.95,CYAN,"r")
B(x+10,y+40,w-20,22,GRID); fill=(w-22)*0.19; R(x+11,y+41,fill*0.93,20,ORE["Cobalt"]); R(x+11+fill*0.93,y+41,fill*0.07,20,ORE["Iron"])
T("1228.5 t",x+10,y+68,0.6,TEXT); T("403.2 / 2122 m³",x+w-10,y+68,0.6,DIM,"r")
ry=y+98
for n,m in [("Cobalt","1142.5 t"),("Iron","86.0 t")]:
    cx,cy=(x+18)*u,(ry+11)*u; r=5.5*u; d.polygon([(cx,cy-r),(cx+r,cy),(cx,cy+r),(cx-r,cy)],fill=ORE[n])
    T(n,x+30,ry,0.62,TEXT); T(m,x+w-10,ry,0.62,TEXT,"r"); ry+=26
# power
x=W/u/2+3; card(x,y,w,h,"POWER & FUEL"); ry=y+34
def line(lbl,det,val,frac,col):
    global ry
    T(lbl,x+10,ry,0.62,TEXT); T(det,x+10+M(lbl,0.62)+10,ry+3,0.5,DIM); T(val,x+w-10,ry,0.62,col,"r")
    if frac>=0: R(x+10,ry+25,w-20,6,FAINT); R(x+10,ry+25,(w-20)*frac,6,col)
    ry+=38
line("Battery","3h 12m","86%",0.86,BAT); line("Uranium",">99h","100.0 kg",-1,URAN); line("Hydrogen","idle","84%",0.84,H2); line("Jump","ready","100%",1,JUMP)
dy=y+h-58; d.line([(x+8)*u,(dy-4)*u,(x+w-8)*u,(dy-4)*u],fill=GRID,width=int(u))
T("DELTA-V",x+10,dy+4,0.5,DIM); T("1206 m/s *",x+w-10,dy,0.8,ROUTE,"r"); T("2.0 trips at 300 m/s",x+10,dy+30,0.52,TEXT); T("* estimate",x+w-10,dy+30,0.5,DIM,"r")
import os
img.save(os.path.join(os.path.dirname(os.path.abspath(__file__)), "images", "status.png"))
