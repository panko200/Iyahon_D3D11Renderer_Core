using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D;

internal sealed class ViewportD3D11Host : HwndHost
{
    public enum MouseEventKind
    {
        LeftDown,
        LeftUp,
        RightDown,
        RightUp,
        MiddleDown,
        MiddleUp,
        Move,
        Wheel,
        Leave,
    }

    private const string WindowClassName = "Iyahon_D3D11Renderer_Viewport3D_Host";
    private static bool _isClassRegistered;
    private static WndProcDelegate? _defWndProc;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain? _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D? _depthTex;
    private ID3D11DepthStencilView? _dsv;
    private long? _deviceAdapterLuid;

    private int _pixelWidth = 1;
    private int _pixelHeight = 1;
    private bool _disposed;
    private bool _isTrackingMouseLeave;

    public ID3D11Device? Device => _device;
    public ID3D11DeviceContext? Context => _context;
    public ID3D11RenderTargetView? RenderTargetView => _rtv;
    public ID3D11DepthStencilView? DepthStencilView => _dsv;
    public int PixelWidth => _pixelWidth;
    public int PixelHeight => _pixelHeight;
    public double DpiScaleX { get; private set; } = 1.0;
    public double DpiScaleY { get; private set; } = 1.0;

    public event Action<ID3D11Device, ID3D11DeviceContext, ID3D11RenderTargetView, ID3D11DepthStencilView, int, int>? Render;
    public event Action<Point, MouseEventKind, int>? MouseAction;
    public event Action<Key, ModifierKeys>? KeyAction;
    public Func<Key, ModifierKeys, bool>? KeyDownInterceptor { get; set; }
    public event Action? DeviceLostOrRecreated;

    public void FocusHost()
    {
        if (!_disposed && Handle != IntPtr.Zero)
        {
            SetFocus(Handle);
        }
    }

    internal static Key ResolveKeyFromWin32Message(IntPtr wParam, IntPtr lParam)
    {
        int vkey = unchecked((int)wParam.ToInt64()) & 0xFF;
        if (vkey == 0xE5) // VK_PROCESSKEY (IME active)
        {
            uint scanCode = (uint)((lParam.ToInt64() >> 16) & 0xFF);
            int mapped = unchecked((int)MapVirtualKey(scanCode, 1)); // MAPVK_VSC_TO_VK
            if (mapped != 0)
                vkey = mapped;
        }
        return KeyInterop.KeyFromVirtualKey(vkey);
    }

    protected override bool TranslateAcceleratorCore(ref MSG msg, ModifierKeys modifiers)
    {
        if (msg.message == 0x0100 || msg.message == 0x0104) // WM_KEYDOWN / WM_SYSKEYDOWN
        {
            Key wpfKey = ResolveKeyFromWin32Message(msg.wParam, msg.lParam);
            if (wpfKey != Key.None && KeyDownInterceptor != null && KeyDownInterceptor(wpfKey, modifiers))
            {
                return true;
            }
        }
        return base.TranslateAcceleratorCore(ref msg, modifiers);
    }

    protected override bool TranslateCharCore(ref MSG msg, ModifierKeys modifiers)
    {
        if (msg.message is 0x0102 or 0x0103 or 0x0106 or 0x0107) // WM_CHAR / WM_DEADCHAR / WM_SYSCHAR / WM_SYSDEADCHAR
            return true;
        return base.TranslateCharCore(ref msg, modifiers);
    }

