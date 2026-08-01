using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace BlockGame.App;

internal sealed class DropBridgeHost : IDisposable
{
    private const int OwnerWindowIndex = -8;
    private const int HideWindow = 0;
    private const uint PositionNoActivate = 0x0010;
    private const uint PositionShowWindow = 0x0040;
    private const uint PositionNoOwnerZOrder = 0x0200;
    private const uint WmClose = 0x0010;

    private readonly Window _owner;
    private readonly FrameworkElement _dropHost;
    private readonly Action<IReadOnlyList<string>> _filesDropped;
    private readonly Action<bool>? _availabilityChanged;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly string _nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _pipeName = $"BlockGame.DropBridge.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private NamedPipeServerStream? _pipe;
    private nint _bridgeWindow;
    private int _bridgeProcessId;
    private PixelBounds? _lastBounds;
    private bool _temporarilyHidden;
    private bool _disposed;

    internal bool IsConnected => _bridgeWindow != 0;

    internal bool BridgeIsElevated { get; private set; }

    public DropBridgeHost(
        Window owner,
        FrameworkElement dropHost,
        Action<IReadOnlyList<string>> filesDropped,
        Action<bool>? availabilityChanged = null)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _dropHost = dropHost ?? throw new ArgumentNullException(nameof(dropHost));
        _filesDropped = filesDropped ?? throw new ArgumentNullException(nameof(filesDropped));
        _availabilityChanged = availabilityChanged;

