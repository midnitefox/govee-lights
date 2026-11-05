using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Web.Script.Serialization;
using GoveeLights;

internal static class VerifyHome {
    private static void Need(bool ok,string text){if(!ok)throw new Exception(text);}
    private static Device D(string id,string sku,int i){return new Device{Id=id,Sku=sku,Address=IPAddress.Parse("127.0.0."+i)};}
    private sealed class Network : IGroupNetwork {
        internal List<Device> Devices=new List<Device>{D("12340001","H6056",2),D("12340002","H619A",3),D("12340003","H6121",4)};
        internal Dictionary<string,DeviceData> States=new Dictionary<string,DeviceData>();
        internal int Writes;internal string FailOnce;
        private readonly JavaScriptSerializer json=new JavaScriptSerializer();
        public Network(){foreach(var d in Devices)States[d.Id]=new DeviceData{onOff=1,brightness=65,color=new Rgb{r=12,g=45,b=96},colorTemInKelvin=0};}
        public List<Device> Discover(string id=null){return Devices.Where(x=>id==null||x.Id==id).ToList();}
        public DeviceData Read(Device d,Func<DeviceData,bool> expected=null){return json.Deserialize<DeviceData>(json.Serialize(States[d.Id]));}
        public DeviceData Change(Device d,Setting s){
            Writes++;var v=States[d.Id];var data=json.Deserialize<Dictionary<string,object>>(json.Serialize(s.Payload));
            if(s.Command=="turn")v.onOff=(int)data["value"];else if(s.Command=="brightness")v.brightness=(int)data["value"];
            else{v.colorTemInKelvin=(int)data["colorTemInKelvin"];v.color=json.Deserialize<Rgb>(json.Serialize(data["color"]));}
            if(FailOnce==d.Id){FailOnce=null;throw new Exception("Simulated lost reply");}
            Need(s.Matches(v),"unexpected write");return Read(d);
        }
        public double StreamIntervalMs{get{return 33;}}
        public void SendSetting(Device d,Setting s){Change(d,s);}
        public bool PollGroupHealth(Device[] a,long b){return true;}
        public void StreamPacket(Device d,string p){}
    }
    private static void Until(Func<bool> done) {
        var watch=Stopwatch.StartNew();while(!done() && watch.ElapsedMilliseconds<15000){
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(()=>{}));Thread.Sleep(4);}
        Need(done(),"sign-in dispatcher timeout");
    }
    internal static void Check() {
        var n=new Network();var lib=new LightLibrary();var group=lib.Items.Where(x=>x.Included).ToArray();
        Need(group.Length==2 && group.Any(x=>x.Room=="Studio"),"Expected persisted room and group");
        n.States[group[1].Id].colorTemInKelvin=4500;
        var store=new PresetStore();var p=new LightingPreset{Name="Evening",Lights=new LightingBatch(n,group).Capture()};store.Save(p);
        var saved=new PresetStore().Read().Single();Need(saved.Name==p.Name && saved.Lights.Count==2,"Preset round trip");
        foreach(var r in group){n.States[r.Id].brightness=19;n.States[r.Id].onOff=0;}
        new LightingBatch(n,group).Apply(null,saved);
        Need(group.All(r=>n.States[r.Id].brightness==65 && n.States[r.Id].onOff==1),"Preset application");
        new LightingBatch(n,group).Apply(Setting.Brightness(37),null);Need(group.All(r=>n.States[r.Id].brightness==37),"Room brightness");
        n.FailOnce=group[1].Id;bool failed=false;
        try{new LightingBatch(n,group).Apply(Setting.Brightness(70),null);}catch(System.IO.IOException){failed=true;}
        Need(failed && group.All(r=>n.States[r.Id].brightness==37),"Partial group rollback");
        int writes=n.Writes;var absent=n.Devices.First(d=>d.Id==group[1].Id);n.Devices.Remove(absent);
        try{new LightingBatch(n,group).Apply(null,saved);throw new Exception("Accepted offline target");}catch(System.IO.IOException){}
        Need(writes==n.Writes,"Wrote before preflight");n.Devices.Add(absent);
        // The sign-in path uses the real window dispatcher, tray preference, registration,
        // retry loop and selected-device builder. Only the network and the screen source are simulated.
        int scans=0,started=0;string[] sent=null;SignInOptions.SetEnabled(true);SignInOptions.AutoSync=true;
        var ui=new Controller(()=>Interlocked.Increment(ref scans)==1?new List<Device>():n.Discover(),d=>n.Read(d),()=>n,
            (targets,test,cancel,show)=>{
                sent=targets.Select(t=>t.Light.Id).OrderBy(x=>x).ToArray();
                show(SceneFrame.Solid(Pair.Test));Interlocked.Increment(ref started);
                cancel.WaitHandle.WaitOne(10000);
                return targets.ToDictionary(x=>x.Light.Id,x=>n.Read(x.Light));
            });
        Task sign=ui.StartAtSignIn();
        Until(()=>started==1);
        Need(!ui.Window.IsVisible && !ui.Window.ShowInTaskbar && scans>=2 && sent.SequenceEqual(group.Select(x=>x.Id).OrderBy(x=>x)),
            "Hidden sign-in did not resume exact selected devices after network recovery");
        var access=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var other=n.Devices.Single(d=>d.Id=="12340003");
        var select=(Task)typeof(Controller).GetMethod("Select",access).Invoke(ui,new object[]{other});
        Until(()=>select.IsCompleted);select.GetAwaiter().GetResult();
        typeof(Controller).GetMethod("Navigate",access).Invoke(ui,new object[]{true});
        Need(((System.Windows.Controls.StackPanel)ui.Window.FindName("ManualPage")).IsEnabled,"Unrelated light controls locked during sync");
        var manual=(Task)typeof(Controller).GetMethod("Apply",access).Invoke(ui,new object[]{Setting.Brightness(42)});
        Until(()=>manual.IsCompleted);manual.GetAwaiter().GetResult();
        Need(n.States[other.Id].brightness==42 && started==1,"Concurrent independent control");
        var stop=(Task)typeof(Controller).GetMethod("ToggleSession",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(ui,new object[]{false});
        Until(()=>sign.IsCompleted && stop.IsCompleted);sign.GetAwaiter().GetResult();stop.GetAwaiter().GetResult();
        var privateFlags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(Controller).GetMethod("OpenExtra",privateFlags).Invoke(ui,new object[]{"Presets"});
        var pick=(HashSet<string>)typeof(Controller).GetField("presetSelection",privateFlags).GetValue(ui);
        foreach(var r in group)pick.Add(r.Id);
        ((System.Windows.Controls.TextBox)ui.Window.FindName("PresetName")).Text="Reading";
        var capture=(Task)typeof(Controller).GetMethod("SaveLook",privateFlags).Invoke(ui,new object[]{null});
        Until(()=>capture.IsCompleted);capture.GetAwaiter().GetResult();
        var reading=store.Read().Single(x=>x.Name=="Reading");
        foreach(var r in group)n.States[r.Id].brightness=17;
        var apply=(Task)typeof(Controller).GetMethod("ApplyLook",privateFlags).Invoke(ui,new object[]{reading});
        Until(()=>apply.IsCompleted);apply.GetAwaiter().GetResult();
        Need(group.All(r=>n.States[r.Id].brightness==37),"Preset page capture/apply");
        store.Remove(reading.Id);
        // Quit explicitly, independent of the close-to-tray preference left by the UI fixture.
        typeof(Controller).GetMethod("Quit",privateFlags).Invoke(ui,null);
        SignInOptions.AutoSync=false;int unexpected=0;
        var idle=new Controller(()=>n.Discover(),d=>n.Read(d),()=>n,(t,b,c,f)=>{unexpected++;return new Dictionary<string,DeviceData>();});
        Task idleStart=idle.StartAtSignIn();Until(()=>idleStart.IsCompleted);idleStart.GetAwaiter().GetResult();
        Need(!idle.Window.IsVisible && unexpected==0,"Tray-only sign-in started sync");typeof(Controller).GetMethod("Quit",privateFlags).Invoke(idle,null);
        store.Remove(saved.Id);Need(store.Read().Count==0,"Preset deletion");
        Console.WriteLine("Passed room controls, presets, rollback, offline preflight, hidden sign-in, delayed network, exact group resume, independent control during sync, stop, and tray-only sign-in.");
    }
}
