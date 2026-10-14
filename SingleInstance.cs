using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FileLens;

internal sealed class ActivationRequest
{
    public string Action { get; set; } = "Activate";
    public string? Path { get; set; }
}

internal sealed class ActivationReply
{
    public int Pid { get; set; } = Environment.ProcessId;
    public bool Accepted { get; set; } = true;
    public string? Path { get; set; }
    public bool Scanning { get; set; }
    public bool Minimized { get; set; }
    public long WindowHandle { get; set; }
    public int[] ProcessIds { get; set; } = [];
}

internal sealed class SingleInstance : IDisposable
{
    private readonly SafeWaitHandle mutex;
    private readonly CancellationTokenSource cancellation = new();
    private NamedPipeServerStream? pipe;
    private bool ownsMutex;
    internal static string PipeName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            string testScope = Environment.GetEnvironmentVariable("FILELENS_TEST_INSTANCE_SCOPE") ?? "";
            // A test namespace keeps integration checks away from a user's window.
            if (testScope.Length > 0 && !Guid.TryParseExact(testScope, "N", out _)) throw new ArgumentException("Invalid integration-test scope.");
            return $"FileLens.Activation.{identity.User!.Value}.{process.SessionId}{(testScope.Length > 0 ? ".Test." + testScope : "")}";
        }
    }

    internal SingleInstance()
    {
        mutex = InstanceNative.WithUserSecurity((ref InstanceNative.SecurityAttributes security) =>
            InstanceNative.CreateMutex(ref security, false, @"Local\" + PipeName));
        if (mutex.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal bool TryOwn()
    {
        if (ownsMutex) return true;
        uint result = InstanceNative.WaitForSingleObject(mutex, 0);
        if (result is 0 or 0x80) return ownsMutex = true;
        if (result == 0x102) return false;
        throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal void StartServer(Func<ActivationRequest, Task<ActivationReply>> receive)
    {
        var handle = InstanceNative.WithUserSecurity((ref InstanceNative.SecurityAttributes security) =>
            InstanceNative.CreateNamedPipe(@"\\.\pipe\" + PipeName, 0x40000003, 0x8, 1, 4096, 4096, 0, ref security));
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        pipe = new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        _ = ServeAsync(receive);
    }

    private async Task ServeAsync(Func<ActivationRequest, Task<ActivationReply>> receive)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await pipe!.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await WriteAsync(pipe, new ActivationReply(), deadline.Token).ConfigureAwait(false);
                var request = await ReadAsync<ActivationRequest>(pipe, deadline.Token).ConfigureAwait(false);
                var reply = await receive(request).WaitAsync(deadline.Token).ConfigureAwait(false);
                await WriteAsync(pipe, reply, deadline.Token).ConfigureAwait(false);
                // DisconnectNamedPipe discards unread data. Wait until the client
                // confirms that it consumed the reply before reusing this server.
                byte[] consumed = new byte[1];
                await pipe.ReadExactlyAsync(consumed, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
            { if (cancellation.IsCancellationRequested) break; }
            finally
            {
                try { if (pipe?.IsConnected == true) pipe.Disconnect(); } catch (IOException) { } catch (ObjectDisposedException) { }
            }
        }
    }

    internal static async Task<ActivationReply> SendAsync(ActivationRequest request, int timeoutMilliseconds = 10000)
    {
        using var deadline = new CancellationTokenSource(timeoutMilliseconds);
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
        var greeting = await ReadAsync<ActivationReply>(client, deadline.Token).ConfigureAwait(false);
        if (request.Action == "Activate") InstanceNative.AllowSetForegroundWindow(greeting.Pid);
        await WriteAsync(client, request, deadline.Token).ConfigureAwait(false);
        var reply = await ReadAsync<ActivationReply>(client, deadline.Token).ConfigureAwait(false);
        await client.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
        return reply;
    }

    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(data, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 262144) throw new IOException("Invalid activation message length.");
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(data) ?? throw new IOException("Empty activation message.");
    }

    public void Dispose()
    {
        cancellation.Cancel();
        pipe?.Dispose();
        if (ownsMutex) { InstanceNative.ReleaseMutex(mutex); ownsMutex = false; }
        mutex.Dispose();
    }
}

internal static class InstanceNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    internal delegate T CreateWithSecurity<T>(ref SecurityAttributes security);
    internal static T WithUserSecurity<T>(CreateWithSecurity<T> create)
    {
        using var identity = WindowsIdentity.GetCurrent();
        // Same user only. A medium mandatory label permits Explorer to reach an
        // elevated FileLens owned by that user; remote pipe clients are rejected.
        string sddl = $"D:P(A;;GA;;;{identity.User!.Value})S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            return create(ref security);
        }
        finally { LocalFree(descriptor); }
    }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint length);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    [DllImport("kernel32.dll", EntryPoint = "CreateMutexW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeWaitHandle CreateMutex(ref SecurityAttributes security, [MarshalAs(UnmanagedType.Bool)] bool owned, string name);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeWaitHandle mutex, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReleaseMutex(SafeWaitHandle mutex);
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances, uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes security);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindowAsync(IntPtr window, int show);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