    public bool EnsureDeviceInitialized()
    {
        if (_disposed || Handle == IntPtr.Zero) return false;

        if (_device != null)
        {
            bool shouldReset = false;
            try
            {
                if (_device.DeviceRemovedReason.Failure)
                    shouldReset = true;
            }
            catch
            {
                shouldReset = true;
            }

            if (!shouldReset &&
                Viewport3DBridge.TryGetYmmAdapterLuid(out long ymmLuid) &&
                _deviceAdapterLuid.HasValue &&
                _deviceAdapterLuid.Value != ymmLuid)
            {
                shouldReset = true;
            }

            if (shouldReset)
                ResetDeviceAndSwapChain();
        }

        if (_device == null)
        {
            var flags = DeviceCreationFlags.BgraSupport;
            FeatureLevel[] levels =
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0,
            };
            if (!TryCreateMatchingDevice(flags, levels, out _device, out _context, out _deviceAdapterLuid))
                return false;

            DeviceLostOrRecreated?.Invoke();
        }

        if (_swapChain == null)
        {
            CreateSwapChainAndTargets();
        }
        else if (_rtv == null || _dsv == null)
        {
            ResizeOrCreateTargets();
        }

        return _device != null && _context != null && _swapChain != null && _rtv != null && _dsv != null;
    }

    public void RenderFrame()
    {
        if (!EnsureDeviceInitialized()) return;
        if (_device == null || _context == null || _swapChain == null || _rtv == null || _dsv == null) return;

        try
        {
            Render?.Invoke(_device, _context, _rtv, _dsv, _pixelWidth, _pixelHeight);
            _swapChain.Present(0, PresentFlags.None);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            if (_device != null && _device.DeviceRemovedReason.Failure)
            {
                ResetDeviceAndSwapChain();
            }
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"Viewport3D RenderFrame 例外: {ex.Message}");
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        if (!_isClassRegistered)
        {
            _defWndProc = DefWindowProc;
            IntPtr classPtr = Marshal.StringToHGlobalUni(WindowClassName);
            try
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    style = 0x0008, // CS_DBLCLKS
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_defWndProc),
                    hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512),
                    hbrBackground = GetStockObject(4),
                    lpszClassName = classPtr,
                };
                RegisterClassEx(ref wc);
            }
            finally
            {
                Marshal.FreeHGlobal(classPtr);
            }
            _isClassRegistered = true;
        }

        UpdatePixelSize();
        IntPtr cPtr = Marshal.StringToHGlobalUni(WindowClassName);
        IntPtr wPtr = Marshal.StringToHGlobalUni(string.Empty);
        try
        {
            IntPtr hwnd = CreateWindowEx(
                0, cPtr, wPtr,
                0x40000000 | 0x10000000, // WS_CHILD | WS_VISIBLE
                0, 0, _pixelWidth, _pixelHeight,
                hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (hwnd != IntPtr.Zero)
            {
                ImmAssociateContext(hwnd, IntPtr.Zero);
            }
            return new HandleRef(this, hwnd);
        }
        finally
        {
            Marshal.FreeHGlobal(cPtr);
            Marshal.FreeHGlobal(wPtr);
        }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
            DestroyWindow(hwnd.Handle);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdatePixelSize();
        ResizeOrCreateTargets();
        DeviceLostOrRecreated?.Invoke();
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case 0x0014: // WM_ERASEBKGND
                handled = true;
                return new IntPtr(1);
            case 0x000F: // WM_PAINT
                DeviceLostOrRecreated?.Invoke();
                break;
            case 0x0201: // WM_LBUTTONDOWN
                SetFocus(hwnd);
                SetCapture(hwnd);
                MouseAction?.Invoke(GetPoint(lParam), MouseEventKind.LeftDown, 0);
                handled = true;
                break;
            case 0x0202: // WM_LBUTTONUP
                ReleaseCapture();
                MouseAction?.Invoke(GetPoint(lParam), MouseEventKind.LeftUp, 0);
                handled = true;
                break;
            case 0x0204: // WM_RBUTTONDOWN
                SetFocus(hwnd);
                SetCapture(hwnd);
                MouseAction?.Invoke(GetPoint(lParam), MouseEventKind.RightDown, 0);
                handled = true;
                break;
            case 0x0205: // WM_RBUTTONUP
                ReleaseCapture();
                MouseAction?.Invoke(GetPoint(lParam), MouseEventKind.RightUp, 0);
                handled = true;
                break;
            case 0x0207: // WM_MBUTTONDOWN
                SetFocus(hwnd);
                SetCapture(hwnd);
                MouseAction?.Invoke(GetPoint(lParam), MouseEventKind.MiddleDown, 0);
                handled = true;
                break;
            case 0x0208: // WM_MBUTTONUP
                ReleaseCapture();
                MouseAction?.Invoke(GetPoint(lParam), MouseEventKind.MiddleUp, 0);
                handled = true;
                break;
            case 0x0200: // WM_MOUSEMOVE
                if (!_isTrackingMouseLeave)
                {
                    var tme = new TRACKMOUSEEVENT
                    {
                        cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = 0x00000002, // TME_LEAVE
                        hwndTrack = hwnd,
                        dwHoverTime = 0,
                    };
                    if (TrackMouseEvent(ref tme))
                        _isTrackingMouseLeave = true;
                }
                MouseAction?.Invoke(GetPoint(lParam), MouseEventKind.Move, 0);
                handled = true;
                break;
            case 0x02A3: // WM_MOUSELEAVE
                _isTrackingMouseLeave = false;
                MouseAction?.Invoke(new Point(-1, -1), MouseEventKind.Leave, 0);
                handled = true;
                break;
            case 0x020A: // WM_MOUSEWHEEL
                short delta = (short)((long)wParam >> 16);
                MouseAction?.Invoke(new Point(0, 0), MouseEventKind.Wheel, delta);
                handled = true;
                break;
            case 0x0087: // WM_GETDLGCODE
                handled = true;
                return new IntPtr(0x0004); // DLGC_WANTALLKEYS
            case 0x0100: // WM_KEYDOWN
            case 0x0104: // WM_SYSKEYDOWN
            {
                Key wpfKey = ResolveKeyFromWin32Message(wParam, lParam);
                if (wpfKey != Key.None)
                {
                    KeyAction?.Invoke(wpfKey, Keyboard.Modifiers);
                }
                handled = true;
                break;
            }
            case 0x0101: // WM_KEYUP
            case 0x0102: // WM_CHAR
            case 0x0103: // WM_DEADCHAR
            case 0x0105: // WM_SYSKEYUP
            case 0x0106: // WM_SYSCHAR
            case 0x010D: // WM_IME_STARTCOMPOSITION
            case 0x010F: // WM_IME_COMPOSITION
            case 0x0286: // WM_IME_CHAR
                handled = true;
                return IntPtr.Zero;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void UpdatePixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        DpiScaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        DpiScaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        double w = ActualWidth > 0 ? ActualWidth * DpiScaleX : 1.0;
        double h = ActualHeight > 0 ? ActualHeight * DpiScaleY : 1.0;
        _pixelWidth = Math.Max(1, (int)Math.Round(w));
        _pixelHeight = Math.Max(1, (int)Math.Round(h));
    }

    private void CreateSwapChainAndTargets()
    {
        if (_device == null || Handle == IntPtr.Zero || _pixelWidth <= 0 || _pixelHeight <= 0) return;

        CleanupTargets();
        _swapChain?.Dispose();
        _swapChain = null;

        try
        {
            using var dxgiDev = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDev.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory1>();

            var desc = new SwapChainDescription
            {
                BufferCount = 2,
                BufferDescription = new ModeDescription(
                    _pixelWidth, _pixelHeight, new Rational(60, 1), Format.R8G8B8A8_UNorm),
                BufferUsage = Usage.RenderTargetOutput,
                OutputWindow = Handle,
                SampleDescription = new SampleDescription(1, 0),
                Windowed = true,
                SwapEffect = SwapEffect.Discard,
            };

            _swapChain = factory.CreateSwapChain(_device, desc);
            factory.MakeWindowAssociation(Handle, WindowAssociationFlags.IgnoreAltEnter);
            CreateRtvAndDepth();
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"ViewportD3D11Host SwapChain 作成失敗: {ex.Message}");
            CleanupTargets();
            _swapChain?.Dispose();
            _swapChain = null;
        }
    }

    private void ResizeOrCreateTargets()
    {
        if (_device == null || _swapChain == null || _pixelWidth <= 0 || _pixelHeight <= 0)
            return;

        CleanupTargets();
        try
        {
            var hr = _swapChain.ResizeBuffers(0, _pixelWidth, _pixelHeight, Format.Unknown, SwapChainFlags.None);
            if (hr.Failure)
            {
                CreateSwapChainAndTargets();
                return;
            }
            CreateRtvAndDepth();
        }
        catch
        {
            CreateSwapChainAndTargets();
        }
    }

    private void CreateRtvAndDepth()
    {
        if (_device == null || _swapChain == null) return;

        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(backBuffer);

        _depthTex = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = _pixelWidth,
            Height = _pixelHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        });
        _dsv = _device.CreateDepthStencilView(_depthTex);
    }

    private void CleanupTargets()
    {
        _dsv?.Dispose(); _dsv = null;
        _depthTex?.Dispose(); _depthTex = null;
        _rtv?.Dispose(); _rtv = null;
    }

    private static bool TryCreateMatchingDevice(
        DeviceCreationFlags flags,
        FeatureLevel[] levels,
        out ID3D11Device? device,
        out ID3D11DeviceContext? context,
        out long? adapterLuid)
    {
        device = null;
        context = null;
        adapterLuid = null;

        if (Viewport3DBridge.TryGetYmmAdapterLuid(out long targetLuid))
        {
            try
            {
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                for (int i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
                {
                    using (adapter)
                    {
                        var luid = adapter.Description1.Luid;
                        if (Viewport3DBridge.PackLuid(luid.HighPart, luid.LowPart) != targetLuid)
                            continue;

                        var hrMatch = D3D11.D3D11CreateDevice(
                            adapter, DriverType.Unknown, flags, levels, out device, out context);
                        if (hrMatch.Success && device != null && context != null)
                        {
                            adapterLuid = targetLuid;
                            return true;
                        }
                        device?.Dispose(); device = null;
                        context?.Dispose(); context = null;
                    }
                }
            }
            catch { }
        }

        var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out device, out context);
        if (hr.Failure || device == null || context == null)
        {
            device?.Dispose(); device = null;
            context?.Dispose(); context = null;
            hr = D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, levels, out device, out context);
        }
        if (hr.Failure || device == null || context == null)
        {
            device?.Dispose(); device = null;
            context?.Dispose(); context = null;
            return false;
        }

        adapterLuid = QueryDeviceAdapterLuid(device);
        return true;
    }

    private static long? QueryDeviceAdapterLuid(ID3D11Device device)
    {
        try
        {
            using var dxgiDev = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDev.GetAdapter();
            var luid = adapter.Description.Luid;
            return Viewport3DBridge.PackLuid(luid.HighPart, luid.LowPart);
        }
        catch
        {
            return null;
        }
    }

    private void ResetDeviceAndSwapChain()
    {
        if (_device != null)
        {
            Model3D.ModelGpuCache.ClearForDevice(_device.NativePointer);
        }
        CleanupTargets();
        _swapChain?.Dispose(); _swapChain = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
        _deviceAdapterLuid = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
                ResetDeviceAndSwapChain();
        }
        base.Dispose(disposing);
    }

    private static Point GetPoint(IntPtr lParam)
    {
        int raw = unchecked((int)lParam.ToInt64());
        return new Point((short)(raw & 0xFFFF), (short)(raw >> 16));
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static extern short RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, IntPtr lpClassName, IntPtr lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr GetStockObject(int fnObject);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);
}