        _owner.LayoutUpdated += Owner_LayoutUpdated;
        _owner.LocationChanged += Owner_PositionChanged;
        _owner.StateChanged += Owner_PositionChanged;
        _owner.IsVisibleChanged += Owner_IsVisibleChanged;
        _dropHost.IsVisibleChanged += Owner_IsVisibleChanged;
    }

    public void Start()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DropBridgeHost));
        }

        _ = RunAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _owner.LayoutUpdated -= Owner_LayoutUpdated;
        _owner.LocationChanged -= Owner_PositionChanged;
        _owner.StateChanged -= Owner_PositionChanged;
        _owner.IsVisibleChanged -= Owner_IsVisibleChanged;
        _dropHost.IsVisibleChanged -= Owner_IsVisibleChanged;
        _cancellation.Cancel();
        if (_bridgeWindow != 0)
        {
            _ = PostMessage(_bridgeWindow, WmClose, 0, 0);
            _bridgeWindow = 0;
        }
        _pipe?.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            string? helperPath = FindHelperPath();
            if (helperPath is null)
            {
                return;
            }

            string helperDirectory = Path.GetDirectoryName(helperPath)
                ?? AppContext.BaseDirectory;

            _pipe = CreatePipeForCurrentUser(_pipeName);
            string arguments = string.Join(
                ' ',
                "--pipe", _pipeName,
                "--nonce", _nonce,
                "--parent-pid", Environment.ProcessId.ToString());
            _bridgeProcessId = UnelevatedProcessLauncher.Start(
                helperPath,
                arguments,
                helperDirectory);

            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                _cancellation.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out uint clientProcessId)
                || clientProcessId != _bridgeProcessId)
            {
                throw new InvalidDataException("快捷方式拖放组件身份校验失败。");
            }

            using var reader = new StreamReader(
                _pipe,
                new UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);
            string? readyLine = await reader.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
            BridgeMessage ready = ParseMessage(readyLine);
            if (ready.Type != "ready"
                || ready.Nonce != _nonce
                || ready.ProcessId != _bridgeProcessId
                || ready.WindowHandle == 0)
            {
                throw new InvalidDataException("快捷方式拖放组件握手失败。");
            }

            BridgeIsElevated = ready.IsElevated;
            await _owner.Dispatcher.InvokeAsync(() => AttachWindow((nint)ready.WindowHandle));

            while (!_cancellation.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                BridgeMessage message = ParseMessage(line);
                if (message.Type != "files" || message.Nonce != _nonce || message.Paths is null)
                {
                    continue;
                }

                string[] paths = message.Paths
                    .Where(path => !string.IsNullOrWhiteSpace(path) && path.Length <= short.MaxValue)
                    .Take(256)
                    .ToArray();
                if (paths.Length == 0)
                {
                    continue;
                }

                await _owner.Dispatcher.InvokeAsync(() =>
                {
                    _temporarilyHidden = true;
                    UpdateWindowBounds();
                    try
                    {
                        _filesDropped(paths);
                    }
                    finally
                    {
                        _temporarilyHidden = false;
                        UpdateWindowBounds(force: true);
                    }
                });
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine("Drop bridge unavailable: " + exception.Message);
        }
        finally
        {
            if (!_disposed)
            {
                await _owner.Dispatcher.InvokeAsync(() =>
                {
                    if (_bridgeWindow != 0)
                    {
                        _ = ShowWindow(_bridgeWindow, HideWindow);
                        _bridgeWindow = 0;
                    }
                    _availabilityChanged?.Invoke(false);
                });
            }
        }
    }

    private static string? FindHelperPath()
    {
        const string helperFileName = "BlockGame.DropBridge.exe";
        string baseDirectory = AppContext.BaseDirectory;
        string installedCandidate = Path.Combine(baseDirectory, helperFileName);
        if (File.Exists(installedCandidate))
        {
            return installedCandidate;
        }

        // scripts/build.ps1 keeps each published executable in its own directory.
        // Support launching the app directly from artifacts/publish/app as well as
        // from the final installation directory.
        string publishedCandidate = Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "dropbridge",
            helperFileName));
        if (File.Exists(publishedCandidate))
        {
            return publishedCandidate;
        }

        // dotnet build also emits the two executable projects into sibling project
        // directories. This keeps local Debug/Release runs consistent with installs.
        var targetFrameworkDirectory = new DirectoryInfo(baseDirectory);
        DirectoryInfo? configurationDirectory = targetFrameworkDirectory.Parent;
        DirectoryInfo? binDirectory = configurationDirectory?.Parent;
        DirectoryInfo? appProjectDirectory = binDirectory?.Parent;
        DirectoryInfo? sourceDirectory = appProjectDirectory?.Parent;
        if (configurationDirectory is not null
            && binDirectory is not null
            && appProjectDirectory is not null
            && sourceDirectory is not null
            && string.Equals(binDirectory.Name, "bin", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                appProjectDirectory.Name,
                "BlockGame.App",
                StringComparison.OrdinalIgnoreCase))
        {
            string buildCandidate = Path.Combine(
                sourceDirectory.FullName,
                "BlockGame.DropBridge",
                "bin",
                configurationDirectory.Name,
                targetFrameworkDirectory.Name,
                helperFileName);
            if (File.Exists(buildCandidate))
            {
                return buildCandidate;
            }
        }

        return null;
    }

    private static NamedPipeServerStream CreatePipeForCurrentUser(string pipeName)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier userSid = identity.User
            ?? throw new InvalidOperationException("无法读取当前 Windows 用户标识。");
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        pipeSecurity.SetOwner(userSid);
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            userSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096,
            4096,
            pipeSecurity,
            HandleInheritability.None);
    }

    private void AttachWindow(nint bridgeWindow)
    {
        nint ownerHandle = new WindowInteropHelper(_owner).Handle;
        if (ownerHandle == 0 || !IsWindow(bridgeWindow))
        {
            return;
        }

        _bridgeWindow = bridgeWindow;
        _ = SetWindowLongPtr(bridgeWindow, OwnerWindowIndex, ownerHandle);
        _availabilityChanged?.Invoke(true);
        UpdateWindowBounds(force: true);
    }

    private void UpdateWindowBounds(bool force = false)
    {
        if (_bridgeWindow == 0 || !IsWindow(_bridgeWindow))
        {
            return;
        }

        bool shouldShow = !_temporarilyHidden
            && _owner.IsVisible
            && _owner.WindowState != WindowState.Minimized
            && _dropHost.IsVisible
            && _dropHost.ActualWidth >= 2
            && _dropHost.ActualHeight >= 2;
        if (!shouldShow)
        {
            _ = ShowWindow(_bridgeWindow, HideWindow);
            _lastBounds = null;
            return;
        }

        Point topLeft;
        Point bottomRight;
        try
        {
            topLeft = _dropHost.PointToScreen(new Point(0, 0));
            bottomRight = _dropHost.PointToScreen(
                new Point(_dropHost.ActualWidth, _dropHost.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            _ = ShowWindow(_bridgeWindow, HideWindow);
            _lastBounds = null;
            return;
        }
        var bounds = new PixelBounds(
            checked((int)Math.Round(topLeft.X)),
            checked((int)Math.Round(topLeft.Y)),
            Math.Max(2, checked((int)Math.Round(bottomRight.X - topLeft.X))),
            Math.Max(2, checked((int)Math.Round(bottomRight.Y - topLeft.Y))));
        if (!force && bounds == _lastBounds)
        {
            return;
        }

        _lastBounds = bounds;
        _ = SetWindowPos(
            _bridgeWindow,
            0,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            PositionNoActivate | PositionShowWindow | PositionNoOwnerZOrder);
    }

    private void Owner_LayoutUpdated(object? sender, EventArgs e) => UpdateWindowBounds();

    private void Owner_PositionChanged(object? sender, EventArgs e)
        => UpdateWindowBounds(force: true);

    private void Owner_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => UpdateWindowBounds(force: true);

    private static BridgeMessage ParseMessage(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 1_000_000)
        {
            throw new InvalidDataException("快捷方式拖放组件消息无效。");
        }

        return JsonSerializer.Deserialize<BridgeMessage>(line)
            ?? throw new InvalidDataException("快捷方式拖放组件消息为空。");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtr(
        nint windowHandle,
        int index,
        nint newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        nint windowHandle,
        uint message,
        nint wordParameter,
        nint longParameter);

    private sealed class BridgeMessage
    {
        public string Type { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public long WindowHandle { get; set; }
        public bool IsElevated { get; set; }
        public string[]? Paths { get; set; }
    }

    private sealed record PixelBounds(int X, int Y, int Width, int Height);
}
