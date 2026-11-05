using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;

namespace GoveeLights {
    internal interface IGroupNetwork {
        List<Device> Discover(string targetId=null);
        DeviceData Read(Device light,Func<DeviceData,bool> expected=null);
        DeviceData Change(Device light,Setting setting);
        void SendSetting(Device light,Setting setting);
        void StreamPacket(Device light,string packet);
        bool PollGroupHealth(Device[] lights,long now);
        double StreamIntervalMs {get;}
    }
    internal sealed class SyncTarget {
        public Device Light;
        public readonly string Name, Placement;
        private readonly Func<bool> swap;
        private readonly Func<int> zones;
        public DeviceData Before,After;
        public bool Armed;
        private readonly Color[] sent=new Color[12], candidate=new Color[12];
        public Func<bool> FlipLeft=()=>false, FlipRight=()=>false;
        public Func<int> Physical=()=>6;
        public Func<bool> PeriodicModeRefresh;
        private int lastZones;
        private bool lastSwap;
        private double lastSend=-1000,lastChange=-1000;
        private string lastPacket;
        public SyncTarget(Device light,string name,string placement,Func<bool> swap,Func<int> zones) {
            Light=light; Name=name; Placement=placement; this.swap=swap; this.zones=zones;
            PeriodicModeRefresh=()=>Light.Sku!="H6056";
        }
        internal void RefreshStream(IGroupNetwork lan) {
            lan.StreamPacket(Light,ScreenSync.Enable);
            if(lastPacket!=null)lan.StreamPacket(Light,lastPacket);
            lastSend=-1000; // Also repeat the newest frame even when its colors are unchanged.
        }
        internal void MaintainMode(IGroupNetwork lan){if(PeriodicModeRefresh())RefreshStream(lan);}
        public bool Send(IGroupNetwork lan,SceneFrame frame,double now) {
            bool split=Placement=="Split bars" && Light.Sku=="H6056";
            int physical=split?Physical():1, count=physical*2;
            bool reverse=split && swap();
            if(split) {
                int n=Math.Min(zones(),physical); var bands=frame.Bands(n);
                bool flipLeft=FlipLeft(),flipRight=FlipRight();
                for(int i=0;i<physical;i++) {
                    int band=i*n/physical;
                    candidate[(reverse?physical:0)+i]=bands[flipLeft?n-1-band:band].Left;
                    candidate[(reverse?0:physical)+i]=bands[flipRight?n-1-band:band].Right;
                }
            } else {
                Color c=SceneColors.Source(frame,Placement);
                candidate[0]=candidate[1]=c;
            }
            bool changed=lastZones!=count || reverse!=lastSwap;
            for(int i=0;i<count;i++) {
                if(lastZones==count) candidate[i]=SceneColors.HoldNoise(sent[i],candidate[i]);
                changed |= candidate[i].ToArgb()!=sent[i].ToArgb();
            }
            if(now-lastSend<250 && (!changed || now-lastChange<lan.StreamIntervalMs-1)) return false;
            lastPacket=ScreenSync.EncodeZones(candidate,count,!split);
            lan.StreamPacket(Light,lastPacket);
            if(changed) lastChange=now;
            Array.Copy(candidate,sent,count);lastSend=now;lastZones=count;lastSwap=reverse;
            return true;
        }
        public void Restore(IGroupNetwork lan) {
            var same=lan.Discover(Light.Id).FirstOrDefault(d=>d.Id==Light.Id && d.Sku==Light.Sku);
            if(same==null) throw new IOException("Disconnected before restoration.");
            lan.StreamPacket(same,ScreenSync.Disable); Thread.Sleep(80);
            lan.StreamPacket(same,ScreenSync.Disable); Thread.Sleep(150);
            After=lan.Read(same);
            if(After.onOff!=1) return;
            if(Before.onOff==0) { After=lan.Change(same,Setting.Power(false));return; }
            if(Before.colorTemInKelvin>0 && Before.colorTemInKelvin!=After.colorTemInKelvin)
                After=lan.Change(same,Setting.Tone(Color.Black,Before.colorTemInKelvin.Value));
            else if(Before.colorTemInKelvin==0 && Before.color!=null && After.color!=null &&
                (After.colorTemInKelvin!=0 || Before.color.r!=After.color.r || Before.color.g!=After.color.g || Before.color.b!=After.color.b))
                After=lan.Change(same,Setting.Tone(Color.FromArgb(Before.color.r,Before.color.g,Before.color.b),0));
            if(Before.brightness>0 && Before.brightness!=After.brightness)
                After=lan.Change(same,Setting.Brightness(Before.brightness.Value));
        }
    }
    internal static class GroupPlayback {
        internal static Dictionary<string,DeviceData> Execute(IGroupNetwork lan,SyncTarget[] group,bool test,
            CancellationToken cancel,Func<SceneFrame> sample,Action<SceneFrame> preview,Action<double> timing=null) {
            if(group.Length==0 || group.Select(x=>x.Light.Id).Distinct().Count()!=group.Length)
                throw new IOException("Choose a light for screen sync.");
            var online=lan.Discover();
            foreach(var t in group) {
                var light=online.FirstOrDefault(d=>d.Id==t.Light.Id && d.Sku==t.Light.Sku);
                if(light==null) throw new IOException(t.Name+" is offline. Reconnect it or uncheck it in Lights.");
                if(!light.Model.Streaming) throw new IOException(t.Name+" supports manual controls only.");
                t.Light=light; t.Before=lan.Read(light); t.After=t.Before;
            }
            var devices=group.Select(t=>t.Light).ToArray();
            var testFrame=test?SceneFrame.Solid(Pair.Test):null;
            SceneFrame initial=testFrame;
            if(!test && !cancel.IsCancellationRequested)initial=sample();
            var errors=new List<string>();
            try {
                foreach(var t in group) {
                    if(cancel.IsCancellationRequested) break;
                    t.Armed=true;
                    if(t.Before.onOff==0) lan.Change(t.Light,Setting.Power(true));
                    lan.StreamPacket(t.Light,ScreenSync.Enable);
                    if(initial!=null)t.Send(lan,initial,0);
                }
                if(!cancel.WaitHandle.WaitOne(105)) {
                    var elapsed=Stopwatch.StartNew();double next=0,arm=25000;
                    using(var pacer=new FramePacer(cancel)) while(!cancel.IsCancellationRequested && (!test || elapsed.ElapsedMilliseconds<8000)) {
                        if(!lan.PollGroupHealth(devices,elapsed.ElapsedMilliseconds)) break;
                        if(elapsed.Elapsed.TotalMilliseconds>=arm) {
                            foreach(var t in group)t.MaintainMode(lan);
                            arm=elapsed.Elapsed.TotalMilliseconds+25000;
                        }
                        double start=elapsed.Elapsed.TotalMilliseconds;
                        SceneFrame frame=test?testFrame:sample();
                        bool transmitted=false;
                        foreach(var t in group) transmitted |= t.Send(lan,frame,elapsed.Elapsed.TotalMilliseconds);
                        if(transmitted && timing!=null) timing(elapsed.Elapsed.TotalMilliseconds-start);
                        if(preview!=null) preview(frame);
                        double period=test?80:ScreenSync.SampleIntervalMs;
                        next+=period; double done=elapsed.Elapsed.TotalMilliseconds;
                        if(next<done) next+=Math.Floor((done-next)/period+1)*period;
                        pacer.WaitUntil(next,elapsed);
                    }
                }
            } catch(Exception e) { errors.Add(e.Message); }
            finally {
                foreach(var t in group.Where(x=>x.Armed)) {
                    try {t.Restore(lan);} catch(Exception e) {errors.Add(t.Name+" — "+e.Message);}
                }
            }
            if(errors.Count>0) throw new IOException(String.Join(" ",errors));
            return group.ToDictionary(t=>t.Light.Id,t=>t.After);
        }
    }
}
