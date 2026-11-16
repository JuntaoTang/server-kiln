using System.Diagnostics;
using System.Text;

namespace ServerKiln.Windows.Core;

public interface ICollectorSession : IAsyncDisposable
{
    Task<ServerSnapshot> FetchAsync(CancellationToken cancellationToken = default);
}

/// <summary>One native OpenSSH process per host. Uses redirected UTF-8 pipes, never a local shell.</summary>
public sealed class SshSession(ServerConfig server, string? executable = null, string? configPath = null,
    TimeSpan? requestTimeout = null) : ICollectorSession
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private Task? stderrReader;
    private readonly StringBuilder stderr = new();
    private bool disposed;
    private const int MaxFrameCharacters = 8 * 1024 * 1024;
    private static readonly Lazy<string> Collector = new(() =>
    {
        using var stream = typeof(SshSession).Assembly.GetManifestResourceStream("ServerKiln.collect-loop.sh")
            ?? throw new InvalidOperationException("Missing collector resource.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    });
    public static string CollectorCommand => "sh -c 'eval \"$(echo "
        + Convert.ToBase64String(Encoding.UTF8.GetBytes(Collector.Value)) + " | base64 -d)\"'";

    public static string ResolveExecutable(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (!File.Exists(full)) throw new FileNotFoundException("SSH executable not found. / 找不到 SSH 程序。", full);
            return full;
        }
        var native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
        if (File.Exists(native)) return native;
        // Do not search the working directory for an executable.
        throw new FileNotFoundException("Install the Windows OpenSSH Client optional feature, or select ssh.exe in Settings. / 请安装 Windows 的 OpenSSH 客户端，或在设置中指定 ssh.exe。");
    }

    public static ProcessStartInfo CreateStartInfo(ServerConfig server, string? executable = null, string? configPath = null)
    {
        server.Validate();
        var info = new ProcessStartInfo(ResolveExecutable(executable))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            var full = Path.GetFullPath(configPath);
            if (!File.Exists(full)) throw new FileNotFoundException("SSH configuration not found. / 找不到 SSH 配置文件。", full);
            info.ArgumentList.Add("-F"); info.ArgumentList.Add(full);
        }
        foreach (var argument in new[] { "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=8",
            "-o", "ClearAllForwardings=yes", "-o", "ForwardAgent=no", "-o", "ForwardX11=no", "-o", "Compression=yes",
            "-o", "LogLevel=ERROR", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3",
            "-o", "ControlMaster=no", "-o", "ControlPath=none" }) info.ArgumentList.Add(argument);
        if (server.Port is { } port) { info.ArgumentList.Add("-p"); info.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        info.ArgumentList.Add(server.Target);
        info.ArgumentList.Add(CollectorCommand);
        return info;
    }

    public async Task<ServerSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is null || process.HasExited) { await CloseProcessAsync().ConfigureAwait(false); Start(); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(20));
            try
            {
                await process!.StandardInput.WriteLineAsync(ReadOnlyMemory<char>.Empty, timeout.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                var frame = new StringBuilder();
                var chars = new char[4096];
                while (true)
                {
                    // StreamReader buffers partial lines. ReadAsync is bounded even without a newline.
                    var count = await process.StandardOutput.ReadAsync(chars.AsMemory(), timeout.Token).ConfigureAwait(false);
                    if (count == 0)
                    {
                        if (stderrReader is not null) await stderrReader.WaitAsync(TimeSpan.FromSeconds(1), timeout.Token).ConfigureAwait(false);
                        string message;
                        lock (stderr) message = stderr.ToString().Trim();
                        throw new IOException(string.IsNullOrEmpty(message) ? "SSH session closed. / SSH 连接已断开。" : message);
                    }
                    frame.Append(chars, 0, count);
                    if (frame.Length > MaxFrameCharacters) throw new IOException("SSH response exceeded the 8 MiB limit.");
                    if (EndsWith(frame, "@@END\n") || EndsWith(frame, "@@END\r\n"))
                    {
                        var output = frame.ToString();
                        if (!output.StartsWith("@@HOST\n", StringComparison.Ordinal) && !output.StartsWith("@@HOST\r\n", StringComparison.Ordinal)
                            && !output.Contains("\n@@HOST\n", StringComparison.Ordinal) && !output.Contains("\n@@HOST\r\n", StringComparison.Ordinal))
                            throw new IOException("Unrecognized collector response. / 无法识别采集结果。");
                        return SnapshotParser.Parse(output);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await CloseProcessAsync().ConfigureAwait(false);
                throw new TimeoutException("SSH collection timed out. / SSH 采集超时。");
            }
            catch
            {
                await CloseProcessAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private static bool EndsWith(StringBuilder value, string suffix)
    {
        if (value.Length < suffix.Length) return false;
        for (var i = 0; i < suffix.Length; i++)
            if (value[value.Length - suffix.Length + i] != suffix[i]) return false;
        return true;
    }

    private void Start()
    {
        lock (stderr) stderr.Clear();
        process = new Process { StartInfo = CreateStartInfo(server, executable, configPath) };
        if (!process.Start()) throw new IOException("Could not start SSH.");
        process.StandardInput.NewLine = "\n";
        var reader = process.StandardError;
        stderrReader = Task.Run(async () =>
        {
            var chars = new char[1024];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(chars).ConfigureAwait(false)) > 0)
                    lock (stderr)
                    {
                        stderr.Append(chars, 0, count);
                        if (stderr.Length > 8192) stderr.Remove(0, stderr.Length - 8192);
                    }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        });
    }

    private async Task CloseProcessAsync()
    {
        var old = process;
        process = null;
        if (old is null) return;
        try
        {
            if (!old.HasExited) old.Kill(entireProcessTree: true);
            await old.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
        finally { old.Dispose(); }
        if (stderrReader is not null)
        {
            try { await stderrReader.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            stderrReader = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { disposed = true; await CloseProcessAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
