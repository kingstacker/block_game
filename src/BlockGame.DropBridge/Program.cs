using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace BlockGame.DropBridge;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (!BridgeOptions.TryParse(args, out BridgeOptions? options) || options is null)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        using var pipe = new NamedPipeClientStream(
            ".",
            options.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(15_000);
        }
        catch (Exception exception) when (
            exception is TimeoutException or UnauthorizedAccessException or IOException)
        {
            return;
        }

        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true
        };
        using var form = new DropBridgeForm(paths =>
        {
            writer.WriteLine(JsonSerializer.Serialize(new BridgeMessage
            {
                Type = "files",
                Nonce = options.Nonce,
                ProcessId = Environment.ProcessId,
                Paths = [.. paths]
            }));
        });
        form.Shown += (_, _) =>
        {
            writer.WriteLine(JsonSerializer.Serialize(new BridgeMessage
            {
                Type = "ready",
                Nonce = options.Nonce,
                ProcessId = Environment.ProcessId,
                WindowHandle = form.Handle.ToInt64(),
                IsElevated = IsAdministrator()
            }));
        };

        _ = MonitorParentAsync(reader, form, options.ParentProcessId);
        Application.Run(form);
    }

    private static async Task MonitorParentAsync(
        StreamReader reader,
        Form form,
        int parentProcessId)
    {
        try
        {
            using Process parent = Process.GetProcessById(parentProcessId);
            Task pipeClosed = reader.ReadToEndAsync();
            Task parentExited = parent.WaitForExitAsync();
            _ = await Task.WhenAny(pipeClosed, parentExited).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
        }
        catch (IOException)
        {
        }

        if (!form.IsDisposed && form.IsHandleCreated)
        {
            form.BeginInvoke(form.Close);
        }
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed class BridgeOptions
    {
        public required string PipeName { get; init; }
        public required string Nonce { get; init; }
        public required int ParentProcessId { get; init; }

        public static bool TryParse(string[] args, out BridgeOptions? options)
        {
            options = null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index + 1 < args.Length; index += 2)
            {
                values[args[index]] = args[index + 1];
            }

            if (!values.TryGetValue("--pipe", out string? pipeName)
                || !values.TryGetValue("--nonce", out string? nonce)
                || !values.TryGetValue("--parent-pid", out string? parentText)
                || string.IsNullOrWhiteSpace(pipeName)
                || !System.Text.RegularExpressions.Regex.IsMatch(pipeName, @"^[A-Za-z0-9.]+$")
                || nonce.Length != 64
                || !int.TryParse(parentText, out int parentProcessId)
                || parentProcessId <= 0)
            {
                return false;
            }

            options = new BridgeOptions
            {
                PipeName = pipeName,
                Nonce = nonce,
                ParentProcessId = parentProcessId
            };
            return true;
        }
    }

    private sealed class BridgeMessage
    {
        public string Type { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public long WindowHandle { get; set; }
        public bool IsElevated { get; set; }
        public string[]? Paths { get; set; }
    }
}
