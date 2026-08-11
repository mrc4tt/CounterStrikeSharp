using System;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace CounterStrikeSharp.API.Core
{
    /// <summary>
    /// Managed -> native entry points. Every method follows the same shape:
    /// grab the per-thread <see cref="ScriptContext.GlobalScriptContext"/>, reset it,
    /// push arguments, set the native id, invoke, read the result.
    ///
    /// PERF: these bodies used to open with
    ///   <c>lock (ScriptContext.GlobalScriptContext.Lock) { ... }</c>
    /// and then re-read the <c>GlobalScriptContext</c> property once per statement.
    /// Both were pure overhead:
    ///   * <c>GlobalScriptContext</c> is <c>[ThreadStatic]</c>, so the monitor being
    ///     taken belongs to the calling thread's own context object. No other thread
    ///     can ever contend it, and Monitor is reentrant, so a nested native call on
    ///     the same thread walked straight through. The lock guarded nothing while
    ///     costing an interlocked acquire + release on EVERY native call.
    ///   * Each <c>ScriptContext.GlobalScriptContext</c> read is a thread-static
    ///     lookup; a 6-statement native call did six of them.
    /// Both collapse to one TLS read into the <c>_ctx</c> local. Cross-thread native
    /// calls were never safe here (each thread gets its own context, and the engine
    /// requires game-thread access) -- the lock did not make them safe, so removing it
    /// changes no guarantee that actually held.
    /// </summary>
    public class NativeAPI {

        public static bool AddListener(string name, InputArgument callback){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.Push((InputArgument)callback);
			_ctx.SetIdentifier(0x8E7D0305);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static bool RemoveListener(string name, InputArgument callback){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.Push((InputArgument)callback);
			_ctx.SetIdentifier(0x47C507A2);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static void AddCommand(string name, string description, bool serveronly, int flags, InputArgument callback){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.PushString(description);
			_ctx.PushPrimitive(serveronly);
			_ctx.PushPrimitive(flags);
			_ctx.Push((InputArgument)callback);
			_ctx.SetIdentifier(0x807C6B9C);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void RemoveCommand(string name, InputArgument callback){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.Push((InputArgument)callback);
			_ctx.SetIdentifier(0xEC2412DB);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void AddCommandListener(string cmd, InputArgument callback, bool post){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(cmd);
			_ctx.Push((InputArgument)callback);
			_ctx.PushPrimitive(post);
			_ctx.SetIdentifier(0x2D2D803D);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void RemoveCommandListener(string cmd, InputArgument callback, bool post){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(cmd);
			_ctx.Push((InputArgument)callback);
			_ctx.PushPrimitive(post);
			_ctx.SetIdentifier(0x34DBBF1A);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static int CommandGetArgCount(IntPtr command){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(command);
			_ctx.SetIdentifier(0xAD28109C);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<int>();
		}

        public static string CommandGetArgString(IntPtr command){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(command);
			_ctx.SetIdentifier(0x2E52E8EA);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static string CommandGetCommandString(IntPtr command){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(command);
			_ctx.SetIdentifier(0x8FABC059);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static string CommandGetArgByIndex(IntPtr command, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(command);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0x3E8D9805);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static CommandCallingContext CommandGetCallingContext(IntPtr command){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(command);
			_ctx.SetIdentifier(0x886D0EB6);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<CommandCallingContext>();
		}

        public static void IssueClientCommand(int slot, string command){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(slot);
			_ctx.PushString(command);
			_ctx.SetIdentifier(0xCA5BA982);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void IssueClientCommandFromServer(int slot, string command){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(slot);
			_ctx.PushString(command);
			_ctx.SetIdentifier(0x85376751);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static IntPtr FindConvar(string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.SetIdentifier(0x52254718);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static void SetConvarStringValue(IntPtr convar, string value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(convar);
			_ctx.PushString(value);
			_ctx.SetIdentifier(0x9A736FC1);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static string GetClientConvarValue(int clientindex, string convarname){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(clientindex);
			_ctx.PushString(convarname);
			_ctx.SetIdentifier(0xAE4B1B79);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static void SetFakeClientConvarValue(int clientindex, string convarname, string convarvalue){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(clientindex);
			_ctx.PushString(convarname);
			_ctx.PushString(convarvalue);
			_ctx.SetIdentifier(0x4C61E8BB);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void ReplicateConvar(int clientslot, string convarname, string convarvalue){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(clientslot);
			_ctx.PushString(convarname);
			_ctx.PushString(convarvalue);
			_ctx.SetIdentifier(0xC8728BEC);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void SetConvarFlags(ushort convar, ulong flags){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(convar);
			_ctx.PushPrimitive(flags);
			_ctx.SetIdentifier(0xB2BDCCBF);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static ulong GetConvarFlags(ushort convar){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(convar);
			_ctx.SetIdentifier(0x94829E2B);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<ulong>();
		}

        public static short GetConvarType(ushort convar){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(convar);
			_ctx.SetIdentifier(0xB6E0E54C);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<short>();
		}

        public static string GetConvarName(ushort convar){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(convar);
			_ctx.SetIdentifier(0xB6F0E2F3);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static string GetConvarHelpText(ushort convar){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(convar);
			_ctx.SetIdentifier(0x341D1F67);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static ushort GetConvarAccessIndexByName(string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.SetIdentifier(0x6288420D);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<ushort>();
		}

        public static T GetConvarValue<T>(ushort convar){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(convar);
			_ctx.SetIdentifier(0x935B2E9F);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return (T)_ctx.GetResult(typeof(T));
		}

        public static string GetConvarValueAsString(ushort convar){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(convar);
			_ctx.SetIdentifier(0x5CC184F8);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static IntPtr GetConvarValueAddress(ushort convar){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(convar);
			_ctx.SetIdentifier(0xECC4CC16);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static void SetConvarValueAsString(ushort convar, string value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(convar);
			_ctx.PushString(value);
			_ctx.SetIdentifier(0x5EF52D6C);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void SetConvarValue<T>(ushort convar, T value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(convar);
			_ctx.Push(value);
			_ctx.SetIdentifier(0xB3DDAA0B);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static ushort CreateConvar<T>(string name, short type, string helptext, ulong flags, bool hasmin, bool hasmax, T defaultvalue, T minvalue, T maxvalue){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.PushPrimitive(type);
			_ctx.PushString(helptext);
			_ctx.PushPrimitive(flags);
			_ctx.PushPrimitive(hasmin);
			_ctx.PushPrimitive(hasmax);
			_ctx.Push(defaultvalue);
			_ctx.Push(minvalue);
			_ctx.Push(maxvalue);
			_ctx.SetIdentifier(0xF22079B9);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<ushort>();
		}

        public static void DeleteConvar(ushort convar){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(convar);
			_ctx.SetIdentifier(0xFC28F444);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static string GetStringFromSymbolLarge(IntPtr pointer){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pointer);
			_ctx.SetIdentifier(0x600A804B);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static uint GetVariantType(IntPtr pvariant){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pvariant);
			_ctx.SetIdentifier(0x7AC3DA1C);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<uint>();
		}

        public static int GetVariantInt(IntPtr pvariant){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pvariant);
			_ctx.SetIdentifier(0x78156617);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<int>();
		}

        public static uint GetVariantUint(IntPtr pvariant){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pvariant);
			_ctx.SetIdentifier(0x7AC49FA2);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<uint>();
		}

        public static float GetVariantFloat(IntPtr pvariant){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pvariant);
			_ctx.SetIdentifier(0xD20595B4);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<float>();
		}

        public static string GetVariantString(IntPtr pvariant){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pvariant);
			_ctx.SetIdentifier(0x41C49F71);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static bool GetVariantBool(IntPtr pvariant){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pvariant);
			_ctx.SetIdentifier(0x7ABC76EA);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static void SetVariantInt(IntPtr pvariant, int value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pvariant);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0x801EC403);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void SetVariantUint(IntPtr pvariant, uint value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pvariant);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0x83EC7436);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void SetVariantFloat(IntPtr pvariant, float value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pvariant);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0x266E8A0);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void SetVariantString(IntPtr pvariant, string value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pvariant);
			_ctx.PushString(value);
			_ctx.SetIdentifier(0x2450A3E5);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void SetVariantBool(IntPtr pvariant, bool value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pvariant);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0x83F1967E);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static T DynamicHookGetReturn<T>(IntPtr hook, int datatype){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(hook);
			_ctx.PushPrimitive(datatype);
			_ctx.SetIdentifier(0x4F5B80D0);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return (T)_ctx.GetResult(typeof(T));
		}

        public static void DynamicHookSetReturn<T>(IntPtr hook, int datatype, T value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(hook);
			_ctx.PushPrimitive(datatype);
			_ctx.Push(value);
			_ctx.SetIdentifier(0xDB297E44);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static T DynamicHookGetParam<T>(IntPtr hook, int datatype, int paramindex){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(hook);
			_ctx.PushPrimitive(datatype);
			_ctx.PushPrimitive(paramindex);
			_ctx.SetIdentifier(0x5F5ABDD5);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return (T)_ctx.GetResult(typeof(T));
		}

        public static void DynamicHookSetParam<T>(IntPtr hook, int datatype, int paramindex, T value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(hook);
			_ctx.PushPrimitive(datatype);
			_ctx.PushPrimitive(paramindex);
			_ctx.Push(value);
			_ctx.SetIdentifier(0xA96CFBC1);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static string GetMapName(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0x43C2ED68);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static string GetGameDirectory(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0xD8F03FD4);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static bool IsMapValid(string mapname){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(mapname);
			_ctx.SetIdentifier(0xD88A5CD5);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static float GetTickInterval(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0x970CB1B9);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<float>();
		}

        public static float GetCurrentTime(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0xFDF24F);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<float>();
		}

        public static int GetTickCount(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0xAB744EC5);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<int>();
		}

        public static double GetEngineTime(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0x39A17C88);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<double>();
		}

        public static int GetMaxClients(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0x5DF2E20D);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<int>();
		}

        public static float GetGameFrameTime(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0x97E331CA);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<float>();
		}

        public static void IssueServerCommand(string command){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(command);
			_ctx.SetIdentifier(0xA5901A5E);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void PrecacheModel(string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.SetIdentifier(0x77A0C6BE);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void AddResource(string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.SetIdentifier(0x3B1DC491);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static bool PrecacheSound(string name, bool preload){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.PushPrimitive(preload);
			_ctx.SetIdentifier(0x758F3FD2);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static bool IsSoundPrecached(string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.SetIdentifier(0xD4372AF3);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static float GetSoundDuration(string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.SetIdentifier(0x20BB05CE);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<float>();
		}

        public static IntPtr CreateRay1(int rayType, IntPtr vec1, IntPtr vec2){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(rayType);
			_ctx.PushPrimitive(vec1);
			_ctx.PushPrimitive(vec2);
			_ctx.SetIdentifier(0x7A3E109A);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static IntPtr CreateRay2(IntPtr vec1, IntPtr vec2, IntPtr vec3, IntPtr vec4){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vec1);
			_ctx.PushPrimitive(vec2);
			_ctx.PushPrimitive(vec3);
			_ctx.PushPrimitive(vec4);
			_ctx.SetIdentifier(0x7A3E1099);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static void TraceRay(IntPtr ray, IntPtr ptrace, IntPtr traceFilter, uint flags){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(ray);
			_ctx.PushPrimitive(ptrace);
			_ctx.PushPrimitive(traceFilter);
			_ctx.PushPrimitive(flags);
			_ctx.SetIdentifier(0x35182751);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static IntPtr NewSimpleTraceFilter(int indexToIgnore){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(indexToIgnore);
			_ctx.SetIdentifier(0xC3572E09);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static IntPtr NewTraceFilterProxy(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0x881F122B);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static void TraceFilterProxySetTraceTypeCallback(IntPtr traceFilter, IntPtr callback){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(traceFilter);
			_ctx.PushPrimitive(callback);
			_ctx.SetIdentifier(0xE907BCBA);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void TraceFilterProxySetShouldHitEntityCallback(IntPtr traceFilter, IntPtr callback){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(traceFilter);
			_ctx.PushPrimitive(callback);
			_ctx.SetIdentifier(0x3858171B);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static IntPtr NewTraceResult(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0x95B04711);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static double GetTickedTime(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0x84108452);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<double>();
		}

        public static void QueueTaskForFrame(int tick, InputArgument callback){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(tick);
			_ctx.Push((InputArgument)callback);
			_ctx.SetIdentifier(0x2F92C340);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static IntPtr GetValveInterface(int interfacetype, string interfacename){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(interfacetype);
			_ctx.PushString(interfacename);
			_ctx.SetIdentifier(0xDFAED2BE);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static T GetCommandParamValue<T>(string param, DataType datatype, T defaultvalue){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(param);
			_ctx.PushPrimitive(datatype);
			_ctx.Push(defaultvalue);
			_ctx.SetIdentifier(0x748F302F);
			_ctx.Invoke();
			_ctx.CheckErrors();
		        return _ctx.GetResult<T>();
		}

        public static bool FindCommandLineParam(string param){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(param);
			_ctx.SetIdentifier(0x292DA159);
			_ctx.Invoke();
			_ctx.CheckErrors();
                        return _ctx.GetResultPrimitive<bool>();
		}

        public static string GetCommandLineParam(string param, string defaultvalue){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(param);
			_ctx.PushString(defaultvalue);
			_ctx.SetIdentifier(0xD0F293AA);
			_ctx.Invoke();
			_ctx.CheckErrors();
                        return _ctx.GetResultString();
		}

        public static string GetCommandLineString(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0xF0980C30);
			_ctx.Invoke();
			_ctx.CheckErrors();
                        return _ctx.GetResultString();
		}

        public static void PrintToServerConsole(string msg){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(msg);
			_ctx.SetIdentifier(0x5D4EE1C2);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        // Records the suspect plugin natively so the SIGABRT fatal handler can name it
        // as the last console line if a garbage-collected-delegate FailFast terminates
        // the process. Identifier = hash_string("SET_FATAL_SUSPECT_PLUGIN").
        public static void SetFatalSuspectPlugin(string pluginName){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(pluginName);
			_ctx.SetIdentifier(0x6C433298);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void DisconnectClient(int slot, int reason){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(slot);
			_ctx.PushPrimitive(reason);
			_ctx.SetIdentifier(0x799EE9C3);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void ClientPrint(int slot, int huddestination, string msg){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(slot);
			_ctx.PushPrimitive(huddestination);
			_ctx.PushString(msg);
			_ctx.SetIdentifier(0x8F03FA72);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static IntPtr GetEntityFromIndex(int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0xD551EB1F);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static int GetUseridFromIndex(int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0x83542138);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<int>();
		}

        public static string GetDesignerName(IntPtr pointer){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pointer);
			_ctx.SetIdentifier(0x28DCCD51);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static IntPtr GetEntityPointerFromHandle(IntPtr entityhandlepointer){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(entityhandlepointer);
			_ctx.SetIdentifier(0xEE3A8DEF);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static uint GetRefFromEntityPointer(IntPtr entitypointer){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(entitypointer);
			_ctx.SetIdentifier(0xAF13DA94);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<uint>();
		}

        public static IntPtr GetEntityPointerFromRef(uint entityref){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(entityref);
			_ctx.SetIdentifier(0xDBC17174);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static IntPtr GetConcreteEntityListPointer(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0x5756DB36);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static bool IsRefValidEntity(uint entityref){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(entityref);
			_ctx.SetIdentifier(0x6E38A1FC);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static void PrintToConsole(int index, string message){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(index);
			_ctx.PushString(message);
			_ctx.SetIdentifier(0x7F033898);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void TransmitSetHidden(int entityindex, int playerslot, bool hidden){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(entityindex);
			_ctx.PushPrimitive(playerslot);
			_ctx.PushPrimitive(hidden);
			_ctx.SetIdentifier(0x6648C4C7);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void TransmitSetHiddenAll(int entityindex, bool hidden){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(entityindex);
			_ctx.PushPrimitive(hidden);
			_ctx.SetIdentifier(0x33472679);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void TransmitClearEntity(int entityindex){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(entityindex);
			_ctx.SetIdentifier(0x57F9A9ED);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void TransmitClearPlayer(int playerslot){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(playerslot);
			_ctx.SetIdentifier(0x3943AC45);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void TransmitClearAll(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0xAA93A6D7);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static bool TransmitIsHidden(int entityindex, int playerslot){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(entityindex);
			_ctx.PushPrimitive(playerslot);
			_ctx.SetIdentifier(0x4342479F);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static IntPtr GetFirstActiveEntity(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0x3E50DC41);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static ulong GetPlayerAuthorizedSteamid(int slot){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(slot);
			_ctx.SetIdentifier(0xD1F30B3B);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<ulong>();
		}

        public static string GetPlayerIpAddress(int slot){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(slot);
			_ctx.SetIdentifier(0x46A45CB0);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static void HookEntityOutput(string classname, string outputname, InputArgument callback, HookMode mode){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(classname);
			_ctx.PushString(outputname);
			_ctx.Push((InputArgument)callback);
			_ctx.PushPrimitive(mode);
			_ctx.SetIdentifier(0x15245242);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void UnhookEntityOutput(string classname, string outputname, InputArgument callback, HookMode mode){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(classname);
			_ctx.PushString(outputname);
			_ctx.Push((InputArgument)callback);
			_ctx.PushPrimitive(mode);
			_ctx.SetIdentifier(0x87DBD139);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void AcceptInput(IntPtr pthis, string inputname, IntPtr activator, IntPtr caller, string value, int outputid){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pthis);
			_ctx.PushString(inputname);
			_ctx.PushPrimitive(activator);
			_ctx.PushPrimitive(caller);
			_ctx.PushString(value);
			_ctx.PushPrimitive(outputid);
			_ctx.SetIdentifier(0x259E084C);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void AddEntityIoEvent(IntPtr ptarget, string inputname, IntPtr activator, IntPtr caller, string value, float delay, int outputid){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(ptarget);
			_ctx.PushString(inputname);
			_ctx.PushPrimitive(activator);
			_ctx.PushPrimitive(caller);
			_ctx.PushString(value);
			_ctx.PushPrimitive(delay);
			_ctx.PushPrimitive(outputid);
			_ctx.SetIdentifier(0x4CFDE98A);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static uint EmitSoundFilter(ulong filtermask, uint ent, string sound, float volume, float pitch){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(filtermask);
			_ctx.PushPrimitive(ent);
			_ctx.PushString(sound);
			_ctx.PushPrimitive(volume);
			_ctx.PushPrimitive(pitch);
			_ctx.SetIdentifier(0x43C4A2B3);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<uint>();
		}

        public static void DispatchSpawn(IntPtr entity, IntPtr keyvalues){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(entity);
			_ctx.PushPrimitive(keyvalues);
			_ctx.SetIdentifier(0xAE01E931);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static IntPtr EntityKeyValuesNew(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0x445FE212);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static void EntityKeyValuesRelease(IntPtr keyvalues){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(keyvalues);
			_ctx.SetIdentifier(0xAE679E87);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static T EntityKeyValuesGetValue<T>(IntPtr keyvalues, string key, uint type){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(keyvalues);
			_ctx.PushString(key);
			_ctx.PushPrimitive(type);
			_ctx.SetIdentifier(0xA9A569AC);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return (T)_ctx.GetResult(typeof(T));
		}

        public static void EntityKeyValuesSetValue(IntPtr keyvalues, string key, uint type, object[] arguments){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(keyvalues);
			_ctx.PushString(key);
			_ctx.PushPrimitive(type);
			foreach (var obj in arguments)
			{
				_ctx.Push(obj);
			}
			_ctx.SetIdentifier(0x60234AB8);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static bool EntityKeyValuesHasValue(IntPtr keyvalues, string key){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(keyvalues);
			_ctx.PushString(key);
			_ctx.SetIdentifier(0xD3E04DA0);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static void HookEvent(string name, InputArgument callback, bool ispost){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.Push((InputArgument)callback);
			_ctx.PushPrimitive(ispost);
			_ctx.SetIdentifier(0xE71F04D5);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void UnhookEvent(string name, InputArgument callback, bool ispost){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.Push((InputArgument)callback);
			_ctx.PushPrimitive(ispost);
			_ctx.SetIdentifier(0x2154AFAE);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static IntPtr CreateEvent(string name, bool force){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.PushPrimitive(force);
			_ctx.SetIdentifier(0x7B472432);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static void FreeEvent(IntPtr gameevent){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.SetIdentifier(0x7E8B60C2);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void FireEvent(IntPtr gameevent, bool dontbroadcast){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushPrimitive(dontbroadcast);
			_ctx.SetIdentifier(0x2D52AEE);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void FireEventToClient(IntPtr gameevent, int clientindex){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushPrimitive(clientindex);
			_ctx.SetIdentifier(0x40B7C06C);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static string GetEventName(IntPtr gameevent){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.SetIdentifier(0xDFF86998);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static bool GetEventBool(IntPtr gameevent, string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushString(name);
			_ctx.SetIdentifier(0xDFFEE451);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static int GetEventInt(IntPtr gameevent, string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushString(name);
			_ctx.SetIdentifier(0xB17427CC);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<int>();
		}

        public static float GetEventFloat(IntPtr gameevent, string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushString(name);
			_ctx.SetIdentifier(0xDF96CB6F);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<float>();
		}

        public static string GetEventString(IntPtr gameevent, string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushString(name);
			_ctx.SetIdentifier(0xB4EBC50A);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static void SetEventBool(IntPtr gameevent, string name, bool value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushString(name);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0x31859DC5);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void SetEventFloat(IntPtr gameevent, string name, float value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushString(name);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0x627CF47B);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void SetEventString(IntPtr gameevent, string name, string value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushString(name);
			_ctx.PushString(value);
			_ctx.SetIdentifier(0xCB7E7B9E);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void SetEventInt(IntPtr gameevent, string name, int value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushString(name);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0x4F1363D8);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static int LoadEventsFromFile(string path, bool searchall){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(path);
			_ctx.PushPrimitive(searchall);
			_ctx.SetIdentifier(0xED480293);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<int>();
		}

        public static IntPtr GetEventPlayerController(IntPtr gameevent, string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushString(name);
			_ctx.SetIdentifier(0x88E33F2F);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static void SetEventPlayerController(IntPtr gameevent, string name, IntPtr value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushString(name);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0xE8A2033B);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void SetEventEntity(IntPtr gameevent, string name, IntPtr value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushString(name);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0xAB420F50);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void SetEventEntityIndex(IntPtr gameevent, string name, int value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushString(name);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0xAF9B1691);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static IntPtr GetEventPlayerPawn(IntPtr gameevent, string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushString(name);
			_ctx.SetIdentifier(0x80D3545B);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static ulong GetEventUint64(IntPtr gameevent, string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushString(name);
			_ctx.SetIdentifier(0xA5EADD5B);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<ulong>();
		}

        public static void SetEventUint64(IntPtr gameevent, string name, ulong value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(gameevent);
			_ctx.PushString(name);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0xD0C2D3CF);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static IntPtr CreateVirtualFunction(IntPtr pointer, int vtableoffset, int numarguments, int returntype, object[] arguments){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pointer);
			_ctx.PushPrimitive(vtableoffset);
			_ctx.PushPrimitive(numarguments);
			_ctx.PushPrimitive(returntype);
			foreach (var obj in arguments)
			{
				_ctx.Push(obj);
			}
			_ctx.SetIdentifier(0x2531DA2);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static IntPtr CreateVirtualFunctionBySignature(IntPtr pointer, string binaryname, string signature, int numarguments, int returntype, object[] arguments){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pointer);
			_ctx.PushString(binaryname);
			_ctx.PushString(signature);
			_ctx.PushPrimitive(numarguments);
			_ctx.PushPrimitive(returntype);
			foreach (var obj in arguments)
			{
				_ctx.Push(obj);
			}
			_ctx.SetIdentifier(0x8D25187D);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static IntPtr CreateVirtualFunctionBySymbol(string binaryname, string symbolname, int vtableoffset, int numarguments, int returntype, object[] arguments){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(binaryname);
			_ctx.PushString(symbolname);
			_ctx.PushPrimitive(vtableoffset);
			_ctx.PushPrimitive(numarguments);
			_ctx.PushPrimitive(returntype);
			foreach (var obj in arguments)
			{
				_ctx.Push(obj);
			}
			_ctx.SetIdentifier(0xF873189F);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static IntPtr CreateVirtualFunctionFromVTable(IntPtr pointer, int vtableoffset, int numarguments, int returntype, object[] arguments){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pointer);
			_ctx.PushPrimitive(vtableoffset);
			_ctx.PushPrimitive(numarguments);
			_ctx.PushPrimitive(returntype);
			foreach (var obj in arguments)
			{
				_ctx.Push(obj);
			}
			_ctx.SetIdentifier(0xE9D17E63);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static void HookFunction(IntPtr function, InputArgument hook, bool post){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(function);
			_ctx.Push((InputArgument)hook);
			_ctx.PushPrimitive(post);
			_ctx.SetIdentifier(0xA6C8BA9B);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void UnhookFunction(IntPtr function, InputArgument hook, bool post){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(function);
			_ctx.Push((InputArgument)hook);
			_ctx.PushPrimitive(post);
			_ctx.SetIdentifier(0x2051B00);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static T ExecuteVirtualFunction<T>(IntPtr function, bool bypass, object[] arguments){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(function);
			_ctx.PushPrimitive(bypass);
			foreach (var obj in arguments)
			{
				_ctx.Push(obj);
			}
			_ctx.SetIdentifier(0x376A0359);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return (T)_ctx.GetResult(typeof(T));
		}

        public static IntPtr FindSignature(string modulepath, string signature){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(modulepath);
			_ctx.PushString(signature);
			_ctx.SetIdentifier(0xE9E1819B);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static IntPtr FindVirtualTable(string modulepath, string vtablename){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(modulepath);
			_ctx.PushString(vtablename);
			_ctx.SetIdentifier(0xEA506CFF);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static int GetNetworkVectorSize(IntPtr vec){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vec);
			_ctx.SetIdentifier(0xA585F34E);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<int>();
		}

        public static IntPtr GetNetworkVectorElementAt(IntPtr vec, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vec);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0x67A31E3F);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static void RemoveAllNetworkVectorElements(IntPtr vec){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vec);
			_ctx.SetIdentifier(0x67206C08);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static IntPtr MetaFactory(string interfacename){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(interfacename);
			_ctx.SetIdentifier(0x61521EF3);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static void TraceShape(IntPtr startpos, IntPtr angles, IntPtr ignoreentity, ulong interactsas, ulong interactswith, ulong interactsexclude, IntPtr outresult){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(startpos);
			_ctx.PushPrimitive(angles);
			_ctx.PushPrimitive(ignoreentity);
			_ctx.PushPrimitive(interactsas);
			_ctx.PushPrimitive(interactswith);
			_ctx.PushPrimitive(interactsexclude);
			_ctx.PushPrimitive(outresult);
			_ctx.SetIdentifier(0xDBED3874);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void TraceEndShape(IntPtr startpos, IntPtr endpos, IntPtr ignoreentity, ulong interactsas, ulong interactswith, ulong interactsexclude, IntPtr outresult){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(startpos);
			_ctx.PushPrimitive(endpos);
			_ctx.PushPrimitive(ignoreentity);
			_ctx.PushPrimitive(interactsas);
			_ctx.PushPrimitive(interactswith);
			_ctx.PushPrimitive(interactsexclude);
			_ctx.PushPrimitive(outresult);
			_ctx.SetIdentifier(0x8A833D84);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void TraceHullShape(IntPtr startpos, IntPtr endpos, IntPtr mins, IntPtr maxs, IntPtr ignoreentity, ulong interactsas, ulong interactswith, ulong interactsexclude, IntPtr outresult){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(startpos);
			_ctx.PushPrimitive(endpos);
			_ctx.PushPrimitive(mins);
			_ctx.PushPrimitive(maxs);
			_ctx.PushPrimitive(ignoreentity);
			_ctx.PushPrimitive(interactsas);
			_ctx.PushPrimitive(interactswith);
			_ctx.PushPrimitive(interactsexclude);
			_ctx.PushPrimitive(outresult);
			_ctx.SetIdentifier(0x6C62B676);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static ulong PointContents(IntPtr pos, ulong contentsmask){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(pos);
			_ctx.PushPrimitive(contentsmask);
			_ctx.SetIdentifier(0x8A68FFAC);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<ulong>();
		}

        public static bool CheckAreaOverlappingEntity(IntPtr area, IntPtr entity, bool extrudehullheight){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(area);
			_ctx.PushPrimitive(entity);
			_ctx.PushPrimitive(extrudehullheight);
			_ctx.SetIdentifier(0x2ACFC3F3);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static void GetEntityWorldSpaceAabb(IntPtr entity, IntPtr minsout, IntPtr maxsout){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(entity);
			_ctx.PushPrimitive(minsout);
			_ctx.PushPrimitive(maxsout);
			_ctx.SetIdentifier(0x6C485DCE);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static IntPtr GetEconItemSystem(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0x981E9B5B);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static bool IsServerPaused(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0xB216AAAC);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static short GetSchemaOffset(string classname, string propname){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(classname);
			_ctx.PushString(propname);
			_ctx.SetIdentifier(0x57B77D8F);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<short>();
		}

        public static bool IsSchemaFieldNetworked(string classname, string propname){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(classname);
			_ctx.PushString(propname);
			_ctx.SetIdentifier(0xFE413B0C);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static T GetSchemaValueByName<T>(IntPtr instance, int returntype, string classname, string propname){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(instance);
			_ctx.PushPrimitive(returntype);
			_ctx.PushString(classname);
			_ctx.PushString(propname);
			_ctx.SetIdentifier(0xD01E4EB5);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return (T)_ctx.GetResult(typeof(T));
		}

        public static void SetSchemaValueByName<T>(IntPtr instance, int returntype, string classname, string propname, T value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(instance);
			_ctx.PushPrimitive(returntype);
			_ctx.PushString(classname);
			_ctx.PushString(propname);
			_ctx.Push(value);
			_ctx.SetIdentifier(0xAB9AA921);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static int GetSchemaClassSize(string classname){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(classname);
			_ctx.SetIdentifier(0x9CE4FC56);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<int>();
		}

        public static void SchemaSetStateChanged(IntPtr instance, uint offset, uint arrayindex, uint pathindex){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(instance);
			_ctx.PushPrimitive(offset);
			_ctx.PushPrimitive(arrayindex);
			_ctx.PushPrimitive(pathindex);
			_ctx.SetIdentifier(0x7D697B7C);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void SchemaNetworkStateChanged(IntPtr instance, uint offset, uint arrayindex, uint pathindex){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(instance);
			_ctx.PushPrimitive(offset);
			_ctx.PushPrimitive(arrayindex);
			_ctx.PushPrimitive(pathindex);
			_ctx.SetIdentifier(0xBBE9D700);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static IntPtr CreateTimer(float interval, InputArgument callback, int flags){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(interval);
			_ctx.Push((InputArgument)callback);
			_ctx.PushPrimitive(flags);
			_ctx.SetIdentifier(0x7A5BAE39);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static void KillTimer(IntPtr timer){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(timer);
			_ctx.SetIdentifier(0x32313EDF);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void HookUsermessage(int messageid, InputArgument callback, HookMode mode){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(messageid);
			_ctx.Push((InputArgument)callback);
			_ctx.PushPrimitive(mode);
			_ctx.SetIdentifier(0x76C63A83);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void UnhookUsermessage(int messageid, InputArgument callback, HookMode mode){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(messageid);
			_ctx.Push((InputArgument)callback);
			_ctx.PushPrimitive(mode);
			_ctx.SetIdentifier(0x63B0AC38);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static bool PbHasfield(UserMessage message, string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.SetIdentifier(0xC971FB70);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static int PbReadint(UserMessage message, string name, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0x5FA8BDC9);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<int>();
		}

        public static long PbReadint64(UserMessage message, string name, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0xECCF528B);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<long>();
		}

        public static float PbReadfloat(UserMessage message, string name, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0xED208CEA);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<float>();
		}

        public static bool PbReadbool(UserMessage message, string name, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0x54C0D7F4);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static string PbReadstring(UserMessage message, string name, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0x66CACEEF);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static int PbReadbytes(UserMessage message, string name, IntPtr buffer, int size, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(buffer);
			_ctx.PushPrimitive(size);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0xECD23703);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<int>();
		}

        public static int PbReadbyteslength(UserMessage message, string name, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0xF74C465F);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<int>();
		}

        public static int PbGetrepeatedfieldcount(UserMessage message, string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.SetIdentifier(0xDE4E1549);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<int>();
		}

        public static void PbSetint(UserMessage message, string name, int value, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(value);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0x99BBC059);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void PbSetint64(UserMessage message, string name, long value, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(value);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0xF7AD351B);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void PbSetfloat(UserMessage message, string name, float value, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(value);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0xF7FDEB7A);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void PbSetbool(UserMessage message, string name, bool value, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(value);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0xD1342864);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void PbSetstring(UserMessage message, string name, string value, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushString(value);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0x15C78B7F);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void PbSetbytes(UserMessage message, string name, IntPtr buffer, int size, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(buffer);
			_ctx.PushPrimitive(size);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0xF7C09993);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void PbAddint(UserMessage message, string name, int value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0x66CD6A1A);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void PbAddint64(UserMessage message, string name, long value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0x4FD05AD8);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void PbAddfloat(UserMessage message, string name, float value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0x5117B239);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void PbAddbool(UserMessage message, string name, bool value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0x40827C47);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void PbAddstring(UserMessage message, string name, string value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushString(value);
			_ctx.SetIdentifier(0x8DFD739C);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void PbAddbytes(UserMessage message, string name, IntPtr buffer, int size){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(buffer);
			_ctx.PushPrimitive(size);
			_ctx.SetIdentifier(0x50DB8210);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void PbRemoverepeatedfieldvalue(UserMessage message, string name, int index){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushString(name);
			_ctx.PushPrimitive(index);
			_ctx.SetIdentifier(0x1721FCB1);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static string PbGetdebugstring(UserMessage message){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.SetIdentifier(0x913FB7BA);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static ulong UsermessageGetrecipients(UserMessage message){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.SetIdentifier(0x70CDDEBE);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<ulong>();
		}

        public static void UsermessageSetrecipients(UserMessage message, ulong recipients){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.PushPrimitive(recipients);
			_ctx.SetIdentifier(0xB4ED43AA);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static int UsermessageFindmessageidbyname(string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.SetIdentifier(0x22CD6C9F);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<int>();
		}

        public static IntPtr UsermessageCreate(string name){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushString(name);
			_ctx.SetIdentifier(0xE8E83344);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static IntPtr UsermessageCreatebyid(int id){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(id);
			_ctx.SetIdentifier(0xBC758632);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static void UsermessageSend(UserMessage message){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.SetIdentifier(0x24EB6B3C);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void UsermessageDelete(UserMessage message){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.SetIdentifier(0xE10465D9);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static int UsermessageGetid(UserMessage message){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.SetIdentifier(0xC17BA71B);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<int>();
		}

        public static string UsermessageGetname(UserMessage message){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.SetIdentifier(0xEFE0FD1);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static string UsermessageGettype(UserMessage message){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.Push(message);
			_ctx.SetIdentifier(0xEF4842E);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultString();
		}

        public static IntPtr VectorNew(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0xA67981DF);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static IntPtr Vector2dNew(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0x2CD71169);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static IntPtr Vector4dNew(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0x16585EAF);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static IntPtr Matrix3x4New(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0xA2E1A42);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static IntPtr QuaternionNew(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0xD27D7946);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static IntPtr AngleNew(){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.SetIdentifier(0x11907167);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<IntPtr>();
		}

        public static float VectorGetX(IntPtr vector){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vector);
			_ctx.SetIdentifier(0x2A85CBB2);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<float>();
		}

        public static float VectorGetY(IntPtr vector){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vector);
			_ctx.SetIdentifier(0x2A85CBB3);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<float>();
		}

        public static float VectorGetZ(IntPtr vector){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vector);
			_ctx.SetIdentifier(0x2A85CBB0);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<float>();
		}

        public static void VectorSetX(IntPtr vector, float value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vector);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0x2B62AFA6);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void VectorSetY(IntPtr vector, float value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vector);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0x2B62AFA7);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void VectorSetZ(IntPtr vector, float value){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vector);
			_ctx.PushPrimitive(value);
			_ctx.SetIdentifier(0x2B62AFA4);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void VectorAngles(IntPtr vector, IntPtr pseudoup, IntPtr outangle){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vector);
			_ctx.PushPrimitive(pseudoup);
			_ctx.PushPrimitive(outangle);
			_ctx.SetIdentifier(0x6E6886B1);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static void AngleVectors(IntPtr vector, IntPtr forwardout, IntPtr rightout, IntPtr upout){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vector);
			_ctx.PushPrimitive(forwardout);
			_ctx.PushPrimitive(rightout);
			_ctx.PushPrimitive(upout);
			_ctx.SetIdentifier(0xF696A2F1);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static float VectorLength(IntPtr vector){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vector);
			_ctx.SetIdentifier(0x94B5BA5F);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<float>();
		}

        public static float VectorLength2d(IntPtr vector){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vector);
			_ctx.SetIdentifier(0xBAC81CD6);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<float>();
		}

        public static float VectorLengthSqr(IntPtr vector){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vector);
			_ctx.SetIdentifier(0x13CB3150);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<float>();
		}

        public static float VectorLength2dSqr(IntPtr vector){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vector);
			_ctx.SetIdentifier(0xEAF6FE79);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<float>();
		}

        public static bool VectorIsZero(IntPtr vector){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(vector);
			_ctx.SetIdentifier(0xA4B37BC4);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<bool>();
		}

        public static void SetClientListening(IntPtr receiver, IntPtr sender, uint listen){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(receiver);
			_ctx.PushPrimitive(sender);
			_ctx.PushPrimitive(listen);
			_ctx.SetIdentifier(0xD38BEE77);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static ListenOverride GetClientListening(IntPtr receiver, IntPtr sender){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(receiver);
			_ctx.PushPrimitive(sender);
			_ctx.SetIdentifier(0xE95644E3);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<ListenOverride>();
		}

        public static void SetClientVoiceFlags(IntPtr client, uint flags){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(client);
			_ctx.PushPrimitive(flags);
			_ctx.SetIdentifier(0x48EB2FC8);
			_ctx.Invoke();
			_ctx.CheckErrors();
		}

        public static uint GetClientVoiceFlags(IntPtr client){
			var _ctx = ScriptContext.GlobalScriptContext;
			_ctx.Reset();
			_ctx.PushPrimitive(client);
			_ctx.SetIdentifier(0x9685205C);
			_ctx.Invoke();
			_ctx.CheckErrors();
			return _ctx.GetResultPrimitive<uint>();
		}
    }
}
