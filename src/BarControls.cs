using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RGB=System.Drawing.Color;

namespace GoveeLights {
    internal sealed partial class Controller {
        private readonly Dictionary<string,BarStream> barStreams=new Dictionary<string,BarStream>();
        private int manualBar;
        private bool IsBarSet {get{return roomTarget==null && selected!=null && selected.Sku=="H6056";}}
        private void SetupBarControls() {
            SyncOptions.Load();Get<Slider>("Comfort").Value=SyncOptions.Comfort;
            Get<Button>("ColorRange").Content=SyncOptions.Palettes[SyncOptions.Palette]+" ▾";
            Click("ColorRange",()=>{
                var menu=new ContextMenu();
                for(int i=0;i<SyncOptions.Palettes.Length;i++) {
                    int choice=i;var item=new MenuItem{Header=SyncOptions.Palettes[i],IsCheckable=true,IsChecked=SyncOptions.Palette==i};
                    item.Click+=(s,e)=>{SyncOptions.Palette=choice;Get<Button>("ColorRange").Content=SyncOptions.Palettes[choice]+" ▾";
                        try{SyncOptions.Save();}catch(Exception ex){Text("Notice",ex.Message);}};
                    menu.Items.Add(item);
                }
                menu.PlacementTarget=Get<Button>("ColorRange");menu.IsOpen=true;
            });
            Text("ComfortValue",SyncOptions.Comfort+"%");
            Get<Slider>("Comfort").ValueChanged+=(s,e)=>{
                SyncOptions.Comfort=(int)e.NewValue;Text("ComfortValue",SyncOptions.Comfort==0?"Accurate · 0%":SyncOptions.Comfort+"%");
                try{SyncOptions.Save();}catch(Exception ex){Text("Notice","Comfort applies this session. "+ex.Message);}
            };
            Click("BarChoice",()=>{
                var menu=new ContextMenu();
                for(int i=0;i<3;i++) {
                    int choice=i;var item=new MenuItem{Header=i==0?"Both bars":"Bar "+i,IsCheckable=true,IsChecked=manualBar==i};
                    item.Click+=(s,e)=>{manualBar=choice;RenderBarControls();ShowBarValues();Controls();};menu.Items.Add(item);
                }
                menu.PlacementTarget=Get<Button>("BarChoice");menu.IsOpen=true;
            });
            Click("ReleaseBars",async()=>await Busy("Restoring the set’s previous lighting…",async()=>{
                await ReleaseBars(ManualTargets());manualBar=0;RenderBarControls();ShowBarValues();Text("Notice","Individual bar control released. Previous set lighting restored.");
            }));
        }
        private void RenderBarControls() {
            bool set=IsBarSet;Get<Border>("BarSetCard").Visibility=set?Visibility.Visible:Visibility.Collapsed;
            bool active=set && barStreams.ContainsKey(selected.Id);
            Get<Button>("BarChoice").Content=manualBar==0?"Both bars ▾":"Bar "+manualBar+" ▾";
            Get<Button>("BarChoice").IsEnabled=!liveUpdating;
            Get<Button>("ReleaseBars").IsEnabled=active && !liveUpdating;
            Text("BarStatus",active?"Individual RGB control is running. It continues in the tray and restores on release or exit.":
                "This device contains two bars. Individual RGB control runs while the app is open. Power and white warmth apply to both bars.");
            var record=set?library.Items.FirstOrDefault(x=>x.Id==selected.Id):null;
            Text("BarMapping",record!=null && record.Placement=="Split bars" && mapping!=null?
                "Screen mapping: Bar 1 → "+(mapping.Swap?"right":"left")+"; Bar 2 → "+(mapping.Swap?"left":"right")+".":"Bar numbers follow the device’s wiring order.");
            Text("BrightnessTitle",set && manualBar>0?"Bar "+manualBar+" intensity":"Brightness");
            Text("BrightnessHelp",set && manualBar>0?"Relative to the set’s brightness. 0% makes this bar dark.":"Adjusts live as you slide");
            var slider=Get<Slider>("Level");showingManualValues=true;
            try{slider.Minimum=set && manualBar>0?0:1;}finally{showingManualValues=false;}
        }
        private void ShowBarValues() {
            if(!IsBarSet || manualBar==0){if(selectedState!=null)ShowManualValues(selectedState);return;}
            BarStream stream;BarLook look;
            if(barStreams.TryGetValue(selected.Id,out stream))look=stream.Snapshot();
            else {
                look=new BarLook();if(selectedState!=null && selectedState.colorTemInKelvin==0 && selectedState.color!=null)
                    look.Colors[0]=look.Colors[1]=RGB.FromArgb(selectedState.color.r,selectedState.color.g,selectedState.color.b);
            }
            showingManualValues=true;
            try{Get<Slider>("Level").Value=look.Levels[manualBar-1];wheel.SelectedColor=look.Colors[manualBar-1];ShowChosenColor(look.Colors[manualBar-1]);}
            finally{showingManualValues=false;}
        }
        private async void QueueBar(RGB? color,int? level) {
            if(showingManualValues || !isManual || !IsBarSet || manualBar==0 || busy || aiOperation || closing || OverlapsSync(ManualTargets()))return;
            BarStream stream;
            if(barStreams.TryGetValue(selected.Id,out stream)){stream.Update(manualBar-1,color,level);return;}
            if(liveUpdating)return;liveUpdating=true;Controls();CancelResume();
            try {
                var saved=library.Items.First(x=>x.Id==selected.Id);stream=CreateBarStream(saved,null);
                stream.Update(manualBar-1,color,level);stream.Start();WatchBars(stream);await stream.Ready;
                Text("Notice","Individual bar colors sent. White-temperature scenes start the other bar at neutral RGB white; Release restores the original scene.");
            }catch(Exception ex){Text("Notice",ex.Message);}
            finally{liveUpdating=false;RenderBarControls();Controls();RefreshTray();if(closing)Window.Close();}
        }
        private BarStream CreateBarStream(SavedLight saved,BarLook look) {
            var d=new Device{Id=saved.Id,Sku=saved.Sku};var stream=new BarStream(saved,new Mapping(d).Physical,openNetwork,look);
            barStreams.Add(saved.Id,stream);return stream;
        }
        private async void WatchBars(BarStream stream) {
            try {
                var state=await stream.Completion;
                if(selected!=null && selected.Id==stream.Saved.Id && state!=null)ShowState(state);
            }catch(Exception ex){Text("Notice",ex.Message);SetConnection("Check connection","#E7AF70");}
            finally {
                BarStream active;if(barStreams.TryGetValue(stream.Saved.Id,out active) && active==stream)barStreams.Remove(stream.Saved.Id);
                RenderBarControls();Controls();RefreshTray();if(closing)Window.Close();
            }
        }
        private async Task ReleaseBars(IEnumerable<SavedLight> lightsToRelease) {
            var ids=new HashSet<string>(lightsToRelease.Select(x=>x.Id));var streams=barStreams.Values.Where(s=>ids.Contains(s.Saved.Id)).ToArray();
            foreach(var stream in streams)stream.Cancel();
            foreach(var stream in streams) {
                try{await stream.Completion;}finally{BarStream current;if(barStreams.TryGetValue(stream.Saved.Id,out current) && current==stream)barStreams.Remove(stream.Saved.Id);}
            }
        }
        private async Task ResumeBars(KeyValuePair<SavedLight,BarLook>[] saved) {
            foreach(var pair in saved) {
                if(closing || quitRequested)break;
                var stream=CreateBarStream(pair.Key,pair.Value);stream.Start();WatchBars(stream);await stream.Ready;
            }
        }
        private void CancelBars(){foreach(var stream in barStreams.Values.ToArray())stream.Cancel();}
    }
}
