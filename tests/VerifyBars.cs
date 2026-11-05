using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Threading;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Threading;
using GoveeLights;

internal static class VerifyBars {
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Need(bool ok,string m){if(!ok)throw new Exception(m);}
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethod(name,Hidden).Invoke(o,args);}
    static void Until(Func<bool> done){var s=System.Diagnostics.Stopwatch.StartNew();while(!done() && s.ElapsedMilliseconds<6000){Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(()=>{}));Thread.Sleep(2);}Need(done(),"Bar UI timeout");}
    static void Await(Task t){Until(()=>t.IsCompleted);t.GetAwaiter().GetResult();}
    internal sealed class Network : IGroupNetwork {
        internal readonly Device Light=new Device{Id="12340001",Sku="H6056",Address=IPAddress.Parse("192.0.2.10")};
        internal DeviceData State=new DeviceData{onOff=1,brightness=37,colorTemInKelvin=0,color=new Rgb{r=12,g=45,b=96}};
        internal volatile string Last;internal int Enables,Disables,Writes;internal bool Fail;
        public List<Device> Discover(string id=null){return new List<Device>{Light};}
        public DeviceData Read(Device d,Func<DeviceData,bool> match=null){return State;}
        public DeviceData Change(Device d,Setting s){Interlocked.Increment(ref Writes);return State;}
        public void SendSetting(Device d,Setting s){Interlocked.Increment(ref Writes);}
        public void StreamPacket(Device d,string p){if(p==ScreenSync.Disable){Interlocked.Increment(ref Disables);return;}if(p==ScreenSync.Enable){Interlocked.Increment(ref Enables);return;}if(Fail)throw new Exception("Simulated stream failure");Last=p;}
        public bool PollGroupHealth(Device[] lights,long now){return State.onOff==1;}
        public double StreamIntervalMs{get{return 1000.0/30;}}
    }
    internal static void Check() {
        var n=new Network();var saved=SavedLight.From(n.Light);
        var bars=new BarStream(saved,6,()=>n);bars.Update(0,Color.Red,50);bars.Start();bars.Ready.Wait();
        var bytes=Convert.FromBase64String(n.Last);
        Need(bytes[5]==12 && bytes[6]==128 && bytes[7]==0,"Bar 1 color/intensity not encoded");
        Need(bytes[24]==12 && bytes[25]==45 && bytes[26]==96,"Unselected bar did not retain reported RGB");
        Need(n.Writes==0 && n.State.brightness==37,"Bar control changed device brightness");
        bars.Update(1,Color.Blue,100);Thread.Sleep(90);bytes=Convert.FromBase64String(n.Last);
        Need(bytes[6]==128 && bytes[24]==0 && bytes[26]==255,"Updating bar 2 overwrote bar 1");
        bars.Cancel();bars.Completion.Wait();Need(n.Disables==2,"Custom stream did not release on stop");
        n.Fail=true;var broken=new BarStream(saved,6,()=>n);broken.Start();
        try{broken.Completion.Wait();throw new Exception("Failure not reported");}catch(AggregateException){}
        Need(n.Disables==4 && broken.Ready.IsFaulted,"Failure did not restore/release stream");
        n.Fail=false;var ui=new Controller(()=>n.Discover(),d=>n.Read(d),()=>n);
        Await((Task)Call(ui,"Startup"));Await((Task)Call(ui,"Select",n.Light));Call(ui,"Navigate",true);
        typeof(Controller).GetField("manualBar",Hidden).SetValue(ui,1);Call(ui,"RenderBarControls");Call(ui,"ShowBarValues");Call(ui,"Controls");
        Need(!((Button)ui.Window.FindName("On")).IsEnabled && !((Border)ui.Window.FindName("WarmthCard")).IsEnabled,"Individual selection exposed set-wide power/white controls");
        ((Slider)ui.Window.FindName("Level")).Value=28;
        var streams=(Dictionary<string,BarStream>)typeof(Controller).GetField("barStreams",Hidden).GetValue(ui);
        Until(()=>streams.ContainsKey(saved.Id) && streams[saved.Id].Ready.IsCompleted);
        Until(()=>!(bool)typeof(Controller).GetField("liveUpdating",Hidden).GetValue(ui));
        Need(streams[saved.Id].Snapshot().Levels[0]==28 && streams[saved.Id].Snapshot().Levels[1]==100,"Per-bar slider targeted wrong member");
        ((Slider)ui.Window.FindName("Comfort")).Value=61;SyncOptions.Load();Need(SyncOptions.Comfort==61,"Comfort did not persist");
        SyncOptions.Comfort=35;SyncOptions.Save();
        var toggle=(CheckBox)ui.Window.FindName("ModeRefresh");toggle.IsChecked=true;toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Need(new Mapping(n.Light).PeriodicModeRefresh && streams[saved.Id].ModeRefresh,"Firmware compatibility did not persist or update active custom set");
        toggle.IsChecked=false;toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));Need(!new Mapping(n.Light).PeriodicModeRefresh,"Firmware compatibility could not be disabled");
        var original=new LightLibrary().Items.Single(x=>x.Id==saved.Id);
        Call(ui,"OpenLights");var list=(StackPanel)ui.Window.FindName("SavedList");
        var card=list.Children.OfType<Border>().Select(x=>(StackPanel)x.Child).First(x=>Object.Equals(x.Tag,saved.Id));
        var remove=card.Children.OfType<WrapPanel>().SelectMany(x=>x.Children.OfType<Button>()).First(x=>Object.Equals(x.Content,"Remove"));
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Need((bool)typeof(Controller).GetField("busy",Hidden).GetValue(ui),"Removal did not acquire ownership while restoring bars");
        Until(()=>streams.Count==0 && !(bool)typeof(Controller).GetField("busy",Hidden).GetValue(ui));
        Need(!new LightLibrary().Items.Any(x=>x.Id==saved.Id),"Set not removed after release");
        new LightLibrary().Save(original);
        ui.Quit();Need(n.Disables==6,"Removal left a custom stream active");
        var quitting=new Controller(()=>n.Discover(),d=>n.Read(d),()=>n);
        Await((Task)Call(quitting,"ResumeBars",new[]{new KeyValuePair<SavedLight,BarLook>(original,new BarLook())}));
        var remaining=(Dictionary<string,BarStream>)typeof(Controller).GetField("barStreams",Hidden).GetValue(quitting);
        quitting.Quit();Until(()=>remaining.Count==0);Need(n.Disables==8,"App exit left a custom set streaming");
        Console.WriteLine("Passed individual bar packets, untouched member, brightness preservation, release and failure cleanup.");
    }
}
