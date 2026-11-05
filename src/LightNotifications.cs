using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;

namespace GoveeLights {
    internal sealed class NotificationFailure : IOException {
        internal readonly bool Restored;
        internal NotificationFailure(string message,bool restored,Exception cause=null):base(message,cause){Restored=restored;}
    }
    internal static class LightNotifications {
        internal static readonly string[] Kinds=McpProgram.SignalKinds;
        internal static Color ColorFor(string kind) {
            switch(kind){case "complete":return Color.FromArgb(72,220,140);case "attention":return Color.FromArgb(255,176,64);
                case "error":return Color.FromArgb(245,80,80);case "information":return Color.FromArgb(100,174,255);
                default:throw new ArgumentException("Choose complete, attention, error, or information.");}
        }
        internal static int Run(IGroupNetwork lan,SavedLight[] targets,string kind,bool wake,CancellationToken cancel,int duration=3200) {
            var color=ColorFor(kind);var online=lan.Discover();var originals=new Dictionary<Device,LightLook>();
            var states=new Dictionary<Device,DeviceData>();
            foreach(var record in targets) {
                var d=online.FirstOrDefault(x=>x.Id==record.Id && x.Sku==record.Sku);
                if(d==null)throw new IOException(record.Name+" is offline.");
                var state=lan.Read(d);if(state.onOff==0 && !wake)continue;
                if(state.color==null && !(state.colorTemInKelvin>=2000 && state.colorTemInKelvin<=9000))throw new IOException(record.Name+" does not report a restorable color setting.");
                originals[d]=LightLook.Capture(record,state);states[d]=state;
            }
            var touched=new List<Device>();Exception problem=null;
            try {
                foreach(var entry in originals) {
                    cancel.ThrowIfCancellationRequested();var d=entry.Key;touched.Add(d);
                    if(states[d].onOff==0)lan.Change(d,Setting.Power(true));
                    lan.SendSetting(d,states[d].color!=null?Setting.Tone(color,0):Setting.Tone(Color.Black,kind=="attention"||kind=="error"?2700:5500));
                }
                int last=-1;
                // Two gentle breaths over 3.2 seconds; no fast flashing. Keep the saved brightness as the ceiling.
                for(int elapsed=0;elapsed<duration;elapsed+=80) {
                    if(cancel.IsCancellationRequested)break;
                    int factor=(int)Math.Round(25+75*(.5-.5*Math.Cos(4*Math.PI*elapsed/Math.Max(1,duration))));
                    if(factor!=last)foreach(var d in touched)if(states[d].brightness.HasValue) {
                        int ceiling=states[d].onOff==0?Math.Min(35,Math.Max(1,states[d].brightness.Value)):Math.Max(1,states[d].brightness.Value);
                        lan.SendSetting(d,Setting.Brightness(Math.Max(1,ceiling*factor/100)));
                    }
                    last=factor;if(cancel.WaitHandle.WaitOne(80))break;
                }
            }catch(Exception ex){problem=ex;}
            var errors=new List<string>();
            foreach(var d in touched)try{originals[d].Set(lan,d,lan.Read(d),true);}catch(Exception ex){errors.Add(originals[d].Name+": "+ex.Message);}
            if(errors.Count>0)throw new NotificationFailure("Check these lights after the notification: "+String.Join("; ",errors),false);
            if(problem!=null && !(problem is OperationCanceledException))throw new NotificationFailure("Notification stopped; previous settings restored. "+problem.Message,true,problem);
            return touched.Count;
        }
    }
}
