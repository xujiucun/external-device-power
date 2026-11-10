using System;
using System.Runtime.InteropServices;

namespace DevicePower.Taskbar;

/// <summary>
/// 任务栏电量条的原生宿主，做法与 TrafficMonitor 在 Win11 下一致：
/// WS_POPUP + WS_EX_NOREDIRECTIONBITMAP 的窗口挂到 Shell_TrayWnd 下面，
/// 内容来自 D3D11 交换链，由 DirectComposition 合成。
/// 这样窗口本身是任务栏的一部分，开始菜单把任务栏提到更高层时也不会被盖住。
/// </summary>
internal sealed class CompositionStripHost : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExNoactivate = 0x08000000;
    private const int WsExNoredirectionbitmap = 0x00200000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwShowNoActivate = 4;
    private const int SwHide = 0;

    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmRightButtonUp = 0x0205;
    private const int WmMouseLeave = 0x02A3;
    private const int WmNcDestroy = 0x0082;
    private const int WmSetCursor = 0x0020;
    private const int HtClient = 1;
    private const int TmeLeave = 0x0002;

    private const int GwlExStyle = -20;
    private const int IdcArrow = 32512;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const int D3DDriverTypeHardware = 1;
    private const uint D3D11SdkVersion = 7;
    private const uint D3DFeatureLevel110 = 0xB000;

    private const uint DxgiFormatB8G8R8A8Unorm = 87;
    private const uint DxgiUsageRenderTargetOutput = 0x20;
    private const uint DxgiSwapEffectFlipSequential = 3;
    private const uint DxgiAlphaModePremultiplied = 1;

    private const uint D3D11UsageDefault = 0;

    private static readonly Guid IidIDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IidIDxgiFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    private static readonly Guid IidIDCompositionDevice = new("c37ea93a-e7aa-450d-b16f-9746cb0407f3");
    private static readonly Guid IidID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private static CompositionStripHost _instance;
    private static WndProcDelegate _wndProc;
    private static bool _classRegistered;

    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _factory;
    private IntPtr _dcomp;
    private IntPtr _swapChain;
    private IntPtr _target;
    private IntPtr _visual;
    private IntPtr _texture;
    private IntPtr _hwnd;
    private IntPtr _taskbar;
    private int _width;
    private int _height;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;
    private int _lastWidth = -1;
    private int _lastHeight = -1;
    private bool _visible;
    private bool _hover;
    private bool _disposed;

    /// <summary>左键点击电量条。</summary>
    public event Action Clicked;

    /// <summary>右键点击电量条，用来弹出和托盘一致的菜单。</summary>
    public event Action RightClicked;

    public IntPtr Hwnd => _hwnd;

    /// <summary>鼠标进入/离开电量条，用来控制悬停提示。</summary>
    public event Action<bool> HoverChanged;

    /// <summary>创建 D3D11 / DirectComposition 设备。失败时调用方应回退到普通 WPF 窗口。</summary>
    public bool TryInitialize()
    {
        if (_dcomp != IntPtr.Zero) return true;
        _instance = this;

        int[] levels = { (int)D3DFeatureLevel110 };
        int hr = D3D11CreateDevice(IntPtr.Zero, D3DDriverTypeHardware, IntPtr.Zero, D3D11CreateDeviceBgraSupport,
            levels, 1, D3D11SdkVersion, out _device, out _, out _context);
        if (hr < 0 || _device == IntPtr.Zero)
        {
            Log("D3D11CreateDevice 失败 hr=0x" + hr.ToString("X8"));
            return false;
        }

        Guid iidDxgiDevice = IidIDxgiDevice;
        hr = Marshal.QueryInterface(_device, ref iidDxgiDevice, out IntPtr dxgiDevice);
        if (hr < 0)
        {
            Log("QueryInterface(IDXGIDevice) 失败 hr=0x" + hr.ToString("X8"));
            return false;
        }

        Guid iidFactory2 = IidIDxgiFactory2;
        hr = CreateDXGIFactory2(0, ref iidFactory2, out _factory);
        if (hr < 0)
        {
            Log("CreateDXGIFactory2 失败 hr=0x" + hr.ToString("X8"));
            return false;
        }

        Guid iidDcomp = IidIDCompositionDevice;
        hr = DCompositionCreateDevice(dxgiDevice, ref iidDcomp, out _dcomp);
        Marshal.Release(dxgiDevice);
        if (hr < 0)
        {
            Log("DCompositionCreateDevice 失败 hr=0x" + hr.ToString("X8"));
            return false;
        }

        return true;
    }

    /// <summary>
    /// 确保窗口存在、挂在任务栏下、尺寸与位置正确。坐标是屏幕坐标，内部换成任务栏客户区坐标。
    /// 返回 false 表示原生资源已经不可用，调用方应回退到 WPF 窗口。
    /// </summary>
    public bool Update(IntPtr taskbar, int screenX, int screenY, int width, int height)
    {
        if (_disposed) return false;
        if (taskbar == IntPtr.Zero || width <= 0 || height <= 0) return true;

        if (!EnsureWindow(taskbar, width, height)) return false;

        POINT point = new() { x = screenX, y = screenY };
        if (!ScreenToClient(taskbar, ref point))
        {
            Log("ScreenToClient 失败");
            return false;
        }

        bool moved = point.x != _lastX || point.y != _lastY;
        bool resized = width != _lastWidth || height != _lastHeight;
        _lastX = point.x;
        _lastY = point.y;
        _lastWidth = width;
        _lastHeight = height;

        if (!_visible || moved || resized)
        {
            SetWindowPos(_hwnd, IntPtr.Zero, point.x, point.y, width, height,
                SwpNoActivate | SwpShowWindow);
            _visible = true;
        }

        return true;
    }

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero && _visible)
        {
            ShowWindow(_hwnd, SwHide);
            _visible = false;
        }
    }

    /// <summary>把 WPF 渲染出的预乘 BGRA 像素传到交换链。</summary>
    public bool Upload(byte[] pixels, int width, int height, int stride)
    {
        if (_disposed || _swapChain == IntPtr.Zero || _hwnd == IntPtr.Zero) return false;

        Guid iidTexture = IidID3D11Texture2D;
        int hr = Vtbl<GetBufferFn>(_swapChain, 9)(_swapChain, 0, ref iidTexture, out IntPtr backBuffer);
        if (hr < 0 || backBuffer == IntPtr.Zero)
        {
            Log("GetBuffer 失败 hr=0x" + hr.ToString("X8"));
            return false;
        }

        try
        {
            IntPtr source = Marshal.AllocHGlobal(stride * height);
            try
            {
                Marshal.Copy(pixels, 0, source, stride * height);
                Vtbl<UpdateSubresourceFn>(_context, 48)(_context, _texture, 0, IntPtr.Zero, source,
                    (uint)stride, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(source);
            }

            Vtbl<CopyResourceFn>(_context, 47)(_context, backBuffer, _texture);

            hr = Vtbl<PresentFn>(_swapChain, 8)(_swapChain, 1, 0);
            if (hr < 0)
            {
                Log("Present 失败 hr=0x" + hr.ToString("X8"));
                return false;
            }

            Vtbl<CommitFn>(_dcomp, 3)(_dcomp);
            return true;
        }
        finally
        {
            Marshal.Release(backBuffer);
        }
    }

    private bool EnsureWindow(IntPtr taskbar, int width, int height)
    {
        if (_hwnd != IntPtr.Zero && !IsWindow(_hwnd))
        {
            // Explorer 重启会把子窗口一起销毁
            _hwnd = IntPtr.Zero;
            ReleaseTarget();
        }

        if (_hwnd != IntPtr.Zero && taskbar == _taskbar)
        {
            return EnsureSize(width, height);
        }

        ReleaseTarget();
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        EnsureClass();
        _instance = this;
        _hwnd = CreateWindowEx(
            WsExNoredirectionbitmap | WsExToolwindow | WsExNoactivate,
            "DevicePowerCompositionStrip", "DevicePowerStrip", WsPopup,
            0, 0, width, height, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Log("CreateWindowEx 失败 err=" + Marshal.GetLastWin32Error());
            return false;
        }

        SetParent(_hwnd, taskbar);
        _taskbar = taskbar;
        _lastX = int.MinValue;
        _lastY = int.MinValue;
        _lastWidth = -1;
        _lastHeight = -1;
        _visible = false;
        _hover = false;

        Log("窗口已挂到任务栏 hwnd=0x" + _hwnd.ToString("X") + " taskbar=0x" + taskbar.ToString("X") +
            " ex=0x" + GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64().ToString("X"));

        if (!EnsureSwapChain(width, height)) return false;

        int hr = Vtbl<CreateTargetForHwndFn>(_dcomp, 6)(_dcomp, _hwnd,
            (GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64() & WsExTopmost) != 0 ? 1 : 0, out _target);
        if (hr < 0)
        {
            Log("CreateTargetForHwnd 失败 hr=0x" + hr.ToString("X8"));
            return false;
        }

        hr = Vtbl<CreateVisualFn>(_dcomp, 7)(_dcomp, out _visual);
        if (hr < 0)
        {
            Log("CreateVisual 失败 hr=0x" + hr.ToString("X8"));
            return false;
        }

        hr = Vtbl<SetContentFn>(_visual, 15)(_visual, _swapChain);
        if (hr < 0)
        {
            Log("Visual.SetContent 失败 hr=0x" + hr.ToString("X8"));
            return false;
        }

        hr = Vtbl<SetRootFn>(_target, 3)(_target, _visual);
        if (hr < 0)
        {
            Log("Target.SetRoot 失败 hr=0x" + hr.ToString("X8"));
            return false;
        }

        Vtbl<CommitFn>(_dcomp, 3)(_dcomp);

        return true;
    }

    private bool EnsureSwapChain(int width, int height)
    {
        if (_swapChain != IntPtr.Zero) return EnsureSize(width, height);

        var desc = new SwapChainDesc1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = DxgiFormatB8G8R8A8Unorm,
            Stereo = 0,
            SampleCount = 1,
            SampleQuality = 0,
            BufferUsage = DxgiUsageRenderTargetOutput,
            BufferCount = 2,
            Scaling = 0,
            SwapEffect = DxgiSwapEffectFlipSequential,
            AlphaMode = DxgiAlphaModePremultiplied,
            Flags = 0,
        };

        int hr = Vtbl<CreateSwapChainForCompositionFn>(_factory, 24)(_factory, _device, ref desc, IntPtr.Zero,
            out _swapChain);
        if (hr < 0 || _swapChain == IntPtr.Zero)
        {
            Log("CreateSwapChainForComposition 失败 hr=0x" + hr.ToString("X8"));
            _swapChain = IntPtr.Zero;
            return false;
        }

        _width = width;
        _height = height;
        return CreateTexture(width, height);
    }

    private bool EnsureSize(int width, int height)
    {
        if (width == _width && height == _height && _texture != IntPtr.Zero) return true;

        if (_swapChain != IntPtr.Zero)
        {
            int hr = Vtbl<ResizeBuffersFn>(_swapChain, 13)(_swapChain, 2, (uint)width, (uint)height,
                DxgiFormatB8G8R8A8Unorm, 0);
            if (hr < 0)
            {
                Log("ResizeBuffers 失败 hr=0x" + hr.ToString("X8"));
                return false;
            }
        }

        _width = width;
        _height = height;
        return CreateTexture(width, height);
    }

    private bool CreateTexture(int width, int height)
    {
        if (_texture != IntPtr.Zero)
        {
            Marshal.Release(_texture);
            _texture = IntPtr.Zero;
        }

        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormatB8G8R8A8Unorm,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = D3D11UsageDefault,
            BindFlags = 0,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        int hr = Vtbl<CreateTexture2DFn>(_device, 5)(_device, ref desc, IntPtr.Zero, out _texture);
        if (hr < 0)
        {
            Log("CreateTexture2D 失败 hr=0x" + hr.ToString("X8"));
            return false;
        }

        return true;
    }

    private void EnsureClass()
    {
        if (_classRegistered) return;

        _wndProc = WndProc;
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf(typeof(WndClassEx)),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, (IntPtr)IdcArrow),
            lpszClassName = "DevicePowerCompositionStrip",
        };

        _classRegistered = RegisterClassEx(ref wc) != 0;
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        CompositionStripHost host = _instance;

        switch (msg)
        {
            case WmSetCursor:
                // 任务栏子窗口没有自己的光标时，系统会沿用上一个光标（常见是转圈的"正在加载"）
                if ((lParam.ToInt64() & 0xFFFF) == HtClient)
                {
                    SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)IdcArrow));
                    return (IntPtr)1;
                }
                break;

            case WmMouseMove:
                if (host != null && !host._hover)
                {
                    host._hover = true;
                    var track = new TrackMouseEventData
                    {
                        cbSize = (uint)Marshal.SizeOf(typeof(TrackMouseEventData)),
                        dwFlags = TmeLeave,
                        hwndTrack = hwnd,
                    };
                    TrackMouseEvent(ref track);
                    host.HoverChanged?.Invoke(true);
                }
                return IntPtr.Zero;

            case WmMouseLeave:
                if (host != null && host._hover)
                {
                    host._hover = false;
                    host.HoverChanged?.Invoke(false);
                }
                return IntPtr.Zero;

            case WmLeftButtonUp:
                host?.Clicked?.Invoke();
                return IntPtr.Zero;

            case WmRightButtonUp:
                host?.RightClicked?.Invoke();
                return IntPtr.Zero;

            case WmNcDestroy:
                if (host != null && host._hwnd == hwnd)
                {
                    host._hwnd = IntPtr.Zero;
                    host._visible = false;
                    host.ReleaseTarget();
                }
                break;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ReleaseTarget()
    {
        if (_target != IntPtr.Zero)
        {
            Marshal.Release(_target);
            _target = IntPtr.Zero;
        }

        if (_visual != IntPtr.Zero)
        {
            Marshal.Release(_visual);
            _visual = IntPtr.Zero;
        }
    }

    private static void Log(string text) => DiagLog.WriteIfChanged("dcomp", text);

    private static T Vtbl<T>(IntPtr comObject, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size));

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSwapChainForCompositionFn(IntPtr self, IntPtr device, ref SwapChainDesc1 desc,
        IntPtr restrictToOutput, out IntPtr swapChain);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResizeBuffersFn(IntPtr self, uint count, uint width, uint height, uint format, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBufferFn(IntPtr self, uint index, ref Guid iid, out IntPtr surface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PresentFn(IntPtr self, uint syncInterval, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DFn(IntPtr self, ref Texture2DDesc desc, IntPtr initialData, out IntPtr texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UpdateSubresourceFn(IntPtr self, IntPtr resource, uint subresource, IntPtr box,
        IntPtr data, uint rowPitch, uint depthPitch);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceFn(IntPtr self, IntPtr destination, IntPtr source);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTargetForHwndFn(IntPtr self, IntPtr hwnd, int topmost, out IntPtr target);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateVisualFn(IntPtr self, out IntPtr visual);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetContentFn(IntPtr self, IntPtr content);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetRootFn(IntPtr self, IntPtr visual);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CommitFn(IntPtr self);

    [StructLayout(LayoutKind.Sequential)]
    private struct SwapChainDesc1
    {
        public uint Width;
        public uint Height;
        public uint Format;
        public uint Stereo;
        public uint SampleCount;
        public uint SampleQuality;
        public uint BufferUsage;
        public uint BufferCount;
        public uint Scaling;
        public uint SwapEffect;
        public uint AlphaMode;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        int[] featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel,
        out IntPtr context);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory2(uint flags, ref Guid iid, out IntPtr factory);

    [DllImport("dcomp.dll", ExactSpelling = true)]
    private static extern int DCompositionCreateDevice(IntPtr dxgiDevice, ref Guid iid, out IntPtr device);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx wndClass);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TrackMouseEventData data);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        ReleaseTarget();

        if (_texture != IntPtr.Zero) Marshal.Release(_texture);
        if (_swapChain != IntPtr.Zero) Marshal.Release(_swapChain);
        if (_dcomp != IntPtr.Zero) Marshal.Release(_dcomp);
        if (_factory != IntPtr.Zero) Marshal.Release(_factory);
        if (_context != IntPtr.Zero) Marshal.Release(_context);
        if (_device != IntPtr.Zero) Marshal.Release(_device);

        _texture = _swapChain = _dcomp = _factory = _context = _device = IntPtr.Zero;
        if (_instance == this) _instance = null;
    }
}
