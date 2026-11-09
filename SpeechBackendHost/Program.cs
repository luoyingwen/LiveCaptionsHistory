using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// The private ABI below is valid only for the exact installed binary verified
// by reverse engineering and an independent audio transcription test.
internal static class Program
{
    private const string SupportedHash = "ECCD4ADC7E4E8F127344A0CCBB7E09C92E185D8589CF5197A6F0DD1DE0CE9FEF";
    private static readonly object OutputLock = new();
    private static volatile bool _stop;
    private static volatile bool _acceptCaptions;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        IntPtr instance = 0, factory = 0, className = 0;
        long captionToken = 0, errorToken = 0;
        bool captionAdded = false, errorAdded = false, started = false;
        try
        {
            var parentIndex = Array.IndexOf(args, "--parent");
            if (parentIndex < 0 || parentIndex + 1 >= args.Length || !int.TryParse(args[parentIndex + 1], out var parentId))
                throw new ArgumentException("识别进程必须由字幕历史应用启动。");
            var parent = Process.GetProcessById(parentId);
            _ = parent.Handle; // Hold this process identity rather than reusing a PID.
            // Keep watching even while native Stop/Release is running in finally.
            // A background thread waiting on the owner handle avoids timer/pool
            // starvation; it does not keep this host alive after Main returns.
            new Thread(() => { parent.WaitForExit(); Environment.Exit(0); })
                { IsBackground = true, Name = "Caption owner lifetime" }.Start();
            using var startupTimeout = new Timer(_ => { Write(new { kind = "error", message = "本机识别引擎启动超时，请重新连接。" }); Environment.Exit(2); }, null, 45000, Timeout.Infinite);
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await Console.In.ReadLineAsync() is { } line)
                    {
                        using var command = JsonDocument.Parse(line);
                        if (command.RootElement.GetProperty("command").GetString() == "stop") break;
                    }
                }
                catch (Exception) { }
                finally { _acceptCaptions = false; _stop = true; }
            });

            var package = FindPackage();
            var backend = Path.Combine(package, "LiveCaptionsBackendDll.dll");
            using (var file = File.OpenRead(backend))
                if (Convert.ToHexString(SHA256.HashData(file)) != SupportedHash)
                    throw new NotSupportedException("Windows 识别组件版本已变化，需更新本应用的适配后才能连接。");
            Check(Native.RoInitialize(0));
            Check(Native.CreateDispatcherQueueController(new DqOptions(12, 2, 2), out var dispatcher));
            if (!Native.SetDefaultDllDirectories(0x1000)) throw new Win32Exception();
            AddDirectory(package);
            AddDirectory(Path.Combine(package, "LiveCaptions"));
            var core = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps", "MicrosoftWindows.Client.CoreAI_cw5n1h2txyewy");
            if (Directory.Exists(core)) AddDirectory(core);
            var module = Native.LoadLibraryEx(backend, 0, 0x1100);
            if (module == 0) throw new Win32Exception();
            const string runtimeClass = "LiveCaptionsBackend.CaptionBackend";
            Check(Native.WindowsCreateString(runtimeClass, (uint)runtimeClass.Length, out className));
            Check(Marshal.GetDelegateForFunctionPointer<Factory>(NativeLibrary.GetExport(module, "DllGetActivationFactory"))(className, out factory));
            Check(Method<GetObject>(factory, 6)(factory, out instance));
            var iid = new Guid("5600de30-9216-5bb0-a742-22f696f3b1e4");
            Check(Marshal.QueryInterface(instance, ref iid, out var checkedInterface));
            Marshal.Release(instance);
            instance = checkedInterface;
            Check(Method<NoArgs>(instance, 30)(instance));
            var languages = GetLanguages(instance);
            var languageIndex = Array.IndexOf(args, "--language");
            if (languageIndex >= 0 && languageIndex + 1 < args.Length)
            {
                var selected = languages.FirstOrDefault(l => string.Equals(l, args[languageIndex + 1], StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("所选语言的本机模型尚未安装。");
                Check(Native.WindowsCreateString(selected, (uint)selected.Length, out var h));
                try { Check(Method<SwitchLanguage>(instance, 29)(instance, h, 0)); }
                finally { Native.WindowsDeleteString(h); }
            }
            Check(Method<GetBool>(instance, 21)(instance, out var available));
            if (available == 0) throw new InvalidOperationException("当前语言的 Windows 本机语音模型不可用。");
            // Refuse unexpected microphone capture. This product captures system
            // playback only; it never changes Windows' microphone preference.
            Check(Method<GetBool>(instance, 32)(instance, out var microphone));
            if (microphone != 0) throw new InvalidOperationException("Windows 字幕设置启用了麦克风，请关闭该设置后重新连接；本应用仅采集系统播放声音。");
            var language = GetString(instance, 31);
            var captionSink = new NativeEventSink(new Guid("c097bb71-f26a-567a-b512-2599d91bb6e9"), (_, eventArgs) =>
            {
                if (!_acceptCaptions) return;
                var text = GetString(eventArgs, 6);
                Check(Method<GetBool>(eventArgs, 7)(eventArgs, out var final));
                if (_acceptCaptions) Write(new { kind = "caption", text, final = final != 0 });
            });
            var errorSink = new NativeEventSink(new Guid("3ce21e41-5253-5215-804b-26e386e196d6"), (_, error) =>
            {
                _acceptCaptions = false;
                Write(new { kind = "error", message = $"本机识别引擎报告错误 {error.ToInt64()}，请重新连接。" });
                _stop = true;
            });
            Check(Method<AddEvent>(instance, 10)(instance, captionSink.Pointer, out captionToken));
            captionAdded = true;
            Check(Method<AddEvent>(instance, 12)(instance, errorSink.Pointer, out errorToken));
            errorAdded = true;
            if (_stop) return 0;
            Check(Method<NoArgs>(instance, 6)(instance));
            started = true;
            startupTimeout.Change(Timeout.Infinite, Timeout.Infinite);
            Write(new { kind = "ready", languages, language, backend, version = FileVersionInfo.GetVersionInfo(backend).FileVersion, microphone = false });
            _acceptCaptions = true;
            while (!_stop) Pump();
            GC.KeepAlive(dispatcher);
            return 0;
        }
        catch (Exception e)
        {
            Write(new { kind = "error", message = e.Message });
            return 1;
        }
        finally
        {
            _acceptCaptions = false;
            if (instance != 0)
            {
                if (started) Method<NoArgs>(instance, 7)(instance);
                if (captionAdded) Method<RemoveEvent>(instance, 11)(instance, captionToken);
                if (errorAdded) Method<RemoveEvent>(instance, 13)(instance, errorToken);
                Marshal.Release(instance);
            }
            if (factory != 0) Marshal.Release(factory);
            if (className != 0) Native.WindowsDeleteString(className);
            // The OS reclaims the dispatcher, module and rooted callback thunks
            // at process exit, after asynchronous native work can no longer run.
        }
    }

    private static string FindPackage()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps");
        var candidates = new[] { root, Path.Combine(root, "SxS") }.Where(Directory.Exists)
            .SelectMany(p => Directory.EnumerateDirectories(p, "MicrosoftWindows.*.Livtop_cw5n1h2txyewy"))
            .Where(p => File.Exists(Path.Combine(p, "LiveCaptionsBackendDll.dll")))
            .OrderByDescending(p => Version.TryParse(FileVersionInfo.GetVersionInfo(Path.Combine(p, "LiveCaptionsBackendDll.dll")).FileVersion, out var version) ? version : new Version());
        return candidates.FirstOrDefault() ?? throw new FileNotFoundException("未找到 Windows 本机字幕识别组件。");
    }
    private static void AddDirectory(string path) { if (Native.AddDllDirectory(path) == 0) throw new Win32Exception(); }
    private static string[] GetLanguages(IntPtr instance)
    {
        Check(Method<GetArray>(instance, 27)(instance, out var count, out var array));
        var result = new List<string>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var h = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                try { result.Add(ReadString(h)); } finally { Native.WindowsDeleteString(h); }
            }
        }
        finally { Marshal.FreeCoTaskMem(array); }
        return result.ToArray();
    }
    private static string GetString(IntPtr instance, int slot)
    {
        Check(Method<GetObject>(instance, slot)(instance, out var h));
        try { return ReadString(h); } finally { Native.WindowsDeleteString(h); }
    }
    private static string ReadString(IntPtr h) => Marshal.PtrToStringUni(Native.WindowsGetStringRawBuffer(h, out var length), (int)length) ?? "";
    private static T Method<T>(IntPtr obj, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    internal static void Write(object value) { lock (OutputLock) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); } }
    private static void Pump()
    {
        while (Native.PeekMessage(out var msg, 0, 0, 0, 1)) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
        Thread.Sleep(10);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Factory(IntPtr name, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetObject(IntPtr self, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetArray(IntPtr self, out uint count, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBool(IntPtr self, out byte result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NoArgs(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddEvent(IntPtr self, IntPtr handler, out long token);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RemoveEvent(IntPtr self, long token);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SwitchLanguage(IntPtr self, IntPtr value, byte flag);
    [StructLayout(LayoutKind.Sequential)] private record struct DqOptions(uint Size, int ThreadType, int ApartmentType);
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Msg { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; public uint extra; }
        [DllImport("user32.dll")] internal static extern bool PeekMessage(out Msg msg, IntPtr window, uint min, uint max, uint flags);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Msg msg);
        [DllImport("user32.dll")] internal static extern IntPtr DispatchMessage(ref Msg msg);
        [DllImport("combase.dll")] internal static extern int RoInitialize(uint mode);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] internal static extern int WindowsCreateString(string value, uint length, out IntPtr result);
        [DllImport("combase.dll")] internal static extern int WindowsDeleteString(IntPtr value);
        [DllImport("combase.dll")] internal static extern IntPtr WindowsGetStringRawBuffer(IntPtr value, out uint length);
        [DllImport("CoreMessaging.dll")] internal static extern int CreateDispatcherQueueController(DqOptions options, out IntPtr result);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetDefaultDllDirectories(uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr AddDllDirectory(string path);
        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    }
}
