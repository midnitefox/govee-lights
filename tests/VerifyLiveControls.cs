using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GoveeLights;

internal static class VerifyLiveControls {
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Need(bool b,string message){if(!b)throw new Exception(message);}
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethod(name,Hidden).Invoke(o,args);}
    static T Field<T>(object o,string name){return (T)o.GetType().GetField(name,Hidden).GetValue(o);}
    static void Until(Func<bool> ready) {
        var watch=Stopwatch.StartNew();while(!ready() && watch.ElapsedMilliseconds<8000){
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(()=>{}));Thread.Sleep(2);
        }Need(ready(),"Live-control wait timed out");
    }
    static void Pump(Task t){Until(()=>t.IsCompleted);t.GetAwaiter().GetResult();}
    sealed class Network : IGroupNetwork {
        internal readonly List<Device> Devices=new List<Device>();
        internal readonly Dictionary<string,DeviceData> States=new Dictionary<string,DeviceData>();
        internal int Writes,Scans,Reads;internal bool DropFirst=true,FailSend;
        internal string LastDiscoveryTarget;
        internal readonly List<long> Times=new List<long>();
        internal readonly Stopwatch Clock=Stopwatch.StartNew();
        readonly JavaScriptSerializer json=new JavaScriptSerializer();
        internal Network() {
            int ip=10;foreach(var s in new LightLibrary().Items) {
                var d=new Device{Id=s.Id,Sku=s.Sku,Address=IPAddress.Parse("192.0.2."+ip++)};Devices.Add(d);
                States[d.Id]=new DeviceData{onOff=1,brightness=65,colorTemInKelvin=4000,color=new Rgb{r=12,g=45,b=96}};
            }
        }
        public List<Device> Discover(string id=null){LastDiscoveryTarget=id;Interlocked.Increment(ref Scans);return Devices.Where(d=>id==null||d.Id==id).ToList();}
        public DeviceData Read(Device d,Func<DeviceData,bool> match=null) {
            Interlocked.Increment(ref Reads);var s=States[d.Id];if(match!=null && !match(s))throw new IOException("Expected value not reported");
            return json.Deserialize<DeviceData>(json.Serialize(s));
        }
        public void SendSetting(Device d,Setting value) {
            Interlocked.Increment(ref Writes);Times.Add(Clock.ElapsedMilliseconds);
            if(FailSend)throw new IOException("Simulated disconnect");if(DropFirst){DropFirst=false;return;}
            var data=json.Deserialize<Dictionary<string,object>>(json.Serialize(value.Payload));var s=States[d.Id];
            if(value.Command=="brightness")s.brightness=(int)data["value"];
            else if(value.Command=="colorwc"){s.colorTemInKelvin=(int)data["colorTemInKelvin"];s.color=json.Deserialize<Rgb>(json.Serialize(data["color"]));}
        }
        public DeviceData Change(Device d,Setting s){SendSetting(d,s);return Read(d);}
        public void StreamPacket(Device d,string p){}
        public double StreamIntervalMs{get{return 33;}}
        public bool PollGroupHealth(Device[] d,long n){return true;}
    }
    static IEnumerable<Button> Buttons(DependencyObject d) {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(d);i++) {
            var child=VisualTreeHelper.GetChild(d,i);if(child is Button)yield return (Button)child;
            foreach(var b in Buttons(child))yield return b;
        }
    }
    internal static void Check() {
        Need(ColorWheel.Hsv(0,1).R==255 && ColorWheel.Hsv(120,1).G==255 && ColorWheel.Hsv(240,1).B==255 &&
            ColorWheel.Hsv(42,0).ToArgb()==System.Drawing.Color.White.ToArgb(),"Wheel hue/saturation math");
        var n=new Network();var a=n.Devices.First(d=>d.Sku=="H6056");
        var ui=new Controller(()=>n.Discover(),d=>n.Read(d),()=>n);var win=ui.Window;
        Pump((Task)Call(ui,"Startup"));Pump((Task)Call(ui,"Select",a));Call(ui,"Navigate",true);
        Need(n.Writes==0,"Programmatic status updates sent a command");
        var level=(Slider)win.FindName("Level");int scans=n.Scans;
        level.Value=20;for(int i=21;i<=87;i++)level.Value=i;
        Need(level.IsEnabled && !((Button)win.FindName("NavLights")).IsEnabled,"Live drag locked slider or allowed target change");
        Until(()=>!Field<bool>(ui,"liveUpdating"));
        Need(n.States[a.Id].brightness==87 && n.Writes<=3 && n.Scans==scans,"Drag was not coalesced or target was rediscovered");
        Need(n.Reads>0 && ((TextBlock)win.FindName("Notice")).Text.Contains("confirmed"),"Final value not confirmed after dropped datagram");
        int before=n.Writes;level.Value=22;Until(()=>n.Writes>before);
        level.Value=56;Until(()=>n.Writes>before+1);level.Value=37;
        Until(()=>!Field<bool>(ui,"liveUpdating"));
        Need(n.States[a.Id].brightness==37 && n.Writes<=before+4,"Sustained drag final value");
        // Read-back updates must never feed a command back into the live event handlers.
        before=n.Writes;Call(ui,"ShowState",new DeviceData{onOff=1,brightness=44,colorTemInKelvin=7700,color=new Rgb{r=1,g=2,b=3}});
        Need(n.Writes==before && !Field<bool>(ui,"liveUpdating"),"Readback feedback loop");
        var warmth=(Slider)win.FindName("Warmth");warmth.Value=2700;
        Until(()=>!Field<bool>(ui,"liveUpdating"));
        Need(n.States[a.Id].colorTemInKelvin==2700 && n.States[a.Id].brightness==37,"Warmth changed brightness");
        // Use the control's accessible value API, which follows the same input event as pointer/keyboard input.
        var wheel=(ColorWheel)((Border)win.FindName("WheelHost")).Child;
        var peer=System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(wheel);
        var value=(System.Windows.Automation.Provider.IValueProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Value);
        value.SetValue("#00C080");Until(()=>!Field<bool>(ui,"liveUpdating"));
        Need(n.States[a.Id].color.g==192 && n.States[a.Id].color.b==128 && n.States[a.Id].colorTemInKelvin==0 && n.States[a.Id].brightness==37,"Wheel application/brightness independence");
        // Zone storage accepts all four modes and keeps physical six-segment mapping.
        foreach(int count in new[]{1,2,3,6}) {
            var mapping=new Mapping(a){Zones=count,Physical=6};mapping.Save();Need(new Mapping(a).Zones==count && new Mapping(a).Physical==6,"Zone persistence");
        }
        // A room preflights all targets before any intermediate writes.
        var records=new LightLibrary().Items.ToArray();before=n.Writes;
        var removed=n.Devices.Last();n.Devices.Remove(removed);
        try{using(var channel=new ManualChannel(n,records))channel.Send(Setting.Brightness(15));throw new Exception("Offline room accepted");}catch(IOException){}
        Need(n.Writes==before,"Room sent before offline preflight");n.Devices.Add(removed);
        n.States[removed.Id].brightness=null;
        try{using(var channel=new ManualChannel(n,records))channel.Send(Setting.Brightness(15));throw new Exception("Unsupported room control accepted");}catch(IOException){}
        Need(n.Writes==before,"Room sent before capability preflight");n.States[removed.Id].brightness=65;
        using(var channel=new ManualChannel(n,records)){channel.Send(Setting.Brightness(48));channel.Confirm(new[]{Setting.Brightness(48)});}
        Need(n.States.Values.All(s=>s.brightness==48),"Room live brightness");
        // Recover cleanly after a send failure and permit another attempt.
        n.FailSend=true;level.Value=29;Until(()=>!Field<bool>(ui,"liveUpdating"));
        Need(((TextBlock)win.FindName("Notice")).Text.Contains("Could not confirm") && ((Button)win.FindName("NavLights")).IsEnabled,"Failure did not unlock controls");
        n.FailSend=false;level.Value=51;Until(()=>!Field<bool>(ui,"liveUpdating"));Need(n.States[a.Id].brightness==51,"Recovery after send failure");
        Need(n.LastDiscoveryTarget==a.Id,"Expired live control cache must resolve one target without a full-network scan");
        // All app action and navigation buttons share the same dimensions, including generated cards.
        foreach(string page in new[]{"Lights","Manual","Sync","Presets","Settings"}) {
            if(page=="Lights")Call(ui,"OpenLights");else if(page=="Manual" || page=="Sync")Call(ui,"Navigate",page=="Manual");else Call(ui,"OpenExtra",page);
            var root=(FrameworkElement)win.Content;root.Measure(new Size(832,740));root.Arrange(new Rect(0,0,832,740));root.UpdateLayout();
            foreach(var b in Buttons(root))Need(b.Width==(b.Name=="Refresh"?36:144) && b.Height==36,"Inconsistent button size: "+b.Content);
        }
        Call(ui,"Navigate",true);level.Value=53;ui.Quit();
        Need(Field<bool>(ui,"closing"),"Quit did not wait for final adjustment");Until(()=>!Field<bool>(ui,"liveUpdating"));
        Need(n.States[a.Id].brightness==53,"Quit lost the final slider value");
        Console.WriteLine("Passed live drag coalescing, dropped UDP final resend, warm/RGB brightness independence, room preflight, failure recovery, button dimensions, zone persistence, and close flushing.");
    }
}
