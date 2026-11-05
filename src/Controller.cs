using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using RGB = System.Drawing.Color;
using Screen = System.Windows.Forms.Screen;

namespace GoveeLights {
    internal sealed class Mapping {
        private readonly string path;
        public volatile bool Swap;
        public volatile int Zones = 6;
        public volatile int Physical=6;
        public volatile bool FlipLeft,FlipRight;
        public volatile bool PeriodicModeRefresh;
        internal static string PathFor(string id) {return @"Software\Govee Lights\Layout\" + String.Concat(id.Where(Char.IsLetterOrDigit));}
        public Mapping(Device device) {
            PeriodicModeRefresh=device.Sku!="H6056";
            path = PathFor(device.Id);
            using (var k = Registry.CurrentUser.OpenSubKey(path)) {
                if (k == null) return;
                Swap = Object.Equals(k.GetValue("Swap"), 1);
                int saved;Zones=Int32.TryParse(Convert.ToString(k.GetValue("Zones")),out saved) && new[]{1,2,3,6}.Contains(saved)?saved:6;
                Physical=Object.Equals(k.GetValue("Physical",Zones),3)?3:6;
                Zones=Math.Min(Zones,Physical);
                FlipLeft=Object.Equals(k.GetValue("FlipLeft"),1);FlipRight=Object.Equals(k.GetValue("FlipRight"),1);
                PeriodicModeRefresh=Object.Equals(k.GetValue("PeriodicModeRefresh",PeriodicModeRefresh?1:0),1);
            }
        }
        public void Save() {
            using (var k = Registry.CurrentUser.CreateSubKey(path)) {
                k.SetValue("Swap", Swap ? 1 : 0); k.SetValue("Zones", Zones);
                k.SetValue("Physical",Physical);k.SetValue("FlipLeft",FlipLeft?1:0);k.SetValue("FlipRight",FlipRight?1:0);
                k.SetValue("PeriodicModeRefresh",PeriodicModeRefresh?1:0);
            }
        }
    }

