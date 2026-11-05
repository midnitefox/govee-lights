using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace GoveeLights {
    // Govee directs every reply to port 4002. Each operation gets its own bounded inbox
    // so a room command cannot consume the screen-sync worker's status or discovery reply.
    internal sealed class LanReplies : IDisposable {
        private sealed class Packet {public byte[] Data; public IPEndPoint From;}
        private sealed class Hub {
            public readonly UdpClient Socket=new UdpClient(AddressFamily.InterNetwork);
            public readonly List<IPAddress> Adapters=new List<IPAddress>();
            public readonly List<LanReplies> Clients=new List<LanReplies>();
            public volatile bool Closed;
            public Exception Error;
            public Hub() {
                try {
                    Socket.ExclusiveAddressUse=true;Socket.Client.Bind(new IPEndPoint(IPAddress.Any,4002));
                    foreach(var nic in NetworkInterface.GetAllNetworkInterfaces()) {
                        if(nic.OperationalStatus!=OperationalStatus.Up || !nic.SupportsMulticast || nic.NetworkInterfaceType==NetworkInterfaceType.Loopback)continue;
                        foreach(var ip in nic.GetIPProperties().UnicastAddresses) {
                            if(ip.Address.AddressFamily!=AddressFamily.InterNetwork)continue;
                            try {Socket.JoinMulticastGroup(Group,ip.Address);Adapters.Add(ip.Address);}catch(SocketException){}
                        }
                    }
                    if(Adapters.Count==0)throw new IOException("Connect to your home network and search again.");
                    new Thread(Receive){IsBackground=true,Name="Govee LAN replies"}.Start();
                }catch(SocketException e){Socket.Close();throw new IOException("Govee's reply port is unavailable. Close any other light controller and try again.",e);}
                catch {Socket.Close();throw;}
            }
            private void Receive() {
                try {
                    while(!Closed) {
                        IPEndPoint from=new IPEndPoint(IPAddress.Any,0);byte[] bytes;
                        try {bytes=Socket.Receive(ref from);} catch(SocketException e) {
                            if(!Closed && e.SocketErrorCode==SocketError.ConnectionReset)continue;throw;
                        }
                        if(bytes.Length>8192)continue;
                        var packet=new Packet{Data=bytes,From=from};
                        lock(Gate) {
                            foreach(var c in Clients){if(c.inbox.Count>=128)c.inbox.Dequeue();c.inbox.Enqueue(packet);}
                            Monitor.PulseAll(Gate);
                        }
                    }
                }catch(Exception e){lock(Gate){if(!Closed)Error=e;Monitor.PulseAll(Gate);}}
            }
        }
        private static readonly IPAddress Group=IPAddress.Parse("239.255.255.250");
        private static readonly object Gate=new object();
        private static Hub shared;
        private readonly Hub hub;
        private readonly Queue<Packet> inbox=new Queue<Packet>();
        private bool disposed;
        public LanReplies() {lock(Gate){hub=shared??(shared=new Hub());hub.Clients.Add(this);}}
        public bool Available {get{lock(Gate){Check();return inbox.Count!=0;}}}
        public void Clear() {lock(Gate){Check();inbox.Clear();}}
        private void Check() {
            if(disposed)throw new ObjectDisposedException("LAN operation");
            if(hub.Error!=null)throw new IOException("The network connection was interrupted.",hub.Error);
        }
        public byte[] Receive(out IPEndPoint from) {
            lock(Gate){
                Check();if(inbox.Count==0)Monitor.Wait(Gate,120);Check();
                if(inbox.Count==0){from=null;return null;}
                var p=inbox.Dequeue();from=p.From;return p.Data;
            }
        }
        public void Send(byte[] bytes,IPEndPoint to) {
            lock(Gate){Check();hub.Socket.Send(bytes,bytes.Length,to);}
        }
        public void Scan(byte[] bytes) {
            lock(Gate){
                Check();int sent=0;
                foreach(var ip in hub.Adapters)try {
                    hub.Socket.Client.SetSocketOption(SocketOptionLevel.IP,SocketOptionName.MulticastInterface,ip.GetAddressBytes());
                    hub.Socket.Send(bytes,bytes.Length,new IPEndPoint(Group,4001));sent++;
                }catch(SocketException){}
                if(sent==0)throw new IOException("Discovery could not reach a network adapter.");
            }
        }
        public void Dispose() {
            lock(Gate){if(disposed)return;disposed=true;hub.Clients.Remove(this);inbox.Clear();
                if(hub.Clients.Count==0){hub.Closed=true;hub.Socket.Close();shared=null;}Monitor.PulseAll(Gate);}
        }
    }
}
