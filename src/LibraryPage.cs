using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GoveeLights {
    internal sealed partial class Controller {
        private readonly LightLibrary library=new LightLibrary();
        private readonly Dictionary<string,Mapping> sessionLayouts=new Dictionary<string,Mapping>();
        private bool onLights,scanned;
        internal bool ResumeOnStartup;
        private async Task Startup() {
            OpenLights();
            bool legacy;
            using(var k=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Govee Lights\Layout"))
                legacy=k!=null && k.SubKeyCount>0;
            if(library.Items.Count==0 && !legacy) {
                OpenLights();SetConnection("Setup","#A8B7C8");Text("Notice","Find a light to get started.");return;
            }
            await ScanLights();
            if(library.Items.Count==0) OpenLights();
            else if(ResumeOnStartup && library.Items.Any(x=>x.Included) && selected!=null) ResumeAiSync();
        }
        private void OpenLights() {
            Get<ScrollViewer>("ContentScroll").ScrollToTop();
            ExtraOff();isManual=false;onLights=true;
            Get<StackPanel>("SyncPage").Visibility=Get<StackPanel>("ManualPage").Visibility=Visibility.Collapsed;
            Get<StackPanel>("LightsPage").Visibility=Visibility.Visible;
            Get<Button>("NavSync").Background=Get<Button>("NavManual").Background=Brushes.Transparent;
            Get<Button>("NavLights").Background=Brush("#303944");
            Get<Button>("Refresh").Visibility=Visibility.Collapsed;
            Text("Heading","Lights"); RenderLibrary(); Controls();
            if(session!=null) Text("Notice","Screen sync is running. Other lights are available for manual control.");
        }
        private async Task ScanLights() {
            await Busy("Searching the local network for Govee lights…",async ()=>{
                UpdateDisplayInfo();
                string priorRoom=roomTarget;
                lights=await Task.Run(discoverNetwork);
                scanned=true;library.Migrate(lights);
                selected=null;selectedState=null;mapping=null;ShowMapping();
                var savedOnline=lights.Where(d=>library.Items.Any(x=>x.Id==d.Id && x.Sku==d.Sku)).ToList();
                var focus=savedOnline.FirstOrDefault(d=>d.Id==library.SelectedId)??savedOnline.FirstOrDefault();
                RenderLibrary();
                if(focus!=null) await Select(focus);
                else {
                    Text("CurrentModel","Light status");Text("DeviceState","No connected light selected");
                    SetConnection(library.Items.Count==0?"Setup":"Offline","#AFB5BF");
                    if(!onLights) OpenLights();
                    Text("Notice",lights.Count==0?"No lights replied. In Govee Home, enable LAN Control in each light’s settings and check that the light and PC share a network.":"Choose Add beside a discovered light.");
                }
                if(priorRoom!=null && library.Items.Any(r=>String.Equals(r.Room,priorRoom,StringComparison.CurrentCultureIgnoreCase)))roomTarget=priorRoom;
                if(isManual)RenderManualTarget();RenderLibrary();
            });
        }
        private SyncTarget[] BuildTargets(bool test) {
            sessionLayouts.Clear();
            if(test && (selected==null || selected.Sku!="H6056"))throw new IOException("Select a compatible light set in Lights to test its layout.");
            var chosen=library.Items.Where(x=>test ? x.Id==selected.Id : x.Included).ToArray();
            if(chosen.Length==0)throw new IOException("Open Lights and check Include in screen sync for at least one light.");
            return chosen.Select(record=>{
                var light=lights.FirstOrDefault(d=>d.Id==record.Id && d.Sku==record.Sku);
                // A fresh group discovery resolves the current address, including a previously offline device.
                if(light==null)light=new Device {Id=record.Id,Sku=record.Sku};
                var m=selected!=null && selected.Id==record.Id && mapping!=null ? mapping : new Mapping(light);
                sessionLayouts[record.Id]=m;
                return new SyncTarget(light,record.Name,record.Placement,()=>m.Swap,()=>m.Zones) {
                    Physical=()=>m.Physical,FlipLeft=()=>m.FlipLeft,FlipRight=()=>m.FlipRight,PeriodicModeRefresh=()=>m.PeriodicModeRefresh
                };
            }).ToArray();
        }
        private async Task NextPreview() {
            var options=lights.Where(d=>library.Items.Any(x=>x.Id==d.Id) && (session==null || sessionLayouts.ContainsKey(d.Id))).ToList();
            if(options.Count<2)return;
            var next=options[(options.FindIndex(x=>selected!=null && x.Id==selected.Id)+1)%options.Count];
            if(session==null) await Busy("Reading the light…",async()=>await Select(next));
            else {
                selected=next;mapping=sessionLayouts[next.Id];ShowMapping();library.Choose(next.Id);
                Text("CurrentModel",library.Items.First(x=>x.Id==next.Id).Name);
            }
            RenderLibrary();Controls();
        }
        private TextBlock Label(string text,bool small=false) {
            var b=new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap};
            if(small)b.Style=(Style)Window.FindResource("Small");else b.FontWeight=FontWeights.SemiBold;
            return b;
        }
        private Button ActionButton(string text,Func<Task> action) {
            var b=new Button {Content=text,ToolTip=text,Margin=new Thickness(0,3,7,2)};
            b.Click+=async(s,e)=>{try {await action();} catch(Exception ex){Text("Notice",ex.Message); RenderLibrary();}};
            return b;
        }
        private StackPanel Card(StackPanel parent) {
            var panel=new StackPanel();
            parent.Children.Add(new Border {Style=(Style)Window.FindResource("Card"),Child=panel});
            return panel;
        }
        private void RenderLibrary() {
            var found=Get<StackPanel>("FoundList");var saved=Get<StackPanel>("SavedList");
            found.Children.Clear();saved.Children.Clear();
            Text("WelcomeTitle",library.Items.Count==0?"Set up your first light":"Add lights");
            Get<Button>("FindLights").Content=scanned?"Search again":"Find lights";
            Get<TextBlock>("FoundHeading").Visibility=scanned?Visibility.Visible:Visibility.Collapsed;
            Get<TextBlock>("SavedHeading").Visibility=library.Items.Count>0?Visibility.Visible:Visibility.Collapsed;
            var available=lights.Where(d=>!library.Items.Any(x=>x.Id==d.Id)).ToList();
            if(scanned && available.Count==0)found.Children.Add(Label(lights.Count==0?"No lights found. Enable LAN Control in each light’s Govee Home settings, then search again.":"All discovered lights are saved.",true));
            foreach(var d in available) {
                var row=Card(found);row.Children.Add(Label(d.Model.Name));row.Children.Add(Label(d.Sku+"  ·  "+d.Address+"  ·  "+(d.Model.Streaming?"Screen sync and manual controls":"Manual controls"),true));
                var b=ActionButton("Add light",async()=>{
                    var item=SavedLight.From(d);
                    library.Save(item);await Busy("Reading the light…",async()=>await Select(d));RenderLibrary();
                });b.HorizontalAlignment=HorizontalAlignment.Left;row.Children.Add(b);
            }
            foreach(var room in library.Items.GroupBy(x=>(x.Room??"").Trim(),StringComparer.CurrentCultureIgnoreCase).OrderBy(g=>g.Key.Length==0?1:0).ThenBy(g=>g.Key)) {
                var header=new DockPanel{Margin=new Thickness(0,7,0,6)};
                if(room.Key.Length>0){string roomName=room.Key;var control=ActionButton("Control room",()=>{RoomControls(roomName);return Task.FromResult(0);});
                    DockPanel.SetDock(control,Dock.Right);header.Children.Add(control);}
                header.Children.Add(Label(room.Key.Length==0?"Unassigned":room.Key));saved.Children.Add(header);
                foreach(var r in room.OrderBy(x=>x.Name).ThenBy(x=>x.Id))RenderSaved(saved,r);
            }
            var group=library.Items.Where(x=>x.Included).OrderBy(x=>x.Name).ToArray();
            Text("GroupSummary",group.Length==0?"Choose lights on the Lights page.":String.Join(" · ",group.Select(x=>x.Name)));
            UpdateBands(lastPreview);
        }
        private void RenderSaved(StackPanel parent,SavedLight record) {
            var online=lights.FirstOrDefault(d=>d.Id==record.Id && d.Sku==record.Sku);
            bool focused=selected!=null && selected.Id==record.Id;
            var row=Card(parent);row.Tag=record.Id;row.Children.Add(Label(record.Name));
            row.Children.Add(Label(record.Sku+(record.Sku=="H6056"?"  ·  Two-bar set":"")+"  ·  "+(online==null?"Offline":online.Address.ToString())+(focused?"  ·  Selected":""),true));
            if(online==null)row.Children.Add(Label("Check power, Wi-Fi, and LAN Control in this light’s Govee Home settings.",true));
            var tools=new WrapPanel {Margin=new Thickness(0,6,0,0)};row.Children.Add(tools);
            if(LightModel.Find(record.Sku).Streaming) {
                var include=new CheckBox {Style=(Style)Window.FindResource("Toggle"),Content="Include in screen sync",
                    IsEnabled=session==null,IsChecked=record.Included,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,2,12,2)};
                include.Click+=(s,e)=>{try{record.Included=include.IsChecked==true;library.Save(record);RenderLibrary();Controls();}
                    catch(Exception ex){Text("Notice",ex.Message);}};
                tools.Children.Add(include);
                var role=ActionButton(record.Placement+"  ▾",()=> {
                    var menu=new ContextMenu {Background=Brush("#272B32"),Foreground=Brushes.White};
                    foreach(string placement in record.Placements()) {
                        var mi=new MenuItem {Header=placement,IsCheckable=true,IsChecked=record.Placement==placement};
                        mi.Click+=(s,e)=>{try{record.Placement=placement;library.Save(record);RenderLibrary();}catch(Exception ex){Text("Notice",ex.Message);}};menu.Items.Add(mi);
                    }
                    menu.IsOpen=true;return Task.FromResult(0);
                });role.ToolTip="Choose the screen region that supplies this light’s color";role.IsEnabled=session==null; if(record.Included)tools.Children.Add(role);
            } else tools.Children.Add(Label("Manual controls only",true));
            var actions=new WrapPanel();row.Children.Add(actions);
            var use=ActionButton("Control light",async()=>{await Busy("Reading the light…",async()=>await Select(online));RenderLibrary();if(selected!=null && selected.Id==record.Id)Navigate(true);});
            use.IsEnabled=online!=null;actions.Children.Add(use);
            actions.Children.Add(ActionButton("Edit light",()=>{EditName(record);return Task.FromResult(0);}));
            var remove=ActionButton("Remove",()=>Busy("Releasing and removing the light…",async()=>{
                await ReleaseBars(new[]{record});library.Forget(record.Id);
                if(selected!=null && selected.Id==record.Id) {
                    selected=null;selectedState=null;mapping=null;manualBar=0;ShowMapping();
                    var next=lights.FirstOrDefault(d=>library.Items.Any(x=>x.Id==d.Id && x.Sku==d.Sku));
                    if(next!=null)await Select(next);
                    else {Text("CurrentModel","Light status");Text("DeviceState","No connected light selected");}
                }
                RenderLibrary();Controls();Text("Notice","Light removed from this app.");
            }));remove.IsEnabled=session==null || !sessionLayouts.ContainsKey(record.Id);actions.Children.Add(remove);
        }
        private void EditName(SavedLight record) {
            RenderLibrary();
            // Rebuild this editor only once for the selected record.
            var panel=Get<StackPanel>("SavedList");
            var card=panel.Children.OfType<Border>().Select(b=>(StackPanel)b.Child)
                .First(x=>Object.Equals(x.Tag,record.Id));
            var edit=new TextBox {Text=record.Name,MaxLength=48,Width=180,Padding=new Thickness(6),Background=Brush("#191C22"),Foreground=Brushes.White};
            var roomEdit=new TextBox {Text=record.Room??"",MaxLength=48,Width=180,Padding=new Thickness(6),Background=Brush("#191C22"),Foreground=Brushes.White};
            var line=new WrapPanel();card.Children.Add(line);
            foreach(var pair in new[]{new {Title="Light name",Box=edit},new {Title="Room (optional)",Box=roomEdit}}) {
                var field=new StackPanel {Margin=new Thickness(0,6,8,0)};field.Children.Add(Label(pair.Title,true));field.Children.Add(pair.Box);line.Children.Add(field);
            }
            line.Children.Add(ActionButton("Save",()=>{if(String.IsNullOrWhiteSpace(edit.Text))throw new IOException("Enter a light name.");
                record.Name=edit.Text.Trim();record.Room=roomEdit.Text.Trim();library.Save(record);if(selected!=null && selected.Id==record.Id)Text("CurrentModel",record.Name);
                RenderLibrary();return Task.FromResult(0);}));
            edit.Focus();edit.SelectAll();
        }
    }
}
