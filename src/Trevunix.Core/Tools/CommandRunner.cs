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

    /// <summary>winget, and PowerShell told to write UTF-8.</summary>
    Utf8,

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
        encoding switch
        {
            OutputEncoding.Unicode => Encoding.Unicode,
            OutputEncoding.Utf8 => new UTF8Encoding(false),
            _ => Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
        };
}

public sealed record RepairCommand(
    string Id,
    string FileName,
    string Arguments,
    OutputEncoding Encoding,
    bool NeedsReboot = false,
    bool LongRunning = false,
    string NameArg = ""
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
        // /O picks the right job per drive: TRIM for SSDs, defragmentation for hard disks.
        new("OptimizeDrives", "defrag.exe", "/C /O /U", OutputEncoding.Oem, LongRunning: true),
    ];

    private const string Winget = "winget.exe";
    private const string WingetCommon = "--accept-source-agreements --disable-interactivity";

    /// <summary>
    ///     Overwrites the free space of each fixed drive three times (zeros, ones, random) with
    ///     cipher.exe, so deleted files cannot be recovered. Files in use stay untouched.
    /// </summary>
    public static IReadOnlyList<RepairCommand> WipeFreeSpace() =>
        [
            .. DriveInfo
                .GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => new RepairCommand(
                    "WipeFreeSpace",
                    "cipher.exe",
                    $"/w:{d.Name}",
                    OutputEncoding.Oem,
                    LongRunning: true,
                    NameArg: d.Name.TrimEnd('\\')
                )),
        ];

    /// <summary>App updates through winget, which ships with Windows 10 and 11.</summary>
    public static IReadOnlyList<RepairCommand> AppUpdates { get; } =
    [
        new("WingetList", Winget, $"upgrade --include-unknown {WingetCommon}", OutputEncoding.Utf8),
        new(
            "WingetUpgradeAll",
            Winget,
            $"upgrade --all --silent --accept-package-agreements {WingetCommon}",
            OutputEncoding.Utf8,
            LongRunning: true
        ),
    ];

    private const string PowerShellUtf8 =
        "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; ";

    /// <summary>Devices whose driver is missing or failing, and the installed third-party drivers.</summary>
    public static IReadOnlyList<RepairCommand> Drivers { get; } =
    [
        new(
            "DriverProblems",
            "powershell.exe",
            PowerShellUtf8
                + "$d = Get-PnpDevice -PresentOnly | Where-Object Status -ne 'OK'; "
                + "if ($d) { $d | Format-Table -AutoSize Status,Class,FriendlyName,InstanceId | Out-String -Width 300 } "
                + "else { 'OK' }\"",
            OutputEncoding.Utf8
        ),
        new(
            "DriverList",
            "powershell.exe",
            PowerShellUtf8
                + "Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DriverProviderName -and $_.DriverProviderName -ne 'Microsoft' } "
                + "| Sort-Object DriverDate | Format-Table -AutoSize DeviceName,DriverProviderName,DriverVersion,@{n='Date';e={if ($_.DriverDate) { $_.DriverDate.ToString('yyyy-MM-dd') }}} "
                + "| Out-String -Width 300\"",
            OutputEncoding.Utf8
        ),
    ];
}
