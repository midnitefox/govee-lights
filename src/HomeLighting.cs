using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GoveeLights {
    internal sealed partial class Controller {
        private string roomTarget;
        private readonly PresetStore presets=new PresetStore();
        private readonly HashSet<string> presetSelection=new HashSet<string>();
        private DeviceData selectedState;
        private T WithNetwork<T>(Func<IGroupNetwork,T> run) {
            var lan=openNetwork();try{return run(lan);}finally{var handle=lan as IDisposable;if(handle!=null)handle.Dispose();}
        }
        private string extraPage;
        private SavedLight[] ManualTargets() {
            return library.Items.Where(x=>roomTarget==null ? selected!=null && x.Id==selected.Id :
                String.Equals(x.Room,roomTarget,StringComparison.CurrentCultureIgnoreCase)).ToArray();
        }
        private bool OverlapsSync(IEnumerable<SavedLight> group) {
            return session!=null && group.Any(x=>sessionLayouts.ContainsKey(x.Id));
        }
        private void ExtraOff() {
            extraPage=null;
            foreach(var name in new[]{"Presets","Settings"}) {
                Get<StackPanel>(name+"Page").Visibility=Visibility.Collapsed;Get<Button>("Nav"+name).Background=Brushes.Transparent;
            }
        }
        private void OpenExtra(string page) {
            Get<ScrollViewer>("ContentScroll").ScrollToTop();
            ExtraOff();extraPage=page;isManual=false;onLights=false;
            foreach(var name in new[]{"Lights","Manual","Sync"}) {
                Get<StackPanel>(name+"Page").Visibility=Visibility.Collapsed;Get<Button>("Nav"+name).Background=Brushes.Transparent;
            }
            Text("Heading",page);Get<StackPanel>(page+"Page").Visibility=Visibility.Visible;
            Get<Button>("Nav"+page).Background=Brush("#303944");Get<Button>("Refresh").Visibility=Visibility.Collapsed;
            if(page=="Settings"){ShowSignInSettings();RenderAiSettings();}else RenderPresets();
            Controls();
        }
        private void RenderManualTarget() {
            var targets=ManualTargets();
            Text("ControlTarget",roomTarget!=null?roomTarget:targets.Length>0?targets[0].Name:"Choose a light or room");
            if(roomTarget!=null)Text("DeviceState",targets.Length+" saved light"+(targets.Length==1?"":"s")+" · Commands apply to the whole room.");
            if(OverlapsSync(targets))Text("Notice","Stop screen sync to change lights in the active sync group.");
            RenderBarControls();ShowBarValues();
        }
        private void RoomControls(string room) {
            roomTarget=room;Navigate(true);Controls();RenderManualTarget();
        }
        private void SetupPresets() {
            Click("NavPresets",()=>OpenExtra("Presets")); Click("SavePreset",async()=>await SaveLook(null));
        }
        private void RenderPresets() {
            var members=Get<StackPanel>("PresetMembers");var list=Get<StackPanel>("PresetList");
            members.Children.Clear();list.Children.Clear();presetSelection.RemoveWhere(id=>!library.Items.Any(x=>x.Id==id));
            var roomActions=new WrapPanel();members.Children.Add(roomActions);
            foreach(var group in library.Items.Where(x=>!String.IsNullOrWhiteSpace(x.Room)).GroupBy(x=>x.Room,StringComparer.CurrentCultureIgnoreCase)) {
                var eligible=group.Where(x=>!OverlapsSync(new[]{x})).ToArray();if(eligible.Length==0)continue;
                bool selectedAll=eligible.All(x=>presetSelection.Contains(x.Id));
                roomActions.Children.Add(ActionButton((selectedAll?"Clear ":"Select ")+group.Key,()=>{
                    foreach(var light in eligible)if(selectedAll)presetSelection.Remove(light.Id);else presetSelection.Add(light.Id);
                    RenderPresets();Controls();return Task.FromResult(0);
                }));
            }
            foreach(var light in library.Items.OrderBy(x=>x.Room).ThenBy(x=>x.Name)) {
                bool locked=OverlapsSync(new[]{light});
                var check=new CheckBox {Style=(Style)Window.FindResource("Toggle"),Content=light.Name+
                    (String.IsNullOrWhiteSpace(light.Room)?"":" · "+light.Room)+(locked?" · Syncing":""),
                    Margin=new Thickness(0,4,0,4),IsEnabled=!locked,IsChecked=presetSelection.Contains(light.Id)};
                check.Click+=(s,e)=>{if(check.IsChecked==true)presetSelection.Add(light.Id);else presetSelection.Remove(light.Id);Controls();};
                members.Children.Add(check);
            }
            if(library.Items.Count==0)members.Children.Add(Label("Add a light on the Lights page to create a preset.",true));
            foreach(var p in presets.Read()) {
                var row=Card(list);row.Children.Add(Label(p.Name));
                row.Children.Add(Label(String.Join(" · ",p.Lights.Select(l=>{
                    var item=library.Items.FirstOrDefault(x=>x.Id==l.Id && x.Sku==l.Sku);return item==null?l.Name+" (removed)":item.Name;
                })),true));
                var actions=new WrapPanel();row.Children.Add(actions);
                actions.Children.Add(ActionButton("Apply preset",async()=>await ApplyLook(p)));
                var update=ActionButton("Update preset",async()=>await SaveLook(p));
                update.ToolTip="Replace the saved settings with the current state of this preset’s lights";actions.Children.Add(update);
                actions.Children.Add(ActionButton("Remove",()=>{presets.Remove(p.Id);RenderPresets();Text("Notice","Preset removed.");return Task.FromResult(0);}));
            }
        }
        private SavedLight[] PresetTargets(LightingPreset p) {
            return p.Lights.Select(l=>library.Items.FirstOrDefault(x=>x.Id==l.Id && x.Sku==l.Sku)
                ?? MissingPresetLight(l.Name)).ToArray();
        }
        private static SavedLight MissingPresetLight(string name){throw new IOException(name+" is no longer saved. Add it again or create a new preset.");}
        private void EnsureAvailable(SavedLight[] group) {
            if(group.Length==0)throw new IOException("Choose at least one saved light.");
            if(OverlapsSync(group))throw new IOException("Stop screen sync before changing or saving lights in its active group.");
        }
        private async Task SaveLook(LightingPreset existing) {
            await Busy("Reading the selected lights…",async()=>{
                var group=existing==null?library.Items.Where(x=>presetSelection.Contains(x.Id)).ToArray():PresetTargets(existing);
                EnsureAvailable(group);
                if(group.Any(x=>barStreams.ContainsKey(x.Id)))throw new IOException("Release individual bar control before saving a native device preset. Govee does not report individual streaming colors.");
                string name=existing==null?Get<TextBox>("PresetName").Text.Trim():existing.Name;
                if(String.IsNullOrWhiteSpace(name))throw new IOException("Enter a name for the preset.");
                var looks=await Task.Run(()=>WithNetwork(lan=>new LightingBatch(lan,group).Capture()));
                presets.Save(new LightingPreset{Id=existing==null?null:existing.Id,Name=name,Lights=looks});
                if(existing==null){presetSelection.Clear();Get<TextBox>("PresetName").Clear();}
                RenderPresets();Text("Notice","Saved “"+name+"” for "+group.Length+(group.Length==1?" light.":" lights."));
            });
        }
        private async Task ApplyLook(LightingPreset preset) {
            await Busy("Applying the preset…",async()=>{
                var group=PresetTargets(preset);EnsureAvailable(group);
                await ReleaseBars(group);
                if(pendingResume!=null && group.Any(x=>x.Included))CancelResume();
                await Task.Run(()=>WithNetwork(lan=>{new LightingBatch(lan,group).Apply(null,preset);return true;}));
                if(selected!=null && group.Any(x=>x.Id==selected.Id)) ShowState(await Task.Run(()=>readDevice(selected)));
                Text("Notice","Applied “"+preset.Name+"”.");
            });
        }
    }
}
