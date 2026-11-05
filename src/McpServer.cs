using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GoveeLights {
    internal static class McpProgram {
        internal const string Version="1.0.0";
        internal static readonly string[] SignalKinds={"complete","attention","error","information"};
        private static object StringSchema(string description){return new{type="string",description};}
        private static object Tool(string name,string description,Dictionary<string,object> properties,string[] required,bool read=false) {
            return new{name="govee_"+name,description,inputSchema=new{type="object",properties,required,additionalProperties=false},
                annotations=new{readOnlyHint=read,destructiveHint=false,idempotentHint=read,openWorldHint=false}};
        }
        internal static object[] ToolList() {
            return new[]{
                Tool("get_status","Read the local Govee Lights app's AI connection, sync, and notification status.",new Dictionary<string,object>(),new string[0],true),
                Tool("list_lights","List saved lights explicitly allowed for AI control in the app. IDs come from this tool; never guess them.",new Dictionary<string,object>(),new string[0],true),
                Tool("get_light_state","Read a permitted light's current reported power, brightness, RGB and white temperature over the LAN.",new Dictionary<string,object>{{"light_id",StringSchema("Exact ID from govee_list_lights")}},new[]{"light_id"},true),
                Tool("set_light","Set power, brightness, RGB color or white warmth for one allowed light. Stop screen sync first if that light is syncing. Changes persist.",new Dictionary<string,object>{
                    {"light_id",StringSchema("Exact ID from govee_list_lights")},{"power",new{type="boolean"}},
                    {"brightness",new{type="integer",minimum=1,maximum=100}},{"color",new{type="string",pattern="^#[0-9a-fA-F]{6}$"}},
                    {"kelvin",new{type="integer",minimum=2000,maximum=9000}}},new[]{"light_id"}),
                Tool("list_presets","List saved presets whose lights are all allowed for AI access.",new Dictionary<string,object>(),new string[0],true),
                Tool("apply_preset","Apply a saved lighting preset. Requires all preset lights online, allowed, and outside the active sync group.",new Dictionary<string,object>{{"preset_id",StringSchema("ID from govee_list_presets")}},new[]{"preset_id"}),
                Tool("notify","Send a brief completion, attention, error, or information signal, then restore prior lighting and resume interrupted sync. Off lights remain off unless the app's wake option is enabled. Use one event_id for retries of the same signal.",new Dictionary<string,object>{
                    {"kind",new{type="string",@enum=SignalKinds}},{"light_ids",new{type="array",items=new{type="string"},minItems=1,maxItems=32,uniqueItems=true}},
                    {"event_id",new{type="string",maxLength=128,description="Optional unique event ID to prevent duplicate signals"}}},new[]{"kind"}),
                Tool("stop_notification","End a running light signal early and restore the previous lighting.",new Dictionary<string,object>(),new string[0]),
                Tool("screen_sync","Start or stop the user's selected screen-sync group. Starting captures the primary screen locally and can turn on the selected lights. Every selected light must be allowed for AI control.",new Dictionary<string,object>{{"action",new{type="string",@enum=new[]{"start","stop"}}}},new[]{"action"})
            };
        }
        private static object Error(object id,int code,string message){return new{jsonrpc="2.0",id,error=new{code,message}};}
        internal static async Task<object> Handle(Dictionary<string,object> request,Func<string,Dictionary<string,object>,Task<object>> call) {
            object id,rawMethod;request.TryGetValue("id",out id);
            if(!request.TryGetValue("method",out rawMethod) || !(rawMethod is string))return Error(id,-32600,"Invalid request");
            string method=(string)rawMethod;if(!request.ContainsKey("id"))return null;
            object result;
            switch(method) {
                case "initialize":result=new{protocolVersion="2025-11-25",capabilities=new{tools=new{}},serverInfo=new{name="govee-lights",version=Version},
                    instructions="Controls only the running local Windows app and its explicitly allowed lights. Use govee_notify for temporary AI signals. Never promise an action until its tool result confirms it."};break;
                case "ping":result=new{};break;
                case "tools/list":result=new{tools=ToolList()};break;
                case "tools/call": {
                    object raw;var p=request.TryGetValue("params",out raw)?raw as Dictionary<string,object>:null;
                    object toolName,arguments;if(p==null || !p.TryGetValue("name",out toolName) || !(toolName is string))return Error(id,-32602,"Tool name is required");
                    string name=(string)toolName;var definitions=AiWire.Json().Deserialize<List<Dictionary<string,object>>>(AiWire.Json().Serialize(ToolList()));
                    var definition=definitions.FirstOrDefault(x=>(string)x["name"]==name);if(definition==null)return Error(id,-32602,"Unknown tool");
                    var args=p.TryGetValue("arguments",out arguments)?arguments as Dictionary<string,object>:new Dictionary<string,object>();
                    if(args==null)return Error(id,-32602,"Arguments must be an object");
                    var schema=(Dictionary<string,object>)definition["inputSchema"];var props=(Dictionary<string,object>)schema["properties"];
                    var required=((IEnumerable)schema["required"]).Cast<string>();
                    if(args.Keys.Any(k=>!props.ContainsKey(k)) || required.Any(k=>!args.ContainsKey(k)))return Error(id,-32602,"Missing or unsupported arguments");
                    try {
                        object data=await call(name.Substring(6),args);string text=AiWire.Json().Serialize(data);
                        result=new {content=new[]{new{type="text",text}},structuredContent=new{result=data},isError=false};
                    }catch(Exception ex){result=new{content=new[]{new{type="text",text=ex.Message}},isError=true};}
                    break;
                }
                default:return Error(id,-32601,"Method not found");
            }
            return new{jsonrpc="2.0",id,result};
        }
        private static int Main(string[] args) {
            Console.InputEncoding=new UTF8Encoding(false);Console.OutputEncoding=new UTF8Encoding(false);
            if(args.Length!=0){Console.Error.WriteLine("Run without arguments as an MCP stdio server.");return 2;}
            try {
                var pending=new List<Task>();var outputGate=new object();var capacity=new SemaphoreSlim(16,16);
                while(true) {
                    string line=AiWire.ReadLine(Console.In).GetAwaiter().GetResult();if(line==null)break;
                    object response;
                    try {
                        var request=AiWire.Json().Deserialize<Dictionary<string,object>>(line);
                        if(request!=null) {
                            object id;request.TryGetValue("id",out id);
                            if(!capacity.Wait(0)){response=Error(id,-32000,"Too many pending requests");}
                            else {
                                pending.RemoveAll(t=>t.IsCompleted);
                                pending.Add(Task.Run(async()=>{
                                    try {
                                        object reply=await Handle(request,(action,input)=>AiWire.Request(action,input));
                                        if(reply!=null)lock(outputGate){Console.WriteLine(AiWire.Json().Serialize(reply));Console.Out.Flush();}
                                    }catch(Exception ex){lock(outputGate){Console.WriteLine(AiWire.Json().Serialize(Error(id,-32603,ex.Message)));Console.Out.Flush();}}
                                    finally{capacity.Release();}
                                }));continue;
                            }
                        }else response=Error(null,-32600,"Invalid request");
                    }catch(ArgumentException){response=Error(null,-32700,"Parse error");}
                    catch(InvalidOperationException){response=Error(null,-32600,"Invalid request");}
                    if(response!=null)lock(outputGate){Console.WriteLine(AiWire.Json().Serialize(response));Console.Out.Flush();}
                }
                Task.WhenAll(pending).GetAwaiter().GetResult();
                return 0;
            }catch(IOException){return 1;}
        }
    }
}
