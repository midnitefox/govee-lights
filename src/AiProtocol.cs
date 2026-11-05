using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace GoveeLights {
    internal static class AiWire {
        internal static string PipeName {get{return "GoveeLights-AI-"+WindowsIdentity.GetCurrent().User.Value;}}
        internal static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=32768,RecursionLimit=24};}
        internal static async Task<string> ReadLine(TextReader reader) {
            var text=new StringBuilder();var one=new char[1];
            while(await reader.ReadAsync(one,0,1)>0) {
                if(one[0]=='\n')return text.ToString();if(one[0]!='\r')text.Append(one[0]);
                if(text.Length>32768)throw new IOException("Request is too large.");
            }
            return text.Length==0?null:text.ToString();
        }
        internal static async Task<object> Request(string action,Dictionary<string,object> args,string pipeName=null) {
            using(var pipe=new NamedPipeClientStream(".",pipeName??PipeName,PipeDirection.InOut,PipeOptions.Asynchronous)) {
                using(var deadline=new CancellationTokenSource(45000))
                using(deadline.Token.Register(()=>pipe.Dispose())) {
                    try{await Task.Run(()=>pipe.Connect(1500));}
                    catch(TimeoutException){throw new IOException("Cannot reach Govee Lights. Open the app, enable AI apps in Settings, or wait if it is busy.");}
                    var writer=new StreamWriter(pipe,new UTF8Encoding(false),1024,true){AutoFlush=true};
                    var reader=new StreamReader(pipe,Encoding.UTF8,false,1024,true);
                    await writer.WriteLineAsync(Json().Serialize(new{action,arguments=args}));
                    string line=await ReadLine(reader);if(line==null)throw new IOException("The app closed the connection.");
                    var response=Json().Deserialize<Dictionary<string,object>>(line);
                    if(response.ContainsKey("error"))throw new IOException(Convert.ToString(response["error"]));
                    return response["result"];
                }
            }
        }
    }
    // Only this Windows account can connect. No TCP listener, router rule, or cloud relay.
    internal sealed class AiPipeServer : IDisposable {
        private readonly Func<string,Dictionary<string,object>,Task<object>> handle;
        private readonly string name;
        private readonly CancellationTokenSource stopped=new CancellationTokenSource();
        private readonly object gate=new object();
        private readonly HashSet<NamedPipeServerStream> connections=new HashSet<NamedPipeServerStream>();
        private readonly SemaphoreSlim slots=new SemaphoreSlim(4,4);
        internal AiPipeServer(Func<string,Dictionary<string,object>,Task<object>> handler,string pipeName=null) {
            handle=handler;name=pipeName??AiWire.PipeName;Task.Run((Func<Task>)Listen);
        }
        private async Task Listen() {
            while(!stopped.IsCancellationRequested) {
                NamedPipeServerStream pipe=null;bool acquired=false,handedOff=false;
                try {
                    await slots.WaitAsync(stopped.Token);acquired=true;
                    var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
                    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
                    security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
                    pipe=new NamedPipeServerStream(name,PipeDirection.InOut,4,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,security);
                    lock(gate){if(stopped.IsCancellationRequested)return;connections.Add(pipe);}
                    await pipe.WaitForConnectionAsync(stopped.Token);
                    StartClient(pipe);handedOff=true;
                }catch(OperationCanceledException){break;}catch(ObjectDisposedException){if(stopped.IsCancellationRequested)break;}
                catch(IOException){}
                catch(UnauthorizedAccessException){break;}
                finally{if(!handedOff){if(pipe!=null){lock(gate)connections.Remove(pipe);pipe.Dispose();}if(acquired)slots.Release();}}
                if(!handedOff && !stopped.IsCancellationRequested)await Task.Delay(200);
            }
        }
        private async void StartClient(NamedPipeServerStream pipe) {
            try {
                var reader=new StreamReader(pipe,Encoding.UTF8,false,1024,true);
                var writer=new StreamWriter(pipe,new UTF8Encoding(false),1024,true){AutoFlush=true};object response;
                try {
                    var input=AiWire.ReadLine(reader);
                    if(await Task.WhenAny(input,Task.Delay(5000,stopped.Token))!=input)return;
                    string line=await input;if(line==null)return;
                    var message=AiWire.Json().Deserialize<Dictionary<string,object>>(line);object action,data;
                    if(message==null || !message.TryGetValue("action",out action) || !(action is string))throw new IOException("A valid action is required.");
                    var args=message.TryGetValue("arguments",out data)?data as Dictionary<string,object>:new Dictionary<string,object>();
                    if(args==null)throw new IOException("Arguments must be an object.");
                    response=new{result=await handle((string)action,args)};
                }catch(Exception ex){response=new{error=ex.Message};}
                await writer.WriteLineAsync(AiWire.Json().Serialize(response));
            }catch(IOException){}catch(ObjectDisposedException){}
            finally{lock(gate)connections.Remove(pipe);pipe.Dispose();slots.Release();}
        }
        public void Dispose(){stopped.Cancel();lock(gate){foreach(var pipe in connections)pipe.Dispose();}}
    }
}