    internal sealed partial class Controller {
        public readonly Window Window;
        private List<Device> lights = new List<Device>();
        private Device selected;
        private Mapping mapping;
        private CancellationTokenSource session;
        private bool busy, testing, closing, readingMapping, isManual;
        private int previewQueued;
        private readonly System.Diagnostics.Stopwatch previewClock = System.Diagnostics.Stopwatch.StartNew();
        private long nextPreview;
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        private readonly Func<List<Device>> discoverNetwork;
        private readonly Func<Device,DeviceData> readDevice;
        private readonly Func<IGroupNetwork> openNetwork;
        private readonly Func<SyncTarget[],bool,CancellationToken,Action<SceneFrame>,Dictionary<string,DeviceData>> playGroup;
        public Controller() : this(()=>{using(var lan=new Lan())return lan.Discover();},
            light=>{using(var lan=new Lan())return lan.Read(light);}) {}
        internal Controller(Func<List<Device>> discover,Func<Device,DeviceData> read,Func<IGroupNetwork> network=null,
            Func<SyncTarget[],bool,CancellationToken,Action<SceneFrame>,Dictionary<string,DeviceData>> playback=null) {
            discoverNetwork=discover;readDevice=read;
            openNetwork=network??(()=>new Lan());playGroup=playback??((targets,test,token,show)=>ScreenSync.RunGroup(targets,test,token,show));
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LightsWindow.xaml"))
                Window = (Window)XamlReader.Load(stream);
            Window.SourceInitialized += (s,e) => {
                var hwnd = new WindowInteropHelper(Window).Handle;
                int yes=1, round=2, caption=0x221C19;
                DwmSetWindowAttribute(hwnd,20,ref yes,4);
                DwmSetWindowAttribute(hwnd,33,ref round,4);
                DwmSetWindowAttribute(hwnd,35,ref caption,4);
            };
            Window.Loaded += async (s,e) => await InitializeOnce();
            Window.Loaded += (s,e) => SystemEvents.DisplaySettingsChanged += DisplayChanged;
            Window.Closed += (s,e) => SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            Window.Closing += OnClosing;
            Click("NavSync", () => Navigate(false)); Click("NavManual", () => Navigate(true));
            Click("Refresh", async () => await Discover());
            Click("Start", async () => await ToggleSession(false)); Click("Test", async () => await ToggleSession(true));
            Click("DeviceMenu", OpenLights); Click("NavLights", OpenLights); Click("FindLights", async () => await ScanLights());
            Click("PreviewDevice",async()=>await NextPreview());
            SetupTray();SetupSignIn();SetupPresets();SetupAi();
            Get<CheckBox>("Swap").Click += (s,e) => {
                if (mapping == null || readingMapping) return;
                mapping.Swap = Get<CheckBox>("Swap").IsChecked == true; SaveMapping();
            };
            Click("ZoneCount", () => {
                if (mapping == null) return;
                var menu=new ContextMenu();
                foreach(int count in new[]{1,2,3,6}.Where(n=>n<=mapping.Physical)) {
                    int n=count;var item=new MenuItem{Header=n+(n==1?" zone per bar":" zones per bar"),IsCheckable=true,IsChecked=mapping.Zones==n};
                    item.Click+=(s,e)=>{mapping.Zones=n;ShowMapping();SaveMapping();};menu.Items.Add(item);
                }
                menu.PlacementTarget=Get<Button>("ZoneCount");menu.IsOpen=true;
            });
            Click("Hardware",()=>{if(mapping==null)return;mapping.Physical=mapping.Physical==6?3:6;mapping.Zones=Math.Min(mapping.Zones,mapping.Physical);ShowMapping();SaveMapping();});
            Get<CheckBox>("FlipLeft").Click+=(s,e)=>{if(mapping!=null){mapping.FlipLeft=Get<CheckBox>("FlipLeft").IsChecked==true;SaveMapping();}};
            Get<CheckBox>("FlipRight").Click+=(s,e)=>{if(mapping!=null){mapping.FlipRight=Get<CheckBox>("FlipRight").IsChecked==true;SaveMapping();}};
            Get<CheckBox>("ModeRefresh").Click+=(s,e)=>{if(mapping!=null && !readingMapping){mapping.PeriodicModeRefresh=Get<CheckBox>("ModeRefresh").IsChecked==true;
                BarStream stream;if(selected!=null && barStreams.TryGetValue(selected.Id,out stream))stream.ModeRefresh=mapping.PeriodicModeRefresh;SaveMapping();}};
            Click("On", async () => await Apply(Setting.Power(true)));
            Click("Off", async () => await Apply(Setting.Power(false)));
            SetupLiveControls();SetupBarControls();
            Click("SetColor", async () => {
                string value = Get<TextBox>("ColorInput").Text.Trim().TrimStart('#');
                int rgb;
                if (value.Length != 6 || !Int32.TryParse(value, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,out rgb)) { Text("Notice","Enter a six-digit hex color, such as #84BFFF."); return; }
                var color=RGB.FromArgb((rgb>>16)&255,(rgb>>8)&255,rgb&255);
                if(IsBarSet && manualBar>0){QueueBar(color,null);return;}
                await Apply(Setting.Tone(color,0));
            });
            OpenLights(); ResetPreview(); UpdateDisplayInfo(); Controls();
        }
        private T Get<T>(string name) where T:class { return (T)Window.FindName(name); }
        private void Click(string name, Action action) { Get<Button>(name).Click += (s,e) => action(); }
        private void Text(string name,string value) { Get<TextBlock>(name).Text=value; }
        private static SolidColorBrush Brush(string hex) {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b;
        }
        private static SolidColorBrush Brush(RGB c) {
            var b = new SolidColorBrush(Color.FromRgb(c.R,c.G,c.B)); b.Freeze(); return b;
        }
        private void Navigate(bool manual) {
            Get<ScrollViewer>("ContentScroll").ScrollToTop();
            ExtraOff();isManual=manual;
            onLights=false; Get<StackPanel>("LightsPage").Visibility=Visibility.Collapsed; Get<Button>("NavLights").Background=Brushes.Transparent;
            Get<Button>("Refresh").Visibility=Visibility.Visible;
            Get<StackPanel>("SyncPage").Visibility=manual ? Visibility.Collapsed : Visibility.Visible;
            Get<StackPanel>("ManualPage").Visibility=manual ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("NavSync").Background=manual ? Brushes.Transparent : Brush("#303944");
            Get<Button>("NavManual").Background=manual ? Brush("#303944") : Brushes.Transparent;
            Text("Heading",manual ? "Light controls" : "Screen sync");
            if(manual)RenderManualTarget();Controls();
        }
        private void UpdateDisplayInfo() {
            var scr=Screen.PrimaryScreen;
            Text("Monitor",scr == null ? "Unavailable" : scr.DeviceName.Replace(@"\\.\DISPLAY","Display ")+"  ·  "+scr.Bounds.Width+" × "+scr.Bounds.Height);
        }
        private void DisplayChanged(object sender, EventArgs e) {
            if (!Window.Dispatcher.HasShutdownStarted)
                Window.Dispatcher.BeginInvoke(new Action(UpdateDisplayInfo));
        }
        private void Controls() {
            bool active=session!=null, ready=selected!=null;
            bool locked=busy || liveUpdating || aiOperation;
            foreach(string nav in new[]{"NavLights","NavManual","NavSync","NavPresets","NavSettings"})Get<Button>(nav).IsEnabled=!liveUpdating && !aiOperation;
            Get<Button>("Refresh").IsEnabled=!locked;
            Get<Button>("DeviceMenu").IsEnabled=!locked;
            Get<Button>("DeviceMenu").Visibility=onLights || extraPage!=null?Visibility.Collapsed:Visibility.Visible;
            Get<Button>("Start").IsEnabled=!locked && (active ? !testing : library.Items.Any(x=>x.Included && LightModel.Find(x.Sku).Streaming));
            Get<Button>("Test").IsEnabled=ready && selected.Sku=="H6056" && !locked && (!active || testing);
            Get<Button>("Start").Content=active && !testing ? "Stop sync" : "Start sync";
            Get<Button>("Test").Content=active && testing ? "Stop test" : "Test sides";
            Text("SyncState",active ? (testing ? "Testing bar placement" : "Sync is on") : "Sync is off");
            var targets=ManualTargets();
            Get<StackPanel>("ManualPage").IsEnabled=targets.Length>0 && !busy && !aiOperation && !OverlapsSync(targets);
            bool single=roomTarget==null && selectedState!=null;
            Get<Border>("BrightnessCard").IsEnabled=!single || selectedState.brightness.HasValue;
            Get<Border>("ToneCard").IsEnabled=!single || selectedState.color!=null || selectedState.colorTemInKelvin.HasValue;
            wheel.IsEnabled=!single || selectedState.color!=null;
            Get<TextBox>("ColorInput").IsEnabled=!single || selectedState.color!=null;
            Get<Button>("SetColor").IsEnabled=!liveUpdating && (!single || selectedState.color!=null);
            Get<Border>("WarmthCard").IsEnabled=(!single || selectedState.colorTemInKelvin.HasValue) && !(IsBarSet && manualBar>0);
            Get<Button>("On").IsEnabled=Get<Button>("Off").IsEnabled=!liveUpdating && !(IsBarSet && manualBar>0);
            Get<Button>("SavePreset").IsEnabled=!locked && presetSelection.Count>0;
            Get<StackPanel>("PresetMembers").IsEnabled=!locked;Get<StackPanel>("PresetList").IsEnabled=!locked;
            Get<CheckBox>("Swap").IsEnabled=ready;
            Get<Button>("ZoneCount").IsEnabled=ready && mapping!=null;
            Get<Border>("PlacementCard").Visibility=ready && selected.Sku=="H6056" && library.Items.Any(x=>x.Id==selected.Id && x.Placement=="Split bars")?Visibility.Visible:Visibility.Collapsed;
            Get<Button>("FindLights").IsEnabled=!locked;
            Get<StackPanel>("SavedList").IsEnabled=!locked;
            Get<StackPanel>("FoundList").IsEnabled=!locked;
            Get<Button>("PreviewDevice").Visibility=lights.Count(d=>library.Items.Any(x=>x.Id==d.Id) && (!active || sessionLayouts.ContainsKey(d.Id)))>1?Visibility.Visible:Visibility.Collapsed;
            Get<Button>("PreviewDevice").IsEnabled=!locked;
            Get<StackPanel>("AiLights").IsEnabled=!aiOperation;
            Get<Button>("AiTest").IsEnabled=!locked && AiOptions.Enabled && AiOptions.Targets.Count>0;
            Get<Button>("AiStop").IsEnabled=signalCancel!=null;
            RenderBarControls();
        }
        private void ShowMapping() {
            readingMapping=true;
            Get<CheckBox>("Swap").IsChecked=mapping!=null && mapping.Swap;
            Get<Button>("ZoneCount").Content=(mapping==null ? 6 : mapping.Zones)+" zones / bar ▾";
            Get<Button>("ZoneCount").IsEnabled=mapping!=null;
            Get<Button>("Hardware").Content=(mapping==null?6:mapping.Physical)+" segments / bar";
            Get<CheckBox>("FlipLeft").IsChecked=mapping!=null && mapping.FlipLeft;
            Get<CheckBox>("FlipRight").IsChecked=mapping!=null && mapping.FlipRight;
            Get<CheckBox>("ModeRefresh").IsChecked=mapping!=null && mapping.PeriodicModeRefresh;
            UpdateBands(lastPreview);
            readingMapping=false;
        }
        private void SaveMapping() {
            try { mapping.Save(); } catch (Exception e) { Text("Notice","Layout applies for this session. "+e.Message); }
        }
        private void SetConnection(string label,string color) { Text("Connection",label); Get<Ellipse>("Dot").Fill=Brush(color); }
        private void ShowState(DeviceData state) {
            selectedState=state;
            manualObservedAt=DateTime.UtcNow;
            Text("DeviceState",state.Describe()+"   ·   "+selected.Address);
            ShowManualValues(state);
            if(IsBarSet && manualBar>0)ShowBarValues();
        }
        private async Task Busy(string message,Func<Task> job) {
            if (busy || liveUpdating || aiOperation) return;
            busy=true; Controls(); Text("Notice",message);
            try { await job(); }
            catch (Exception e) { Text("Notice",e.Message); if(session==null)SetConnection("Check connection","#E7AF70"); }
            finally { busy=false; Controls(); if (closing) Window.Close(); }
        }
        private async Task Discover() {
            await ScanLights();
        }
        private async Task Select(Device device) {
            if(selected==null || selected.Id!=device.Id)manualBar=0;
            roomTarget=null;selected=device; if(session==null || !sessionLayouts.TryGetValue(device.Id,out mapping))mapping=new Mapping(selected); ShowMapping(); library.Choose(device.Id);
            var state=await Task.Run(() => readDevice(device));
            ShowState(state); if(session==null)SetConnection("Connected","#78D8AD");
            var record=library.Items.First(x=>x.Id==device.Id);
            Text("CurrentModel",record.Name); Get<TextBlock>("CurrentModel").ToolTip=record.Name+" · "+device.Sku;
            Text("Notice","Ready.");
        }
        private async Task Apply(Setting value) {
            await Busy("Sending to the selected lights…",async () => {
                var group=ManualTargets();EnsureAvailable(group);
                await ReleaseBars(group);
                if(pendingResume!=null && group.Any(x=>x.Included))CancelResume();
                if(group.Length==1 && selected!=null && selected.Id==group[0].Id) {
                    if(selectedState!=null && !value.Supported(selectedState))throw new IOException("This light did not report support for that control.");
                    var applied=await Task.Run(()=>WithNetwork(lan=>lan.Change(selected,value)));
                    ShowState(applied);
                } else {
                    await Task.Run(()=>WithNetwork(lan=>{new LightingBatch(lan,group).Apply(value,null);return true;}));
                    RenderManualTarget();
                }
                if(session==null)SetConnection("Connected","#78D8AD");
                Text("Notice",group.Length==1?"Setting confirmed by the light.":"Setting confirmed by all "+group.Length+" lights.");
            });
        }
        private void ResetPreview() {
            lastPreview=null; UpdateBands(null);
            foreach (string side in new[]{"L","R"}) {
                Get<Border>("Half"+side).Background=Brush(side=="L" ? "#263748" : "#31384C");
                Get<Border>("Bar"+side).Background=Brush("#788B9D");
                Get<Ellipse>("Swatch"+side).Fill=Brush("#788B9D"); Text("Hex"+side,"");
            }
        }
        private void Preview(SceneFrame frame,CancellationTokenSource source) {
            long now = previewClock.ElapsedMilliseconds;
            if (now < nextPreview) return;
            nextPreview = now + 100;
            if (Interlocked.Exchange(ref previewQueued,1)!=0) return;
            Window.Dispatcher.BeginInvoke(new Action(() => {
                Interlocked.Exchange(ref previewQueued,0);
                if (session!=source || source.IsCancellationRequested || trayHidden || Window.WindowState==WindowState.Minimized) return;
                lastPreview=frame; UpdateBands(frame); PreviewSide("L",frame.Whole.Left); PreviewSide("R",frame.Whole.Right);
            }));
        }
        private void PreviewSide(string side, RGB c) {
            var b=Brush(c);
            Get<Border>("Half"+side).Background=b; Get<Border>("Bar"+side).Background=b;
            Get<Ellipse>("Swatch"+side).Fill=b;
            Text("Hex"+side,String.Format("#{0:X2}{1:X2}{2:X2}",c.R,c.G,c.B));
        }
        private async Task ToggleSession(bool test) {
            CancelResume();await RunSession(test);
        }
        private async Task<bool> RunSession(bool test) {
            int streamed=0;
            if (session!=null) {
                session.Cancel(); Text("Notice","Stopping sync and checking the lights…"); return false;
            }
            if (busy || liveUpdating || aiOperation || (test && selected==null)) return false;
            if(barStreams.Count>0) {
                busy=true;Controls();
                try{await ReleaseBars(library.Items.Where(x=>test?selected!=null && x.Id==selected.Id:x.Included));}
                catch(Exception ex){Text("Notice",ex.Message);return false;}
                finally{busy=false;Controls();}
                if(closing)return false;
            }
            var source=new CancellationTokenSource(); session=source; testing=test; Controls(); UpdateDisplayInfo();
            SyncTarget[] targets;
            try { targets=BuildTargets(test);RenderLibrary();if(extraPage=="Presets")RenderPresets();Controls(); } catch(Exception ex){session=null;source.Dispose(); Controls();Text("Notice",ex.Message);return false;}
            SetConnection(test ? "Testing sides" : "Syncing","#90D5FF");
            Text("Notice",test ? "Look for red on the left and blue on the right for eight seconds." : "Following the main display. You can minimize this window.");
            try {
                var restored=await Task.Run(() => playGroup(targets,test,source.Token,p=>{Interlocked.Exchange(ref streamed,1);Preview(p,source);}));
                if(selected!=null && restored.ContainsKey(selected.Id)) ShowState(restored[selected.Id]); SetConnection("Connected","#78D8AD");
                Text("Notice",test ? "Test ended. Use Swap sides if red appeared on the right." : "Sync stopped. The selected lights released screen control.");
            } catch (Exception e) { Text("Notice",e.Message); SetConnection("Check connection","#E7AF70"); }
            finally {
                session=null; source.Dispose(); ResetPreview(); Controls();
                RefreshTray();
                if(!closing) {RenderLibrary();if(extraPage=="Presets")RenderPresets();Controls();if(isManual)RenderManualTarget();}
                if (closing) Window.Close();
            }
            return streamed!=0;
        }
        private void OnClosing(object sender,CancelEventArgs e) {
            if (!quitRequested && trayEnabled && !closing) { e.Cancel=true;Window.Dispatcher.BeginInvoke(new Action(HideToTray));return; }
            CancelResume();
            if(signalCancel!=null)signalCancel.Cancel();
            if (busy || liveUpdating || aiOperation || session!=null || barStreams.Count>0) {
                e.Cancel=true; closing=true;
                if (session!=null) session.Cancel();
                CancelBars();
                Text("Notice","Finishing the current operation before closing…");
            }
        }
    }
}
