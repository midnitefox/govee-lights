using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace GoveeLights {
    internal sealed class LightLook {
        public string Id {get;set;} public string Sku {get;set;} public string Name {get;set;}
        public int Power {get;set;} public int? Brightness {get;set;} public int? Temperature {get;set;} public int? RgbValue {get;set;}
        public LightLook() {}
        public static LightLook Capture(SavedLight record,DeviceData state) {
            var look=new LightLook{Id=record.Id,Sku=record.Sku,Name=record.Name,Power=state.onOff.Value,
                Brightness=state.brightness>0?state.brightness:null};
            if(state.colorTemInKelvin>=2000 && state.colorTemInKelvin<=9000)look.Temperature=state.colorTemInKelvin;
            else if(state.color!=null && state.color.r>=0 && state.color.r<=255 && state.color.g>=0 &&
                state.color.g<=255 && state.color.b>=0 && state.color.b<=255)
                look.RgbValue=(state.color.r<<16)|(state.color.g<<8)|state.color.b;
            return look;
        }
        public bool Valid {get{return Device.ValidId(Id) && LightModel.Find(Sku)!=null && (Power==0||Power==1) &&
            (!Brightness.HasValue || (Brightness>=1 && Brightness<=100)) &&
            (!Temperature.HasValue || (Temperature>=2000 && Temperature<=9000)) &&
            (!RgbValue.HasValue || (RgbValue>=0 && RgbValue<=0xffffff));}}
        public void Set(IGroupNetwork lan, Device light,DeviceData state,bool rollback=false) {
            if(!Valid)throw new IOException("Invalid saved settings for "+Name+".");
            // Off entries never wake an already-off light to change its stored color.
            if(Power==0 && (state.onOff==0 || !rollback)) {
                if(state.onOff!=0)lan.Change(light,Setting.Power(false));return;
            }
            if(state.onOff==0)state=lan.Change(light,Setting.Power(true));
            Setting tone=null;
            if(Temperature.HasValue)tone=Setting.Tone(Color.Black,Temperature.Value);
            else if(RgbValue.HasValue)tone=Setting.Tone(Color.FromArgb(unchecked((int)0xff000000)|RgbValue.Value),0);
            if(tone!=null && !tone.Matches(state))state=lan.Change(light,tone);
            if(Brightness.HasValue && state.brightness!=Brightness)state=lan.Change(light,Setting.Brightness(Brightness.Value));
            if(Power==0)lan.Change(light,Setting.Power(false));
        }
    }
    internal sealed class LightingPreset {
        public string Id {get;set;} public string Name {get;set;}
        public List<LightLook> Lights {get;set;}
        public LightingPreset() {}
    }
    internal sealed class PresetStore {
        private const string Path=@"Software\Govee Lights\Presets";
        private readonly JavaScriptSerializer json=new JavaScriptSerializer();
        public List<LightingPreset> Read() {
            var result=new List<LightingPreset>();using(var key=Registry.CurrentUser.OpenSubKey(Path)) {
                if(key!=null)foreach(var id in key.GetValueNames()) try {
                    var p=json.Deserialize<LightingPreset>(key.GetValue(id) as string??"");
                    if(p!=null && id==p.Id && p.Lights!=null && p.Lights.Count>0 && p.Lights.All(x=>x!=null && x.Valid))result.Add(p);
                }catch(ArgumentException){}catch(InvalidOperationException){}
            }return result.OrderBy(x=>x.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        public void Save(LightingPreset p) {
            if(String.IsNullOrWhiteSpace(p.Name) || p.Name.Trim().Length>48)throw new IOException("Enter a preset name of 1–48 characters.");
            if(Read().Any(x=>x.Id!=p.Id && String.Equals(x.Name,p.Name.Trim(),StringComparison.CurrentCultureIgnoreCase)))
                throw new IOException("A preset already uses that name. Choose another name or update the existing preset.");
            p.Name=p.Name.Trim();if(p.Id==null)p.Id=Guid.NewGuid().ToString("N");
            using(var k=Registry.CurrentUser.CreateSubKey(Path))k.SetValue(p.Id,json.Serialize(p));
        }
        public void Remove(string id){using(var k=Registry.CurrentUser.CreateSubKey(Path))k.DeleteValue(id,false);}
    }
    internal sealed class LightingBatch {
        private sealed class Entry {public Device Device; public SavedLight Record; public DeviceData Before; public LightLook Original;}
        private readonly List<Entry> entries=new List<Entry>();
        private readonly IGroupNetwork lan;
        public LightingBatch(IGroupNetwork network,IEnumerable<SavedLight> targets) {
            lan=network;var requested=targets.ToArray();
            if(requested.Length==0)throw new IOException("Choose at least one saved light.");
            var online=lan.Discover();
            foreach(var r in requested) {
                var device=online.FirstOrDefault(x=>x.Id==r.Id && x.Sku==r.Sku);
                if(device==null)throw new IOException(r.Name+" is offline. Reconnect it and try again.");
                var before=lan.Read(device);
                entries.Add(new Entry{Device=device,Record=r,Before=before,Original=LightLook.Capture(r,before)});
            }
        }
        public List<LightLook> Capture(){return entries.Select(e=>e.Original).ToList();}
        public void Apply(Setting common,LightingPreset preset) {
            var desired=preset==null?null:preset.Lights.ToDictionary(x=>x.Id);
            foreach(var e in entries) {
                if(common!=null) {
                    if(!common.Supported(e.Before))throw new IOException(e.Record.Name+" did not report support for that control.");
                } else if(desired==null || !desired.ContainsKey(e.Record.Id) || desired[e.Record.Id].Sku!=e.Record.Sku || !desired[e.Record.Id].Valid)
                    throw new IOException("This preset no longer matches the selected lights.");
                else {
                    var look=desired[e.Record.Id];
                    if(look.Power==1 && ((look.Brightness.HasValue && !e.Before.brightness.HasValue) ||
                        (look.Temperature.HasValue && !e.Before.colorTemInKelvin.HasValue) || (look.RgbValue.HasValue && e.Before.color==null)))
                        throw new IOException(e.Record.Name+" did not report controls required by this preset.");
                }
            }
            var touched=new List<Entry>();
            try {
                foreach(var e in entries) {
                    if(common!=null && common.Matches(e.Before))continue;
                    touched.Add(e);
                    if(common!=null)lan.Change(e.Device,common);
                    else desired[e.Record.Id].Set(lan,e.Device,e.Before);
                }
            } catch(Exception ex){
                var errors=new List<string>();
                foreach(var e in touched.AsEnumerable().Reverse())try {
                    e.Original.Set(lan,e.Device,lan.Read(e.Device),true);
                }catch(Exception restore){errors.Add(e.Record.Name+" ("+restore.Message+")");}
                throw new IOException(ex.Message+(errors.Count==0?" Previous settings restored.":" Check these lights: "+String.Join(", ",errors)),ex);
            }
        }
    }
}
