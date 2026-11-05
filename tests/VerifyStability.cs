using System;
using System.Drawing;
using System.Collections.Generic;
using System.Net;
using GoveeLights;

internal static class VerifyStability {
    private sealed class Network : IGroupNetwork {
        internal readonly List<string> Packets=new List<string>();
        public List<Device> Discover(string id=null){return new List<Device>();}
        public DeviceData Read(Device d,Func<DeviceData,bool> expected=null){return null;}
        public DeviceData Change(Device d,Setting s){throw new Exception("Unexpected setting write");}
        public void SendSetting(Device d,Setting s){throw new Exception("Unexpected setting write");}
        public void StreamPacket(Device d,string packet){Packets.Add(packet);}
        public bool PollGroupHealth(Device[] d,long now){return true;}
        public double StreamIntervalMs {get{return 1000.0/30;}}
    }
    static void Need(bool ok,string message){if(!ok)throw new Exception(message);}
    internal static byte[] BorderFixture() {
        var p=new byte[64*36*4];Color[] colors={Color.Red,Color.Orange,Color.Yellow,Color.Lime,Color.Cyan,Color.Blue};
        for(int y=0;y<36;y++)for(int x=0;x<64;x++) {
            Color c=y<2||y>=34?Color.FromArgb(7,7,7):colors[Math.Min(5,(y-2)*6/32)];int i=4*(y*64+x);
            p[i]=c.B;p[i+1]=c.G;p[i+2]=c.R;
        }
        return p;
    }
    internal static void Run() {
        double now=0;var engine=new SceneColors(()=>now);var p=BorderFixture();var a=engine.Analyze(p);
        p[64*4+2]=8;p[65*4+2]=8;var b=engine.Analyze(p);
        Need(Math.Abs(a.Six[5].Left.G-b.Six[5].Left.G)<3,"Tiny border noise moved the sampling geometry.");
        now=349;Need(engine.Analyze(p).Six[5].Left==b.Six[5].Left,"Crop adopted too early");
        now=350;Need(engine.Analyze(p).Six[5].Left!=b.Six[5].Left && !engine.HasPendingCrop,"Stable crop was never adopted");
        var recovery=new CaptureContinuity();recovery.Failed(100);
        Need(recovery.Hold(599,true) && !recovery.Fallback,"Recovery discarded a valid frame too soon");
        recovery.Recovered();Need(!recovery.Hold(900,true) && !recovery.Fallback,"Successful recovery used fallback");
        recovery.Failed(1000);Need(!recovery.Hold(1500,true) && recovery.Fallback,"Persistent failure did not pin fallback");
        var reopened=new CaptureContinuity();reopened.Failed(0);
        for(int t=100;t<500;t+=100){Need(reopened.ReuseOnTimeout(t),"Capture timeout lost the recovery frame early");reopened.Failed(t);}
        Need(!reopened.ReuseOnTimeout(500) && reopened.Fallback,"Reopen/timeouts reset the original recovery deadline");
        var valid=new CaptureContinuity();valid.Failed(0);Need(valid.ReuseOnTimeout(400),"Failed handle did not retain last frame");
        valid.Recovered();Need(valid.ReuseOnTimeout(5000) && !valid.Fallback,"Fresh desktop frame did not recover capture");
        var n=new Network();var light=new Device{Id="12340001",Sku="H6056",Address=IPAddress.Loopback};
        var target=new SyncTarget(light,"Set","Split bars",()=>false,()=>6);var scene=SceneFrame.Solid(new Pair(Color.Red,Color.Blue));
        target.Send(n,scene,0);string saved=n.Packets[0];target.RefreshStream(n);
        Need(n.Packets.Count==3 && n.Packets[1]==ScreenSync.Enable && n.Packets[2]==saved,"Stream refresh did not immediately replay static colors");
        Need(target.Send(n,scene,1) && n.Packets[3]==saved,"Fresh frame was delayed after stream refresh");
        int before=n.Packets.Count;target.MaintainMode(n);Need(n.Packets.Count==before,"H6056 was periodically re-enabled by default");
        target.PeriodicModeRefresh=()=>true;target.MaintainMode(n);
        Need(n.Packets.Count==before+2 && n.Packets[before]==ScreenSync.Enable && n.Packets[before+1]==saved,"Compatibility refresh lost immediate replay");
        foreach(int palette in new[]{0,1,2})foreach(int strength in new[]{0,35,100}) {
            Need(ComfortColors.Apply(Color.Black,strength,palette).ToArgb()==Color.Black.ToArgb(),"Black lifted by Comfort");
            for(int v=0;v<256;v+=7) {
                var gray=ComfortColors.Apply(Color.FromArgb(v,v,v),strength,palette);
                Need(gray.R==gray.G && gray.G==gray.B && gray.R<=v,"Comfort tinted or brightened neutral gray");
            }
        }
        Need(ComfortColors.Apply(Color.Red,0).ToArgb()==Color.Red.ToArgb(),"Accurate mode changed color");
        Need(ComfortColors.Apply(Color.White,100).R<ComfortColors.Apply(Color.White,35).R,"Comfort strength did not compress bright peaks");
        foreach(int palette in new[]{1,2})foreach(Color color in new[]{Color.Red,Color.Lime,Color.Blue,Color.Cyan,Color.Magenta}) {
            var lab=SceneColors.Perceptual(ComfortColors.Apply(color,35,palette));double hue=Math.Atan2(lab.B,lab.A)*180/Math.PI;if(hue<0)hue+=360;
            Need(palette==1?(hue>=-1 && hue<=101):(hue>=179 && hue<=291),"Color escaped selected continuous hue range");
        }
        var treatment=new ComfortColors();var red=SceneFrame.Solid(new Pair(Color.Red,Color.Red));var blue=SceneFrame.Solid(new Pair(Color.Blue,Color.Blue));
        treatment.Process(red,35);Need(treatment.Process(blue,35).Whole.Left==ComfortColors.Apply(Color.Blue,35),"Scene cut was temporally averaged");
        var treated=treatment.Process(blue,35);Need(Object.ReferenceEquals(treated,treatment.Process(blue,35)),"Static scene was needlessly recomputed");
        Console.WriteLine("Passed crop-noise stability and adoption, capture recovery, immediate stream refresh, neutral/black Comfort, and immediate scene cuts.");
    }
}
