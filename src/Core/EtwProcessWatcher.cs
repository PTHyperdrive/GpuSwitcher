using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GpuSwitcher.Interop;

namespace GpuSwitcher;

/// <summary>Real-time ETW session on Microsoft-Windows-Kernel-Process: process start/stop and image loads, delivered
/// within milliseconds of the kernel event instead of being found by polling. Needs admin/SYSTEM.</summary>
public sealed unsafe class EtwProcessWatcher : IDisposable
{
    public const string SessionName = "GpuSwitcher-BootGuard";
    static readonly Guid KernelProcess = new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
    const ulong KeywordProcess = 0x10, KeywordImage = 0x40;
    const int PropsSize = 120, NameBytes = 512;

    /// <summary>pid, session id, parent pid.</summary>
    public event Action<int, int, int>? ProcessStarted;
    public event Action<int>? ProcessStopped;
    /// <summary>pid, dll file name: raised only for graphics-API DLLs (d3d11, dxgi, vulkan-1…).</summary>
    public event Action<int, string>? GraphicsDllLoaded;

    IntPtr _props, _logfile, _loggerName;
    ulong _session, _trace = ulong.MaxValue;
    Thread? _thread;
    GCHandle _self;
    long _latencyTicks, _latencySamples;

    /// <summary>Average kernel-event-to-callback delay so far, in milliseconds.</summary>
    public double AverageLatencyMs => _latencySamples == 0 ? 0 : _latencyTicks * 1000.0 / Stopwatch.Frequency / _latencySamples;
    public bool MillisecondFlush { get; private set; }

    static readonly string[] GraphicsDlls =
        { "d3d9.dll", "d3d10.dll", "d3d10_1.dll", "d3d11.dll", "d3d12.dll", "d3d12core.dll", "dxgi.dll", "vulkan-1.dll", "opengl32.dll" };

