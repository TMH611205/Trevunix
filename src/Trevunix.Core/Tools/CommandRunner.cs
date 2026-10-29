using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Trevunix.Core.Tools;

/// <summary>
///     Turns raw console output into lines. A bare carriage return (used by progress bars
///     such as DISM's) rewrites the current line instead of starting a new one.
/// </summary>
public sealed class LineAssembler(Action<string, bool> onLine)
{
    private readonly StringBuilder _current = new();
    private bool _pendingReturn;
    private bool _currentShown;

    public void Push(ReadOnlySpan<char> chunk)
    {
        foreach (var c in chunk)
        {
            if (c == '\0')
                continue;

            if (c == '\r')
            {
                _pendingReturn = true;
                continue;
            }

            if (c == '\n')
            {
                Emit(final: true);
                _pendingReturn = false;
                continue;
            }

            if (_pendingReturn)
            {
                // "\r" not followed by "\n": the same line is being redrawn.
                Emit(final: false);
                _current.Clear();
                _pendingReturn = false;
            }

            _current.Append(c);
        }
    }

    public void Flush()
    {
        if (_current.Length > 0)
            Emit(final: true);
    }

    private void Emit(bool final)
    {
        var text = _current.ToString().TrimEnd();
        if (text.Length > 0 || final)
        {
            if (text.Length > 0 || _currentShown)
                onLine(text, _currentShown);
            _currentShown = !final && text.Length > 0;
        }

        if (final)
        {
            _current.Clear();
            _currentShown = false;
        }
    }
}

public enum OutputEncoding
{
    /// <summary>The console code page; what DISM, netsh and ipconfig write.</summary>
    Oem,

    /// <summary>UTF-16; what sfc.exe writes when redirected.</summary>
    Unicode,
}

/// <summary>Runs a console program and streams its output line by line. No timeout: SFC takes a while.</summary>
public static class CommandRunner
{
    static CommandRunner() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <param name="onLine">Receives each line; the flag is true when it replaces the previous line.</param>
    public static async Task<int> RunAsync(
        string fileName,
        string arguments,
        OutputEncoding encoding,
        Action<string, bool> onLine,
        CancellationToken cancellationToken = default
    )
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Resolve(encoding),
            StandardErrorEncoding = Resolve(encoding),
        };

        using var process = Process.Start(info)!;
        var gate = new Lock();
        var assembler = new LineAssembler(
            (line, replace) =>
            {
                lock (gate)
                    onLine(line, replace);
            }
        );

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
        });

        var stdout = Pump(process.StandardOutput, assembler);
        var stderr = Pump(process.StandardError, new LineAssembler(onLine));
        await Task.WhenAll(stdout, stderr);
        await process.WaitForExitAsync(CancellationToken.None);
        assembler.Flush();

        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    /// <summary>
    ///     Runs a short console program to completion and returns its exit code and output;
    ///     code -1 when it could not start. For quick helpers such as schtasks and powercfg.
    /// </summary>
    public static (int Code, string Output) Run(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info)!;
            // Read stderr alongside stdout so neither pipe can fill up and stall the program.
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            errors.Wait();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "");
        }
    }

    private static async Task Pump(StreamReader reader, LineAssembler assembler)
    {
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
            assembler.Push(buffer.AsSpan(0, read));
        assembler.Flush();
    }

    private static Encoding Resolve(OutputEncoding encoding) =>
        encoding == OutputEncoding.Unicode
            ? Encoding.Unicode
            : Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
}

public sealed record RepairCommand(
    string Id,
    string FileName,
    string Arguments,
    OutputEncoding Encoding,
    bool NeedsReboot = false,
    bool LongRunning = false
);

public static class RepairCatalog
{
    public static IReadOnlyList<RepairCommand> All { get; } =
    [
        new("Sfc", "sfc.exe", "/scannow", OutputEncoding.Unicode, LongRunning: true),
        new(
            "DismRestore",
            "dism.exe",
            "/Online /Cleanup-Image /RestoreHealth",
            OutputEncoding.Oem,
            LongRunning: true
        ),
        new(
            "DismComponentCleanup",
            "dism.exe",
            "/Online /Cleanup-Image /StartComponentCleanup",
            OutputEncoding.Oem,
            LongRunning: true
        ),
        new("FlushDns", "ipconfig.exe", "/flushdns", OutputEncoding.Oem),
        new("ResetWinsock", "netsh.exe", "winsock reset", OutputEncoding.Oem, NeedsReboot: true),
        new("ResetTcpIp", "netsh.exe", "int ip reset", OutputEncoding.Oem, NeedsReboot: true),
        new("ResetStore", "wsreset.exe", "", OutputEncoding.Oem),
    ];
}
