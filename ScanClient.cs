using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace FileLens;

public static class ScanClient
{
    // Native filesystem queries can stall on an unavailable network drive. Keep
    // them in a disposable worker process so the window always remains responsive.
    public static async Task<ScanResult> RunAsync(string path, CancellationToken cancellation)
    {
        string output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"FileLens-{Guid.NewGuid():N}.json");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--scan-json");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(output);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить проверку.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        // Closing the window shuts down the WPF dispatcher; terminate our worker
        // directly from cancellation instead of waiting for a UI continuation.
        using var stopWorker = combined.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        try
        {
            await process.WaitForExitAsync(combined.Token);
            combined.Token.ThrowIfCancellationRequested();
            if (!File.Exists(output)) throw new IOException("Проверка завершилась без результата. Попробуй запустить программу от администратора.");
            var envelope = JsonSerializer.Deserialize<ScanEnvelope>(await File.ReadAllTextAsync(output, cancellation));
            if (envelope?.Result is not null) return envelope.Result;
            throw new IOException(envelope?.Error ?? "Не удалось прочитать результат проверки.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("Проверка заняла больше минуты. Файл или сетевой диск могут быть недоступны.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            // Wait after termination before removing the worker's output file.
            try { if (!process.HasExited) await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
            try { File.Delete(output); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