    public void Start()
    {
        _self = GCHandle.Alloc(this);
        _props = Marshal.AllocHGlobal(PropsSize + NameBytes);

        // A previous instance that died leaves its session running: stop it first.
        InitProps(0, 0);
        Native.ControlTraceW(0, SessionName, _props, Native.EVENT_TRACE_CONTROL_STOP);

        // Prefer a 10 ms flush; older systems reject the millisecond flag, then fall back to 1 s.
        InitProps(Native.EVENT_TRACE_REAL_TIME_MODE | Native.EVENT_TRACE_USE_MS_FLUSH_TIMER, 10);
        int rc = Native.StartTraceW(out _session, SessionName, _props);
        MillisecondFlush = rc == 0;
        if (rc != 0)
        {
            InitProps(Native.EVENT_TRACE_REAL_TIME_MODE, 1);
            rc = Native.StartTraceW(out _session, SessionName, _props);
        }
        if (rc != 0) throw new InvalidOperationException($"StartTrace failed ({rc}); the boot guard must run elevated or as SYSTEM");

        rc = Native.EnableTraceEx2(_session, KernelProcess, Native.EVENT_CONTROL_CODE_ENABLE_PROVIDER, 4, KeywordProcess | KeywordImage, 0, 0, IntPtr.Zero);
        if (rc != 0) throw new InvalidOperationException($"EnableTraceEx2 failed ({rc})");

        // EVENT_TRACE_LOGFILEW: LoggerName @8, ProcessTraceMode @28, EventRecordCallback @424, Context @440 (size 448).
        _logfile = Marshal.AllocHGlobal(448);
        new Span<byte>((void*)_logfile, 448).Clear();
        _loggerName = Marshal.StringToHGlobalUni(SessionName);
        *(IntPtr*)((byte*)_logfile + 8) = _loggerName;
        // RAW_TIMESTAMP (0x1000): keep QPC ticks so latency can be measured against Stopwatch.
        *(uint*)((byte*)_logfile + 28) = Native.PROCESS_TRACE_MODE_REAL_TIME | Native.PROCESS_TRACE_MODE_EVENT_RECORD | 0x1000;
        *(IntPtr*)((byte*)_logfile + 424) = (IntPtr)(delegate* unmanaged[Stdcall]<byte*, void>)&OnEvent;
        *(IntPtr*)((byte*)_logfile + 440) = GCHandle.ToIntPtr(_self);
        _trace = Native.OpenTraceW(_logfile);
        if (_trace == ulong.MaxValue) throw new InvalidOperationException($"OpenTrace failed ({Marshal.GetLastWin32Error()})");

        _thread = new Thread(() =>
        {
            ulong h = _trace;
            Native.ProcessTrace(&h, 1, IntPtr.Zero, IntPtr.Zero);   // blocks until the session stops
        })
        { IsBackground = true, Name = "etw-kernel-process", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    void InitProps(uint logFileMode, uint flushTimer)
    {
        var p = (byte*)_props;
        new Span<byte>(p, PropsSize + NameBytes).Clear();
        *(uint*)(p + 0) = PropsSize + NameBytes;              // Wnode.BufferSize
        *(uint*)(p + 40) = 1;                                 // Wnode.ClientContext: QPC timestamps
        *(uint*)(p + 44) = Native.WNODE_FLAG_TRACED_GUID;     // Wnode.Flags
        *(uint*)(p + 48) = 16;                                // BufferSize (KB)
        *(uint*)(p + 52) = 4;                                 // MinimumBuffers
        *(uint*)(p + 56) = 64;                                // MaximumBuffers
        *(uint*)(p + 64) = logFileMode;
        *(uint*)(p + 68) = flushTimer;
        *(uint*)(p + 116) = PropsSize;                        // LoggerNameOffset
    }

    // EVENT_RECORD: Flags @4, TimeStamp @16, Id @40, UserDataLength @86, UserData @96, UserContext @104.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static void OnEvent(byte* rec)
    {
        try
        {
            var ctx = *(IntPtr*)(rec + 104);
            if (ctx == IntPtr.Zero || GCHandle.FromIntPtr(ctx).Target is not EtwProcessWatcher self) return;
            ushort id = *(ushort*)(rec + 40);
            ushort flags = *(ushort*)(rec + 4);
            byte* data = *(byte**)(rec + 96);
            int len = *(ushort*)(rec + 86);
            if (data == null) return;

            switch (id)
            {
                case 1 when len >= 20:   // ProcessStart: ProcessID, CreateTime, ParentProcessID, SessionID, Flags, ImageName…
                    self.Measure(*(long*)(rec + 16));
                    self.ProcessStarted?.Invoke((int)*(uint*)data, (int)*(uint*)(data + 16), (int)*(uint*)(data + 12));
                    break;
                case 2 when len >= 4:    // ProcessStop: ProcessID…
                    self.ProcessStopped?.Invoke((int)*(uint*)data);
                    break;
                case 5:                  // ImageLoad: ImageBase, ImageSize, ProcessID, CheckSum, TimeDateStamp, DefaultBase, ImageName
                    int ptr = (flags & 0x20) != 0 ? 4 : 8;   // EVENT_HEADER_FLAG_32_BIT_HEADER
                    int pidOff = 2 * ptr, nameOff = 3 * ptr + 12;
                    if (len < nameOff + 4) return;
                    var name = new ReadOnlySpan<char>(data + nameOff, (len - nameOff) / 2);
                    int z = name.IndexOf('\0');
                    if (z >= 0) name = name[..z];
                    int slash = name.LastIndexOf('\\');
                    var file = slash >= 0 ? name[(slash + 1)..] : name;
                    foreach (var g in GraphicsDlls)
                        if (file.Equals(g, StringComparison.OrdinalIgnoreCase))
                        {
                            self.GraphicsDllLoaded?.Invoke((int)*(uint*)(data + pidOff), g);
                            break;
                        }
                    break;
            }
        }
        catch { /* an exception escaping into ETW would kill the process */ }
    }

    void Measure(long eventQpc)
    {
        long d = Stopwatch.GetTimestamp() - eventQpc;
        if (d < 0 || d > Stopwatch.Frequency * 10) return;
        Interlocked.Add(ref _latencyTicks, d);
        Interlocked.Increment(ref _latencySamples);
    }

    public void Dispose()
    {
        if (_props != IntPtr.Zero)
        {
            InitProps(0, 0);
            Native.ControlTraceW(_session, null, _props, Native.EVENT_TRACE_CONTROL_STOP);
        }
        if (_trace != ulong.MaxValue) Native.CloseTrace(_trace);
        _thread?.Join(2000);
        if (_props != IntPtr.Zero) Marshal.FreeHGlobal(_props);
        if (_logfile != IntPtr.Zero) Marshal.FreeHGlobal(_logfile);
        if (_loggerName != IntPtr.Zero) Marshal.FreeHGlobal(_loggerName);
        if (_self.IsAllocated) _self.Free();
        _props = _logfile = _loggerName = IntPtr.Zero;
    }
}
