using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Threading;
using GoveeLights;

internal static class VerifyAI {
    const BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Instance;
    static void Need(bool b,string msg){if(!b)throw new Exception(msg);}
    static void Until(Func<bool> done) {
        var watch=Stopwatch.StartNew();while(!done() && watch.ElapsedMilliseconds<16000){Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(()=>{}));Thread.Sleep(3);}
        Need(done(),"AI operation timeout");
    }
    static T Await<T>(Task<T> t){Until(()=>t.IsCompleted);return t.GetAwaiter().GetResult();}
    static Dictionary<string,object> Args(params object[] a){var d=new Dictionary<string,object>();for(int i=0;i<a.Length;i+=2)d[(string)a[i]]=a[i+1];return d;}
    static Dictionary<string,object> Data(object o){return AiWire.Json().Deserialize<Dictionary<string,object>>(AiWire.Json().Serialize(o));}
    sealed class Network : IGroupNetwork {
        internal readonly List<Device> Devices=new List<Device>();
        internal readonly Dictionary<string,DeviceData> States=new Dictionary<string,DeviceData>();
        internal int Writes,FailAt=-1;
        readonly JavaScriptSerializer json=new JavaScriptSerializer();
        internal Network(){int i=20;foreach(var light in new LightLibrary().Items){Devices.Add(new Device{Id=light.Id,Sku=light.Sku,Address=IPAddress.Parse("192.0.2."+i++)});States[light.Id]=new DeviceData{onOff=1,brightness=63,colorTemInKelvin=0,color=new Rgb{r=50,g=100,b=150}};}}
        public List<Device> Discover(string id=null){return Devices.Where(x=>id==null||x.Id==id).ToList();}
        public DeviceData Read(Device d,Func<DeviceData,bool> match=null){var s=States[d.Id];if(match!=null && !match(s))throw new IOException("Not confirmed");return json.Deserialize<DeviceData>(json.Serialize(s));}
        public void SendSetting(Device d,Setting s){Change(d,s);}
        public DeviceData Change(Device d,Setting s){
            Writes++;var state=States[d.Id];var p=json.Deserialize<Dictionary<string,object>>(json.Serialize(s.Payload));
            if(s.Command=="turn")state.onOff=(int)p["value"];else if(s.Command=="brightness")state.brightness=(int)p["value"];
            else{state.colorTemInKelvin=(int)p["colorTemInKelvin"];state.color=json.Deserialize<Rgb>(json.Serialize(p["color"]));}
            if(Writes==FailAt)throw new IOException("Simulated mid-signal disconnect");return Read(d);
        }
        public double StreamIntervalMs{get{return 33;}}
        public void StreamPacket(Device d,string p){}
        public bool PollGroupHealth(Device[] d,long n){return true;}
    }
    internal static void Check() {
        var n=new Network();var library=new LightLibrary();var targets=library.Items.ToArray();var a=n.Devices[0];
        foreach(string kind in LightNotifications.Kinds) {
            Need(LightNotifications.Run(n,targets,kind,false,CancellationToken.None,80)==targets.Length,"Signal target count");
            Need(n.States.Values.All(s=>s.brightness==63 && s.color.r==50 && s.color.g==100 && s.color.b==150 && s.colorTemInKelvin==0 && s.onOff==1),"Signal did not restore RGB/brightness/power");
        }
        n.States[a.Id].onOff=0;int writes=n.Writes;
        Need(LightNotifications.Run(n,new[]{targets[0]},"attention",false,CancellationToken.None,80)==0 && n.Writes==writes,"Off light was woken without opt-in");
        LightNotifications.Run(n,new[]{targets[0]},"information",true,CancellationToken.None,80);
        Need(n.States[a.Id].onOff==0 && n.States[a.Id].brightness==63 && n.States[a.Id].color.b==150,"Wake signal did not restore off state");n.States[a.Id].onOff=1;
        n.FailAt=n.Writes+2;bool failed=false;
        try{LightNotifications.Run(n,targets,"complete",false,CancellationToken.None,80);}catch(NotificationFailure ex){failed=ex.Restored;}
        Need(failed && n.States.Values.All(s=>s.brightness==63 && s.color.b==150),"Partial signal cleanup");
        using(var cancel=new CancellationTokenSource()){cancel.Cancel();LightNotifications.Run(n,targets,"error",false,cancel.Token,80);}
        AiOptions.Enabled=true;AiOptions.Targets=new HashSet<string>(targets.Select(x=>x.Id));
        int runs=0;var ui=new Controller(()=>n.Discover(),d=>n.Read(d),()=>n,(group,test,cancel,show)=>{
            Interlocked.Increment(ref runs);show(SceneFrame.Solid(Pair.Test));cancel.WaitHandle.WaitOne(15000);return group.ToDictionary(g=>g.Light.Id,g=>n.Read(g.Light));
        });
        var result=Await(ui.DispatchAi("list_lights",Args()));Need(((ICollection)Data(new{list=result})["list"]).Count==targets.Length,"AI allowlist list");
        AiOptions.Targets=new HashSet<string>{a.Id};writes=n.Writes;failed=false;
        try{Await(ui.DispatchAi("set_light",Args("light_id",n.Devices[1].Id,"brightness",20)));}catch(IOException){failed=true;}
        Need(failed && n.Writes==writes,"Disallowed target was written");
        AiOptions.Enabled=false;failed=false;try{Await(ui.DispatchAi("notify",Args("kind","complete")));}catch(IOException){failed=true;}Need(failed,"Disabled AI access accepted action");
        Need(!(bool)Data(Await(ui.DispatchAi("get_status",Args())))["enabled"],"Disabled status wrong");AiOptions.Enabled=true;
        Await(ui.DispatchAi("set_light",Args("light_id",a.Id,"brightness",41)));Need(n.States[a.Id].brightness==41,"AI manual brightness");
        AiOptions.Targets=new HashSet<string>(targets.Select(x=>x.Id));
        Await(ui.DispatchAi("screen_sync",Args("action","start")));Until(()=>runs==1);
        var signal=ui.DispatchAi("notify",Args("kind","complete","event_id","test-turn-cancel"));
        Until(()=>Data(Await(ui.DispatchAi("get_status",Args())))["notification_active"].Equals(true));
        Await(ui.DispatchAi("stop_notification",Args()));Await(signal);Until(()=>runs==2);
        Need(n.States[a.Id].brightness==41 && Data(Await(ui.DispatchAi("get_status",Args())))["sync_active"].Equals(true),"Canceled signal did not resume sync");
        Await(ui.DispatchAi("notify",Args("kind","complete","event_id","test-turn-full")));Until(()=>runs==3);
        writes=n.Writes;Need(Data(Await(ui.DispatchAi("notify",Args("kind","complete","event_id","test-turn-full"))))["duplicate"].Equals(true) && n.Writes==writes,"Duplicate signal replayed");
        Await(ui.DispatchAi("screen_sync",Args("action","stop")));Until(()=>!Data(Await(ui.DispatchAi("get_status",Args())))["sync_active"].Equals(true));
        // Custom sets survive read-only requests and temporary notifications; whole-set writes release them.
        var barRecord=library.Items.First(x=>x.Sku=="H6056");
        var look=new BarLook();look.Colors[0]=System.Drawing.Color.Red;look.Colors[1]=System.Drawing.Color.Blue;look.Levels[0]=43;
        var resume=(Task)typeof(Controller).GetMethod("ResumeBars",Hidden).Invoke(ui,new object[]{new[]{new KeyValuePair<SavedLight,BarLook>(barRecord,look)}});
        Until(()=>resume.IsCompleted);resume.GetAwaiter().GetResult();
        var custom=(Dictionary<string,BarStream>)typeof(Controller).GetField("barStreams",Hidden).GetValue(ui);
        var running=custom[barRecord.Id];
        Await(ui.DispatchAi("get_light_state",Args("light_id",barRecord.Id)));
        Need(custom[barRecord.Id]==running,"Read-only status released an individual set");
        Await(ui.DispatchAi("notify",Args("kind","information","light_ids",new[]{barRecord.Id},"event_id","custom-set-notification")));
        Need(custom.ContainsKey(barRecord.Id) && custom[barRecord.Id]!=running && custom[barRecord.Id].Snapshot().Levels[0]==43 &&
            custom[barRecord.Id].Snapshot().Colors[1].ToArgb()==System.Drawing.Color.Blue.ToArgb(),"Notification lost individual bar settings");
        var startReply=Data(Await(ui.DispatchAi("screen_sync",Args("action","start"))));
        Need((bool)startReply["start_requested"],"Sync handoff incorrectly reported that start was not requested");
        Until(()=>Data(Await(ui.DispatchAi("get_status",Args())))["sync_active"].Equals(true));
        Await(ui.DispatchAi("screen_sync",Args("action","stop")));Until(()=>!Data(Await(ui.DispatchAi("get_status",Args())))["sync_active"].Equals(true));
        resume=(Task)typeof(Controller).GetMethod("ResumeBars",Hidden).Invoke(ui,new object[]{new[]{new KeyValuePair<SavedLight,BarLook>(barRecord,look)}});
        Until(()=>resume.IsCompleted);resume.GetAwaiter().GetResult();
        Await(ui.DispatchAi("set_light",Args("light_id",barRecord.Id,"brightness",42)));
        Need(!custom.ContainsKey(barRecord.Id) && n.States[barRecord.Id].brightness==42,"Whole-set change fought custom streaming");
        // Real Windows IPC with a unique isolated pipe, including concurrent status/stop requests.
        string pipe="GoveeLights-QA-"+Guid.NewGuid().ToString("N");
        using(var bridge=new AiPipeServer(ui.DispatchAi,pipe)) {
            Need(Data(Await(AiWire.Request("get_status",Args(),pipe)))["enabled"].Equals(true),"Named pipe status");
            signal=AiWire.Request("notify",Args("kind","attention"),pipe);
            Until(()=>Data(Await(AiWire.Request("get_status",Args(),pipe)))["notification_active"].Equals(true));
            Await(AiWire.Request("stop_notification",Args(),pipe));Await(signal);
        }
        var init=Data(Await(McpProgram.Handle(Args("id",1,"method","initialize"),(x,y)=>Task.FromResult<object>(null))));
        Need(((Dictionary<string,object>)init["result"])["protocolVersion"].Equals("2025-11-25"),"MCP initialize");
        var list=Data(Await(McpProgram.Handle(Args("id",2,"method","tools/list"),(x,y)=>Task.FromResult<object>(null))));
        Need(((ICollection)((Dictionary<string,object>)list["result"])["tools"]).Count==9,"MCP tool count");
        var call=Data(Await(McpProgram.Handle(Args("id",3,"method","tools/call","params",Args("name","govee_set_light","arguments",Args("light_id",a.Id,"brightness",101))),ui.DispatchAi)));
        Need(((Dictionary<string,object>)call["result"])["isError"].Equals(true),"Invalid MCP argument accepted");
        ui.Quit();AiOptions.Enabled=false;AiOptions.Targets=new HashSet<string>();
        Console.WriteLine("Passed AI opt-in/allowlist, signals, off-light policy, restoration, partial failure, cancellation, sync resume, deduplication, concurrent Windows pipe access, MCP handshake/tools/errors.");
    }
}
