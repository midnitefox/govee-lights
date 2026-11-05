using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RGB=System.Drawing.Color;

namespace GoveeLights {
    internal static class AiOptions {
        private const string Path=@"Software\Govee Lights\AI";
        private static bool Get(string key){using(var k=Registry.CurrentUser.OpenSubKey(Path))return k!=null && Object.Equals(k.GetValue(key),1);}
        private static void Set(string key,bool value){using(var k=Registry.CurrentUser.CreateSubKey(Path))k.SetValue(key,value?1:0);}
        internal static bool Enabled{get{return Get("Enabled");}set{Set("Enabled",value);}}
        internal static bool WakeOff{get{return Get("WakeOff");}set{Set("WakeOff",value);}}
        internal static HashSet<string> Targets {
            get{using(var k=Registry.CurrentUser.OpenSubKey(Path)){var list=k==null?null:k.GetValue("Lights") as string[];return new HashSet<string>(list??new string[0]);}}
            set{using(var k=Registry.CurrentUser.CreateSubKey(Path))k.SetValue("Lights",value.ToArray(),RegistryValueKind.MultiString);}
        }
        internal static string ServerPath{get{return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"Govee Lights MCP.exe");}}
    }
    internal sealed partial class Controller {
        private bool aiOperation;
        private CancellationTokenSource signalCancel;
        private readonly HashSet<string> signalEvents=new HashSet<string>();
        private readonly Queue<string> signalOrder=new Queue<string>();
        private string lastAiActivity="No AI commands received yet.";
        private void SetupAi() {
            Get<CheckBox>("AiEnabled").Click+=(s,e)=>{
                AiOptions.Enabled=Get<CheckBox>("AiEnabled").IsChecked==true;
                if(!AiOptions.Enabled && signalCancel!=null)signalCancel.Cancel();RenderAiSettings();Controls();
            };
            Get<CheckBox>("AiWake").Click+=(s,e)=>AiOptions.WakeOff=Get<CheckBox>("AiWake").IsChecked==true;
            Click("AiStop",()=>{if(signalCancel!=null)signalCancel.Cancel();});
            Click("AiCopyConfig",()=>{
                Clipboard.SetText(AiWire.Json().Serialize(new {mcpServers=new Dictionary<string,object>{{"govee-lights",new {command=AiOptions.ServerPath,args=new string[0]}}}}));
                Text("Notice","MCP configuration copied. Use it in a local Windows AI app.");
            });
            Click("AiTest",async()=>{try{await HandleAi("notify",new Dictionary<string,object>{{"kind","information"}});}catch(Exception ex){Text("Notice",ex.Message);}});
            RenderAiSettings();
        }
        private void RenderAiSettings() {
            Get<CheckBox>("AiEnabled").IsChecked=AiOptions.Enabled;Get<CheckBox>("AiWake").IsChecked=AiOptions.WakeOff;
            Text("AiStatus",(AiOptions.Enabled?"MCP access enabled for this Windows account. ":"MCP access is off. ")+lastAiActivity);
            var host=Get<StackPanel>("AiLights");host.Children.Clear();var allowed=AiOptions.Targets;
            foreach(var light in library.Items) {
                var check=new CheckBox{Content=light.Name+(String.IsNullOrEmpty(light.Room)?"":" · "+light.Room),
                    Style=(Style)Window.FindResource("Toggle"),IsChecked=allowed.Contains(light.Id),Margin=new Thickness(0,4,0,4)};
                string id=light.Id;check.Click+=(s,e)=>{var ids=AiOptions.Targets;if(check.IsChecked==true)ids.Add(id);else ids.Remove(id);AiOptions.Targets=ids;Controls();};host.Children.Add(check);
            }
            if(library.Items.Count==0)host.Children.Add(Label("Add lights on the Lights page first.",true));
        }
        internal Task<object> DispatchAi(string action,Dictionary<string,object> args) {
            return Window.Dispatcher.InvokeAsync(()=>HandleAi(action,args)).Task.Unwrap();
        }
        private static string AiString(Dictionary<string,object> args,string name,bool required=false) {
            object raw;if(!args.TryGetValue(name,out raw)){if(required)throw new IOException(name+" is required.");return null;}
            string value=raw as string;if(value==null || value.Length>128 || (required && value.Length==0))throw new IOException("Invalid "+name+".");return value;
        }
        private SavedLight[] AiTargets(Dictionary<string,object> args,bool single=false) {
            var allowed=AiOptions.Targets;string[] ids;
            if(single)ids=new[]{AiString(args,"light_id",true)};
            else {
                object input;if(args.TryGetValue("light_ids",out input)) {
                    var list=input as IEnumerable;if(list==null || input is string)throw new IOException("light_ids must be a list.");
                    var temp=new List<string>();foreach(object item in list){if(!(item is string))throw new IOException("Invalid light ID.");temp.Add((string)item);}
                    ids=temp.Distinct().ToArray();
                }else ids=allowed.Where(id=>library.Items.Any(x=>x.Id==id)).ToArray();
            }
            if(ids.Length==0 || ids.Length>32)throw new IOException("Select 1–32 lights for AI access in Settings.");
            if(ids.Any(id=>!allowed.Contains(id)))throw new IOException("One of these lights is not allowed for AI control. Change Settings in the app.");
            return ids.Select(id=>library.Items.FirstOrDefault(x=>x.Id==id)??MissingPresetLight(id)).ToArray();
        }
        private static int AiInteger(Dictionary<string,object> args,string key,int min,int max) {
            object raw;if(!args.TryGetValue(key,out raw) || !(raw is int || raw is long || raw is decimal || raw is double))throw new IOException("Invalid "+key+".");
            double v=Convert.ToDouble(raw);if(Double.IsNaN(v) || Double.IsInfinity(v) || v!=Math.Truncate(v) || v<min || v>max)throw new IOException(key+" must be between "+min+" and "+max+".");return (int)v;
        }
        private async Task<object> HandleAi(string action,Dictionary<string,object> args) {
            if(action=="get_status")return new {enabled=AiOptions.Enabled,notification_active=signalCancel!=null,sync_active=session!=null,
                custom_bar_sets=barStreams.Count,busy=busy||liveUpdating||aiOperation,allowed_lights=AiOptions.Targets.Count,last_activity=lastAiActivity};
            if(!AiOptions.Enabled)throw new IOException("AI access is disabled. Enable it in Govee Lights Settings.");
            await InitializeOnce();
            if(action=="list_lights") {
                var allowed=AiOptions.Targets;
                return library.Items.Where(x=>allowed.Contains(x.Id)).Select(x=>new{id=x.Id,name=x.Name,model=x.Sku,room=x.Room,
                    last_seen_online=lights.Any(d=>d.Id==x.Id),selected_for_sync=x.Included}).ToArray();
            }
            if(action=="list_presets")return presets.Read().Where(p=>p.Lights.All(l=>AiOptions.Targets.Contains(l.Id))).Select(p=>new{id=p.Id,name=p.Name,light_ids=p.Lights.Select(l=>l.Id).ToArray()}).ToArray();
            if(action=="stop_notification"){if(signalCancel!=null)signalCancel.Cancel();return new{stop_requested=true};}
            if(busy||liveUpdating||aiOperation||closing)throw new IOException("The app is busy. Wait for the current operation to finish.");
            switch(action) {
                case "notify":return await AiNotify(args);
                case "set_light":return await AiSet(args);
                case "get_light_state":return await AiRead(args);
                case "apply_preset":return await AiPreset(args);
                case "screen_sync": {
                    string mode=AiString(args,"action",true);
                    if(mode!="start" && mode!="stop")throw new IOException("Choose start or stop.");
                    if(mode=="stop"){
                        if(session!=null && sessionLayouts.Keys.Any(id=>!AiOptions.Targets.Contains(id)))throw new IOException("The active sync group includes lights that are not allowed for AI control.");
                        CancelResume();if(session!=null)session.Cancel();return new{stop_requested=true};
                    }
                    var selectedGroup=library.Items.Where(x=>x.Included).ToArray();
                    if(selectedGroup.Length==0 || selectedGroup.Any(x=>!AiOptions.Targets.Contains(x.Id)))throw new IOException("Every selected screen-sync light must also be allowed for AI control.");
                    if(session!=null)return new{already_running=true};CancelResume();ResumeAiSync();return new{start_requested=true};
                }
                default:throw new IOException("Unknown AI action.");
            }
        }
        private async void ResumeAiSync(){await RunSession(false);}
        private async Task<object> AiRead(Dictionary<string,object> args) {
            var target=AiTargets(args,true)[0];aiOperation=true;Controls();
            try {
                var state=await Task.Run(()=>WithNetwork(lan=>{
                    var light=lan.Discover(target.Id).FirstOrDefault(d=>d.Id==target.Id && d.Sku==target.Sku);
                    if(light==null)throw new IOException("The light is offline.");return lan.Read(light);
                }));
                BarStream stream;var custom=barStreams.TryGetValue(target.Id,out stream)?stream.Snapshot():null;
                return new{light_id=target.Id,power=state.onOff==1,brightness=state.brightness,kelvin=state.colorTemInKelvin,color=state.color,
                    individual_bars=custom==null?null:Enumerable.Range(0,2).Select(i=>new{bar=i+1,r=custom.Colors[i].R,g=custom.Colors[i].G,b=custom.Colors[i].B,intensity=custom.Levels[i]}).ToArray()};
            }finally{FinishAi("Last command: read light state.");}
        }
        private void FinishAi(string activity) {
            aiOperation=false;lastAiActivity=activity;RenderAiSettings();Controls();RefreshTray();if(closing)Window.Close();
        }
        private async Task<object> AiSet(Dictionary<string,object> args) {
            var target=AiTargets(args,true)[0];EnsureAvailable(new[]{target});
            bool hasPower=args.ContainsKey("power"),hasLevel=args.ContainsKey("brightness"),hasRgb=args.ContainsKey("color"),hasWhite=args.ContainsKey("kelvin");
            if(!(hasPower||hasLevel||hasRgb||hasWhite) || (hasRgb && hasWhite))throw new IOException("Supply a setting. Use either color or kelvin.");
            bool power=false;if(hasPower){if(!(args["power"] is bool))throw new IOException("power must be true or false.");power=(bool)args["power"];}
            int level=hasLevel?AiInteger(args,"brightness",1,100):0,kelvin=hasWhite?AiInteger(args,"kelvin",2000,9000):0,rgb=0;
            if(hasRgb){string hex=AiString(args,"color",true);if(hex.Length!=7 || hex[0]!='#' || !Int32.TryParse(hex.Substring(1),System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out rgb))throw new IOException("Use color #RRGGBB.");}
            aiOperation=true;Controls();CancelResume();
            try {
                await ReleaseBars(new[]{target});
                var state=await Task.Run(()=>WithNetwork(lan=>{
                    var d=lan.Discover(target.Id).FirstOrDefault(x=>x.Id==target.Id && x.Sku==target.Sku);if(d==null)throw new IOException("The light is offline.");
                    var original=lan.Read(d);var look=LightLook.Capture(target,original);
                    if(hasLevel && !original.brightness.HasValue || hasRgb && original.color==null || hasWhite && !original.colorTemInKelvin.HasValue)throw new IOException("The light did not report support for this control.");
                    if(hasPower)look.Power=power?1:0;if(hasLevel)look.Brightness=level;
                    if(hasRgb){look.RgbValue=rgb;look.Temperature=null;}if(hasWhite){look.Temperature=kelvin;look.RgbValue=null;}
                    if(look.Power==0 && (hasLevel||hasRgb||hasWhite))throw new IOException("Turn the light on to change its brightness or color.");
                    try{look.Set(lan,d,original);}catch(Exception change){
                        try{LightLook.Capture(target,original).Set(lan,d,lan.Read(d),true);}catch(Exception restore){throw new IOException("Check the light after a partial change: "+restore.Message,change);}
                        throw new IOException("Change failed; previous settings restored. "+change.Message,change);
                    }
                    return lan.Read(d);
                }));
                if(selected!=null && selected.Id==target.Id)ShowState(state);
                return new{light_id=target.Id,power=state.onOff==1,brightness=state.brightness,kelvin=state.colorTemInKelvin,color=state.color};
            }finally{FinishAi("Last command: light controls.");}
        }
        private async Task<object> AiPreset(Dictionary<string,object> args) {
            string id=AiString(args,"preset_id",true);var preset=presets.Read().FirstOrDefault(p=>p.Id==id);
            if(preset==null)throw new IOException("Preset not found.");
            if(preset.Lights.Any(l=>!AiOptions.Targets.Contains(l.Id)))throw new IOException("The preset includes a light that is not allowed for AI control.");
            var targets=PresetTargets(preset);EnsureAvailable(targets);aiOperation=true;Controls();CancelResume();
            try{await ReleaseBars(targets);await Task.Run(()=>WithNetwork(lan=>{new LightingBatch(lan,targets).Apply(null,preset);return true;}));return new{applied=preset.Name};}
            finally{FinishAi("Last command: apply preset.");}
        }
        private async Task<object> AiNotify(Dictionary<string,object> args) {
            string kind=AiString(args,"kind",true);LightNotifications.ColorFor(kind);
            var targets=AiTargets(args);string eventId=AiString(args,"event_id");
            if(!String.IsNullOrEmpty(eventId) && signalEvents.Contains(eventId))return new{duplicate=true};
            bool resume=session!=null && !testing && targets.Any(x=>sessionLayouts.ContainsKey(x.Id));
            if(resume && sessionLayouts.Keys.Any(id=>!AiOptions.Targets.Contains(id)))throw new IOException("Allow AI access to every light in the active sync group before temporarily pausing it for a notification.");
            if(testing)throw new IOException("Wait for the placement test to finish.");
            var custom=barStreams.Values.Where(s=>targets.Any(t=>t.Id==s.Saved.Id)).Select(s=>new KeyValuePair<SavedLight,BarLook>(s.Saved,s.Snapshot())).ToArray();
            aiOperation=true;signalCancel=new CancellationTokenSource();Controls();RefreshTray();CancelResume();
            bool restored=false;object result=null;Exception failure=null;
            try {
                if(resume){session.Cancel();while(session!=null)await Task.Delay(25);}
                await ReleaseBars(targets);
                if(signalCancel.IsCancellationRequested){restored=true;result=new{canceled=true};}
                else {
                    Text("Notice","AI notification: "+kind+". Previous lighting will be restored.");
                    int count=await Task.Run(()=>WithNetwork(lan=>LightNotifications.Run(lan,targets,kind,AiOptions.WakeOff,signalCancel.Token)));
                    restored=true;
                    if(!String.IsNullOrEmpty(eventId)){signalEvents.Add(eventId);signalOrder.Enqueue(eventId);while(signalOrder.Count>256)signalEvents.Remove(signalOrder.Dequeue());}
                    Text("Notice",count>0?"Notification ended. Previous lighting restored.":"Notification skipped because the selected lights are off.");
                    result=new {signaled_lights=count,restored=true,sync_resume_requested=resume&&!closing&&!quitRequested};
                }
            }catch(NotificationFailure ex){restored=ex.Restored;failure=ex;}
            catch(Exception ex){failure=ex;}
            finally{signalCancel.Dispose();signalCancel=null;}
            try{if(restored && !closing && !quitRequested)await ResumeBars(custom);}
            catch(Exception ex){failure=new IOException("The notification ended, but individual bar control could not resume. "+ex.Message,ex);}
            finally {
                FinishAi(restored?"Last notification: "+kind+"; lighting restored.":"Notification interrupted. Check the light status.");
                if(resume && restored && !closing && !quitRequested)ResumeAiSync();
            }
            if(failure!=null)throw failure;return result;
        }
    }
}
