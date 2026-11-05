using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace GoveeLights {
    internal sealed class LightModel {
        public string Name;
        public bool Streaming;
        private static readonly Dictionary<string,LightModel> Models = Load();
        private static Dictionary<string,LightModel> Load() {
            var models=new Dictionary<string,LightModel>(StringComparer.OrdinalIgnoreCase);
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("LightModels.tsv"))
            using(var reader=new StreamReader(stream)) {
                string line;
                while((line=reader.ReadLine())!=null) {
                    var v=line.Split('\t'); models[v[0]]=new LightModel{Name=v[1],Streaming=v[2]=="1"};
                }
            }
            return models;
        }
        public static LightModel Find(string sku) {
            LightModel model; return sku!=null && Models.TryGetValue(sku,out model) ? model : null;
        }
    }
    internal sealed class SavedLight {
        public string Id {get;set;}
        public string Sku {get;set;}
        public string Name {get;set;}
        public string Placement {get;set;}
        public bool Included {get;set;}
        public string Room {get;set;}
        public SavedLight() {}
        public static SavedLight From(Device light) {
            return new SavedLight{Id=light.Id,Sku=light.Sku,Name=light.Model.Name,
                Placement="Whole display",Included=false,Room=""};
        }
        public string[] Placements() { return Sku=="H6056" ? new[]{"Whole display","Left half","Right half","Top half","Bottom half","Split bars"} :
            new[]{"Whole display","Left half","Right half","Top half","Bottom half"}; }
    }
    internal sealed class LightLibrary {
        private const string Location=@"Software\Govee Lights\Library";
        private readonly JavaScriptSerializer json=new JavaScriptSerializer();
        public List<SavedLight> Items=new List<SavedLight>();
        public string SelectedId;
        public LightLibrary() {
            using(var key=Registry.CurrentUser.OpenSubKey(Location)) {
                if(key==null) return;
                SelectedId=key.GetValue("Selected") as string;
                foreach(string id in key.GetValueNames().Where(s=>s.StartsWith("Light-"))) {
                    try {
                        var item=json.Deserialize<SavedLight>(key.GetValue(id) as string ?? "");
                        if(item!=null && Device.ValidId(item.Id) && id=="Light-"+item.Id && LightModel.Find(item.Sku)!=null)
                            Items.Add(item);
                    } catch(ArgumentException) {} catch(InvalidOperationException) {}
                }
            }
        }
        public void Migrate(List<Device> online) {
            using(var key=Registry.CurrentUser.OpenSubKey(Location))
                if(key!=null && Object.Equals(key.GetValue("Initialized"),1)) return;
            foreach(var d in online.Where(x=>x.Sku=="H6056")) {
                using(var prior=Registry.CurrentUser.OpenSubKey(Mapping.PathFor(d.Id))) {
                    if(prior!=null && !Items.Any(x=>x.Id==d.Id)) {
                        var known=SavedLight.From(d);known.Placement="Split bars";known.Included=true;Save(known);
                    }
                }
            }
        }
        public void Save(SavedLight item) {
            if(!Device.ValidId(item.Id) || LightModel.Find(item.Sku)==null) throw new ArgumentException("Unknown light.");
            using(var key=Registry.CurrentUser.CreateSubKey(Location)) {
                key.SetValue("Light-"+item.Id,json.Serialize(item)); key.SetValue("Initialized",1);
            }
            Items.RemoveAll(x=>x.Id==item.Id); Items.Add(item);
        }
        public void Choose(string id) {
            if(SelectedId==id)return;
            using(var key=Registry.CurrentUser.CreateSubKey(Location)) key.SetValue("Selected",id??"");
            SelectedId=id;
        }
        public void Forget(string id) {
            using(var key=Registry.CurrentUser.CreateSubKey(Location)) {
                key.DeleteValue("Light-"+id,false); key.SetValue("Initialized",1);
            }
            Items.RemoveAll(x=>x.Id==id);
            if(SelectedId==id) Choose(null);
        }
    }
}
