using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using RGB=System.Drawing.Color;

namespace GoveeLights {
    // One verified target set per gesture burst. Intermediate UDP values are unconfirmed;
    // the final values are repeated and read back. No discovery or sleeps on the UI thread.
    internal sealed class ManualChannel : IDisposable {
        private readonly IGroupNetwork network;
        private readonly List<Device> devices=new List<Device>();
        private readonly Dictionary<string,DeviceData> states=new Dictionary<string,DeviceData>();
        internal ManualChannel(IGroupNetwork lan,SavedLight[] targets,Device known=null,DeviceData state=null) {
            network=lan;
            try {
                var online=known!=null && targets.Length==1 ? new List<Device>{known} : network.Discover(targets.Length==1?targets[0].Id:null);
                foreach(var target in targets) {
                    var d=online.FirstOrDefault(x=>x.Id==target.Id && x.Sku==target.Sku);
                    if(d==null)throw new IOException(target.Name+" is offline. Reconnect it and choose Refresh.");
                    devices.Add(d);states[d.Id]=known==d && state!=null?state:network.Read(d);
                }
                if(devices.Count==0)throw new IOException("Choose at least one saved light.");
            }catch {Dispose();throw;}
        }
        internal void Send(Setting value) {
            if(devices.Any(d=>!value.Supported(states[d.Id])))
                throw new IOException("One of these lights did not report support for that control.");
            foreach(var d in devices)network.SendSetting(d,value);
        }
        internal Dictionary<string,DeviceData> Confirm(Setting[] values) {
            // Repeat the final values once to tolerate a dropped UDP datagram.
            foreach(var value in values)Send(value);
            var result=new Dictionary<string,DeviceData>();
            foreach(var d in devices)result[d.Id]=network.Read(d,s=>values.All(v=>v.Matches(s)));
            return result;
        }
        public void Dispose(){var handle=network as IDisposable;if(handle!=null)handle.Dispose();}
    }

    internal sealed partial class Controller {
        private ColorWheel wheel;
        private bool showingManualValues,liveUpdating;
        private DateTime manualObservedAt;
        private readonly Dictionary<string,Setting> pendingManual=new Dictionary<string,Setting>();
        private void SetupLiveControls() {
            wheel=new ColorWheel();Get<Border>("WheelHost").Child=wheel;
            wheel.ColorChanged+=(s,e)=>{
                ShowChosenColor(wheel.SelectedColor);
                if(IsBarSet && manualBar>0)QueueBar(wheel.SelectedColor,null);
                else QueueLive(Setting.Tone(wheel.SelectedColor,0));
            };
            Get<Slider>("Level").ValueChanged+=(s,e)=>{
                Text("Percent",((int)e.NewValue)+"%");
                if(IsBarSet && manualBar>0)QueueBar(null,(int)e.NewValue);
                else QueueLive(Setting.Brightness((int)e.NewValue));
            };
            Get<Slider>("Warmth").ValueChanged+=(s,e)=>{
                Text("Kelvin",((int)e.NewValue)+" K");QueueLive(Setting.Tone(RGB.Black,(int)e.NewValue));
            };
            ShowManualValues(new DeviceData{brightness=50,color=new Rgb{r=132,g=191,b=255},colorTemInKelvin=4000});
        }
        private void ShowChosenColor(RGB color) {
            Get<TextBox>("ColorInput").Text=String.Format("#{0:X2}{1:X2}{2:X2}",color.R,color.G,color.B);
            Get<Border>("ColorSample").Background=Brush(color);
        }
        private void ShowManualValues(DeviceData state) {
            showingManualValues=true;
            try {
                Get<Slider>("Level").Value=Math.Max(1,state.brightness??1);
                Text("Percent",((int)Get<Slider>("Level").Value)+"%");
                if(state.colorTemInKelvin>=2000 && state.colorTemInKelvin<=9000)Get<Slider>("Warmth").Value=state.colorTemInKelvin.Value;
                Text("Kelvin",((int)Get<Slider>("Warmth").Value)+" K");
                if(state.color!=null && state.colorTemInKelvin==0) {
                    var rgb=RGB.FromArgb(state.color.r,state.color.g,state.color.b);wheel.SelectedColor=rgb;ShowChosenColor(rgb);
                }
            }finally{showingManualValues=false;}
        }
        private async void QueueLive(Setting value) {
            if(showingManualValues || !isManual || busy || closing || !Get<StackPanel>("ManualPage").IsEnabled)return;
            var group=ManualTargets();
            if(group.Length==0 || OverlapsSync(group))return;
            // Two bounded slots: brightness and tone. RGB and white temperature replace each other.
            pendingManual[value.Command]=value;
            if(liveUpdating)return;
            liveUpdating=true;Controls();RefreshTray();Text("Notice","Updating lights as you adjust…");
            if(pendingResume!=null && group.Any(x=>x.Included))CancelResume();
            // The selected device was just resolved and read on entering this page. Reuse it briefly.
            Device known=group.Length==1 && selected!=null && selected.Id==group[0].Id && selectedState!=null &&
                (DateTime.UtcNow-manualObservedAt).TotalSeconds<30 ? selected:null;
            DeviceData knownState=known==null?null:selectedState;
            try {
                await ReleaseBars(group);
                using(var channel=await Task.Run(()=>new ManualChannel(openNetwork(),group,known,knownState))) {
                    var sent=new Dictionary<string,Setting>();var clock=Stopwatch.StartNew();long last=-80;
                    while(true) {
                        while(pendingManual.Count>0) {
                            int wait=80-(int)(clock.ElapsedMilliseconds-last);if(wait>0)await Task.Delay(wait);
                            // Read AFTER the pacing delay, so an older drag position is never queued.
                            string key=pendingManual.Keys.First();var latest=pendingManual[key];pendingManual.Remove(key);
                            await Task.Run(()=>channel.Send(latest));sent[key]=latest;last=clock.ElapsedMilliseconds;
                        }
                        await Task.Delay(180);
                        if(pendingManual.Count>0)continue;
                        var final=sent.Values.ToArray();
                        var states=await Task.Run(()=>channel.Confirm(final));
                        if(pendingManual.Count>0)continue;
                        if(roomTarget==null && selected!=null && states.ContainsKey(selected.Id))ShowState(states[selected.Id]);
                        else RenderManualTarget();
                        if(session==null)SetConnection("Connected","#78D8AD");
                        Text("Notice",group.Length==1?"Setting confirmed by the light.":"Settings confirmed by all "+group.Length+" lights.");
                        break;
                    }
                }
            }catch(Exception ex){
                manualObservedAt=DateTime.MinValue;
                Text("Notice","Could not confirm the adjustment. Some lights may have changed. "+ex.Message);
                if(session==null)SetConnection("Check connection","#E7AF70");
            }finally {
                pendingManual.Clear();liveUpdating=false;Controls();RefreshTray();if(closing)Window.Close();
            }
        }
    }
}
