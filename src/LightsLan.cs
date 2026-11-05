using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace GoveeLights {
    internal class Device {
        public string Id;
        public string Sku;
        public LightModel Model { get { return LightModel.Find(Sku); } }
        public IPAddress Address;
        public static bool ValidId(string id) {
            return !String.IsNullOrEmpty(id) && id.Length<=64 && id.Length>=8 &&
                id.All(c=>Uri.IsHexDigit(c) || c==':' || c=='-');
        }
        public override string ToString() { return Sku+"   ·   "+Address; }
    }

    [DataContract] internal class Envelope {
        [DataMember] public Message msg { get; set; }
    }
    [DataContract] internal class Message {
        [DataMember] public string cmd { get; set; }
        [DataMember] public DeviceData data { get; set; }
    }
    [DataContract] internal class Rgb {
        [DataMember] public int r { get; set; }
        [DataMember] public int g { get; set; }
        [DataMember] public int b { get; set; }
    }
    [DataContract] internal class DeviceData {
        [DataMember] public string device { get; set; }
        [DataMember] public string sku { get; set; }
        [DataMember] public int? onOff { get; set; }
        [DataMember] public int? brightness { get; set; }
        [DataMember] public Rgb color { get; set; }
        [DataMember] public int? colorTemInKelvin { get; set; }
        public bool ValidStatus {
            get { return (onOff == 0 || onOff == 1) && (!brightness.HasValue || (brightness >= 0 && brightness <= 100)); }
        }
        public string Describe() {
            string tone = colorTemInKelvin > 0 ? colorTemInKelvin + " K"
                : color != null ? String.Format("#{0:X2}{1:X2}{2:X2}", color.r, color.g, color.b) : "";
            return (onOff == 1 ? "On" : "Off") + (brightness.HasValue ? "   ·   " + brightness + "%" : "") + (tone == "" ? "" : "   ·   " + tone);
        }
    }

    internal class Setting {
        public string Command;
        public object Payload;
        public Func<DeviceData, bool> Matches;
        public string Feature;
        public bool Supported(DeviceData s) {
            return Feature=="brightness"?s.brightness.HasValue:Feature=="rgb"?s.color!=null:
                Feature=="temperature"?s.colorTemInKelvin.HasValue:s.onOff.HasValue;
        }
        public static Setting Power(bool on) {
            int value = on ? 1 : 0;
            return new Setting { Command = "turn", Feature="power",Payload = new {value}, Matches = s => s.onOff == value };
        }
        public static Setting Brightness(int value) {
            if (value < 1 || value > 100) throw new ArgumentOutOfRangeException("value");
            return new Setting { Command = "brightness",Feature="brightness", Payload = new {value}, Matches = s => s.brightness == value };
        }
        public static Setting Tone(Color rgb, int kelvin) {
            return new Setting {
                Command = "colorwc",
                Feature=kelvin>0?"temperature":"rgb",
                Payload = new {color = new {r = (int)rgb.R, g = (int)rgb.G, b = (int)rgb.B}, colorTemInKelvin = kelvin},
                Matches = s => kelvin > 0 ? s.colorTemInKelvin == kelvin :
                    s.colorTemInKelvin == 0 && s.color != null && s.color.r == rgb.R && s.color.g == rgb.G && s.color.b == rgb.B
            };
        }
    }

    internal sealed class Lan : IDisposable, IGroupNetwork {
        private readonly LanReplies replies=new LanReplies();
        private long checkSent, nextCheck;
        private bool identitySeen, statusSeen;
        private int checkAttempt;
        public double StreamIntervalMs { get; private set; }
        private readonly DataContractJsonSerializer reader = new DataContractJsonSerializer(typeof(Envelope));

        public Lan() {
            // Limit streaming traffic to leave room for device status replies. Capture at 60 Hz
            // and limit continuous traffic to 30 packets/s, always using the latest frame.
            StreamIntervalMs = 1000.0/30;
            nextCheck = 2000;
        }
        public void Dispose() { replies.Dispose(); }
        private void Send(string cmd, object data, IPAddress address, int port) {
            var packet = new JavaScriptSerializer().Serialize(new { msg = new { cmd, data } });
            byte[] bytes = Encoding.UTF8.GetBytes(packet);
            replies.Send(bytes,new IPEndPoint(address, port));
        }
        private Message Receive(out IPEndPoint sender) {
            sender = new IPEndPoint(IPAddress.Any, 0);
            try {
                byte[] bytes = replies.Receive(out sender);
                if(bytes==null)return null;
                if (bytes.Length > 8192) return null;
                using (var stream = new MemoryStream(bytes)) {
                    var envelope = (Envelope)reader.ReadObject(stream);
                    return envelope != null && envelope.msg != null && envelope.msg.data != null ? envelope.msg : null;
                }
            } catch (SerializationException) { return null; }
              catch (System.Xml.XmlException) { return null; }
              catch (SocketException e) {
                  if (e.SocketErrorCode == SocketError.TimedOut || e.SocketErrorCode == SocketError.ConnectionReset) return null;
                  throw;
              }
        }
        private void RequestScan() {
            replies.Scan(Encoding.ASCII.GetBytes("{\"msg\":{\"cmd\":\"scan\",\"data\":{\"account_topic\":\"reserve\"}}}"));
        }
        public List<Device> Discover(string targetId = null) {
            replies.Clear();
            var found = new Dictionary<string,Device>();
            for (int attempt = 0; attempt < 3; attempt++) {
                RequestScan();
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < 650) {
                    IPEndPoint sender;
                    var msg = Receive(out sender);
                    if (msg == null || msg.cmd != "scan" || LightModel.Find(msg.data.sku)==null || !Device.ValidId(msg.data.device)) continue;
                    string id=msg.data.device.ToUpperInvariant();
                    found[id] = new Device { Id=id, Sku=msg.data.sku.ToUpperInvariant(), Address=sender.Address };
                    if (id == targetId) return new List<Device> { found[targetId] };
                }
            }
            return found.Values.OrderBy(x => x.Id).ToList();
        }
        public DeviceData Read(Device light, Func<DeviceData,bool> expected = null) {
            replies.Clear();
            DeviceData last = null;
            for (int attempt = 0; attempt < 4; attempt++) {
                Send("devStatus", new {}, light.Address, 4003);
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < 450) {
                    IPEndPoint sender;
                    var msg = Receive(out sender);
                    if (msg == null || !sender.Address.Equals(light.Address) || msg.cmd != "devStatus" || !msg.data.ValidStatus) continue;
                    last = msg.data;
                    if (expected == null || expected(last)) return last;
                }
            }
            if (last != null) throw new IOException("Light reports " + last.Describe() + ". The requested setting could not be confirmed.");
            throw new IOException("No status reply from the light. Check its power and LAN Control, then choose Refresh.");
        }
        public DeviceData Change(Device selected, Setting setting) {
            var light = Discover(selected.Id).FirstOrDefault(d => d.Id == selected.Id);
            if (light == null) throw new IOException("The selected light is offline. Check its connection, then choose Refresh.");
            Send(setting.Command, setting.Payload, light.Address, 4003);
            Thread.Sleep(160);
            return Read(light, setting.Matches);
        }
        // Caller must resolve identity and capabilities first. Used by a rate-limited
        // manual gesture; its final value is read back after the user pauses.
        public void SendSetting(Device light,Setting setting) {
            Send(setting.Command,setting.Payload,light.Address,4003);
        }
        public void StreamPacket(Device light, string packet) {
            // Packet is base64 generated by the local encoder; its alphabet needs no JSON escaping.
            byte[] bytes = Encoding.ASCII.GetBytes("{\"msg\":{\"cmd\":\"razer\",\"data\":{\"pt\":\""+packet+"\"}}}");
            replies.Send(bytes,new IPEndPoint(light.Address,4003));
        }
        private int healthIndex;
        public bool PollGroupHealth(Device[] group, long now) {
            Device light=group[healthIndex];
            // Run on the frame thread, but only read packets already in the socket.
            if (checkAttempt != 0) {
                for (int i=0; i<12 && replies.Available; i++) {
                    IPEndPoint from;
                    var msg=Receive(out from);
                    if (msg==null) continue;
                    if (msg.cmd=="scan" && String.Equals(msg.data.device,light.Id,StringComparison.OrdinalIgnoreCase)) {
                        if (!from.Address.Equals(light.Address)) throw new IOException("A light changed its network address. Refresh to reconnect.");
                        identitySeen=true;
                    }
                    if (!from.Address.Equals(light.Address)) continue;
                    if (msg.cmd=="scan" && !String.Equals(msg.data.device,light.Id,StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The device at this address changed. Refresh to reconnect.");
                    if (msg.cmd=="devStatus" && msg.data.ValidStatus) {
                        if (msg.data.onOff==0) return false;
                        statusSeen=true;
                        if (now-checkSent > 250) StreamIntervalMs=Math.Max(StreamIntervalMs,1000.0/25);
                    }
                }
                if (statusSeen && identitySeen) { checkAttempt=0; nextCheck=now+Math.Max(250,3500/group.Length); healthIndex=(healthIndex+1)%group.Length; return true; }
                if (now-checkSent < 550) return true;
                if (checkAttempt>=3) throw new IOException("A light in the sync group stopped responding. Refresh to reconnect.");
                StreamIntervalMs=1000.0/20;
            } else if (now<nextCheck) return true;
            if (checkAttempt==0) { replies.Clear();identitySeen=false;statusSeen=false; }
            checkAttempt++; checkSent=now;
            RequestScan();
            Send("devStatus", new {}, light.Address,4003);
            return true;
        }
    }
}
