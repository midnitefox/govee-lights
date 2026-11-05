using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GoveeLights;
internal static class VerifyReplies {
    public static int Main(string[] args) {
        try {
            using(var tx=new UdpClient())using(var a=new LanReplies())using(var b=new LanReplies()) {
                for(int i=0;i<20;i++) {
                    var data=Encoding.ASCII.GetBytes("reply-test-"+i);
                    tx.Send(data,data.Length,new IPEndPoint(IPAddress.Loopback,4002));
                    foreach(var rx in new[]{a,b}) {
                        IPEndPoint from;var got=rx.Receive(out from);
                        if(got==null || !data.SequenceEqual(got))throw new Exception("Reply lost between subscribers");
                    }
                }
                a.Dispose();byte[] end=Encoding.ASCII.GetBytes("last");
                tx.Send(end,end.Length,new IPEndPoint(IPAddress.Loopback,4002));
                IPEndPoint sender;
                if(!end.SequenceEqual(b.Receive(out sender)))throw new Exception("Disposal affected another subscriber");
            }
            using(var reopen=new LanReplies()){}
            Console.WriteLine("Shared reply routing and subscriber disposal passed.");
            if(args.Contains("--live")) {
                var checks=new[]{Task.Run(()=>{using(var a=new Lan()){var d=a.Discover();return d.Count>0 && a.Read(d[0]).ValidStatus;}}),
                    Task.Run(()=>{using(var b=new Lan()){var d=b.Discover();return d.Count>0 && b.Read(d[0]).ValidStatus;}})};
                Task.WaitAll(checks);if(checks.Any(x=>!x.Result))throw new Exception("Concurrent read failed");
                Console.WriteLine("Two concurrent Govee discoveries and status reads passed.");
            }
            return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
