using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Threading;
using GoveeLights;

internal static class VerifyScenes {
    static byte[] Raster(Func<int,int,Color> at) {
        var p=new byte[64*36*4];for(int y=0;y<36;y++)for(int x=0;x<64;x++){
            var c=at(x,y);int k=4*(y*64+x);p[k]=c.B;p[k+1]=c.G;p[k+2]=c.R;
        }return p;
    }
    static Color Legacy(byte[] p) {
        double r=0,g=0,b=0,sum=0;for(int y=0;y<36;y++)for(int x=0;x<32;x++) {
            int i=4*(64*y+x);double w=.12+.88*Math.Min(1,Math.Max(p[i],Math.Max(p[i+1],p[i+2]))/40.0);
            r+=p[i+2]*p[i+2]*w;g+=p[i+1]*p[i+1]*w;b+=p[i]*p[i]*w;sum+=w;
        }return Color.FromArgb((int)Math.Round(Math.Sqrt(r/sum)),(int)Math.Round(Math.Sqrt(g/sum)),(int)Math.Round(Math.Sqrt(b/sum)));
    }
    static double Lin(int v){double x=v/255.0;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}
    static int Encode(double x){return (int)Math.Round(255*(x<=.0031308?12.92*x:1.055*Math.Pow(x,1/2.4)-.055));}
    static Color Mean(byte[] p) {
        double r=0,g=0,b=0;for(int y=0;y<36;y++)for(int x=0;x<32;x++){int i=(y*64+x)*4;r+=Lin(p[i+2]);g+=Lin(p[i+1]);b+=Lin(p[i]);}
        return Color.FromArgb(Encode(r/1152),Encode(g/1152),Encode(b/1152));
    }
    static string Hex(Color c){return String.Format("#{0:X2}{1:X2}{2:X2}",c.R,c.G,c.B);}
    static void Need(bool ok,string msg){if(!ok)throw new Exception(msg);}
    class Fake : IGroupNetwork {
        public readonly Device A=new Device{Id="12340001",Sku="H6056",Address=IPAddress.Parse("127.0.0.2")};
        public readonly Device B=new Device{Id="12340002",Sku="H619A",Address=IPAddress.Parse("127.0.0.3")};
        public readonly List<string> Logs=new List<string>();
        public readonly Dictionary<string,byte[]> Packets=new Dictionary<string,byte[]>();
        public bool Missing,FailArm;
        public List<Device> Discover(string id=null){return (Missing?new[]{A}:new[]{A,B}).Where(x=>id==null||x.Id==id).ToList();}
        public DeviceData Read(Device l,Func<DeviceData,bool> exp=null){return new DeviceData{onOff=1,brightness=50,colorTemInKelvin=4500};}
        public DeviceData Change(Device l,Setting s){return Read(l);}
        public void SendSetting(Device l,Setting s){}
        public bool PollGroupHealth(Device[] group,long clock){return true;}
        public double StreamIntervalMs{get{return 33;}}
        public void StreamPacket(Device l,string frame){
            Logs.Add(l.Id+":"+frame);
            if(FailArm && l==B && frame==ScreenSync.Enable)throw new Exception("test disconnect");
            if(frame!=ScreenSync.Enable && frame!=ScreenSync.Disable)Packets[l.Id]=Convert.FromBase64String(frame);
        }
    }
    internal static SceneFrame Pattern() {
        Color[] a={Color.Red,Color.Coral,Color.Gold,Color.Lime,Color.Cyan,Color.Blue};
        return new SceneColors().Analyze(Raster((x,y)=>x<32?a[y/6]:a[5-y/6]));
    }
    static byte[] Run(Fake f,int n,bool swap,bool flip) {
        var frame=Pattern();int captures=0;
        using(var cancel=new CancellationTokenSource(2000)) {
            var a=new SyncTarget(f.A,"Bars","Split bars",()=>swap,()=>n){FlipLeft=()=>flip};
            var b=new SyncTarget(f.B,"Lamp","Right half",()=>false,()=>6);
            GroupPlayback.Execute(f,new[]{a,b},false,cancel.Token,()=>{captures++;return frame;},p=>cancel.Cancel());
        }
        Need(captures==2,"group must use one shared capture plus startup warm-up");
        Need(f.Packets.Count==2 && f.Packets[f.B.Id][4]==1 && f.Packets[f.B.Id][6]==frame.Whole.Right.R,"generic role");
        Need(f.Logs.Contains(f.A.Id+":"+ScreenSync.Disable) && f.Logs.Contains(f.B.Id+":"+ScreenSync.Disable),"group did not release");
        return f.Packets[f.A.Id];
    }
    [STAThread] public static int Main() {
        try{
            var engine=new SceneColors();Color orange=Color.FromArgb(226,74,22),teal=Color.FromArgb(12,191,202);
            var cases=new Dictionary<string,byte[]> {
                {"flat",Raster((x,y)=>orange)},{"letterbox",Raster((x,y)=>y<6||y>=30?Color.Black:orange)},
                {"75% warm, 25% cyan",Raster((x,y)=>x%32<24?orange:teal)},
                {"balanced",Raster((x,y)=>x%32<16?Color.Red:Color.Cyan)},
                {"small highlight",Raster((x,y)=>x%32==0 && y%4==0?Color.White:Color.FromArgb(8,8,8))},
                {"dark",Raster((x,y)=>Color.FromArgb(11,14,18))}
            };
            foreach(var c in cases) {
                Color color=new SceneColors().Analyze(c.Value).Whole.Left;
                Console.WriteLine(c.Key+"   RMS="+Hex(Legacy(c.Value))+"  linear="+Hex(Mean(c.Value))+"  selected="+Hex(color));
                if(c.Key=="flat"||c.Key=="letterbox")Need(color.ToArgb()==orange.ToArgb(),c.Key);
                if(c.Key.StartsWith("75%"))Need(color.G<Mean(c.Value).G && color.R>Mean(c.Value).R,c.Key);
                if(c.Key=="balanced")Need(Math.Abs(color.R-color.G)<2 && Math.Abs(color.G-color.B)<2,c.Key);
                if(c.Key=="small highlight")Need(color.R<Legacy(c.Value).R,c.Key);
                if(c.Key=="dark")Need(color.ToArgb()==Color.FromArgb(11,14,18).ToArgb(),c.Key);
            }
            Need(SceneColors.HoldNoise(Color.FromArgb(90,95,100),Color.FromArgb(91,95,100)).R==90,"subtle noise not held");
            Need(SceneColors.HoldNoise(Color.Red,Color.Blue).B==255 && SceneColors.HoldNoise(Color.Blue,Color.Black).B==0,"cut delayed");
            Color held=Color.FromArgb(90,95,100),raw=held;int rawUpdates=0,visibleUpdates=0;
            for(int i=0;i<120;i++) {
                Color c=Color.FromArgb(90+i%2,95,100);if(c!=raw)rawUpdates++;raw=c;
                Color next=SceneColors.HoldNoise(held,c);if(next!=held)visibleUpdates++;held=next;
            }
            Need(rawUpdates==119 && visibleUpdates==0,"noise compression");
            for(int v=92;v<=190;v++) held=SceneColors.HoldNoise(held,Color.FromArgb(v,95,100));
            Need(held.R>=187,"a slow ramp got stuck");
            Console.WriteLine("120-frame one-code jitter: "+rawUpdates+" raw RGB changes; "+visibleUpdates+" perceptual color changes. Slow ramp advanced to R="+held.R+".");
            VerifyStability.Run();
            var pattern=Pattern();Need(pattern.Six[0].Left.ToArgb()!=pattern.Six[1].Left.ToArgb(),"spatial regions collapsed");
            Need(pattern.One[0].Left==pattern.Whole.Left && pattern.Two[0].Left!=pattern.Two[1].Left,"one/two sampling collapsed");
            foreach(int n in new[]{1,2,3,6})foreach(bool swap in new[]{false,true})foreach(bool flip in new[]{false,true}) {
                var bytes=Run(new Fake(),n,swap,flip);Need(bytes[5]==12 && bytes[4]==0,"physical zones");
                for(int i=0;i<6;i++){int k=6+3*((swap?6:0)+i);int zone=i*n/6;
                    Color c=pattern.Bands(n)[flip?n-1-zone:zone].Left;
                    Need(bytes[k]==c.R && bytes[k+1]==c.G && bytes[k+2]==c.B,"physical zone color mismatch");}
            }
            var missing=new Fake{Missing=true};try{Run(missing,6,false,false);throw new Exception("missing device accepted");}catch(System.IO.IOException){}
            Need(missing.Logs.Count==0,"offline preflight wrote devices");
            var broken=new Fake{FailArm=true};try{Run(broken,6,false,false);throw new Exception("arm failure missed");}catch(System.IO.IOException){}
            Need(broken.Logs.Contains(broken.A.Id+":"+ScreenSync.Disable) && broken.Logs.Contains(broken.B.Id+":"+ScreenSync.Disable),"partial group cleanup");
            var frames=cases.Values.ToArray();var sw=Stopwatch.StartNew();
            for(int i=0;i<350;i++)engine.Analyze(frames[i%frames.Length]);
            Console.WriteLine("Color analysis including both layouts: "+(sw.Elapsed.TotalMilliseconds/350).ToString("F3")+" ms/frame");
            var random=new Random(6056);
            var texture=Raster((x,y)=>Color.FromArgb(random.Next(256),random.Next(256),random.Next(256)));
            sw.Restart();for(int i=0;i<200;i++)engine.Analyze(texture);
            Console.WriteLine("High-variation texture, all zones: "+(sw.Elapsed.TotalMilliseconds/200).ToString("F3")+" ms/frame");
            Console.WriteLine("Group, offline preflight, rollback, noise, cut, 1/2/3/6 zones, swap and flips passed.");
            return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
