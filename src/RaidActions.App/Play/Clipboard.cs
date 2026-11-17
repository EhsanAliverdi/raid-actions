using System.Diagnostics;

namespace RaidActions.App.Play;

/// <summary>
/// The Windows clipboard's text, through PowerShell (the bot has no UI thread). Used to put back what the game's Get Referral Link overwrote (2026-11-18).
/// Failures are ignored: the clipboard is a convenience, never a reason to stop.
/// </summary>
internal static class Clipboard
{
    public static string? Read()
    {
        try
        {
            var p = Start("-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-Clipboard -Raw\"", redirectIn: false);
            var text = p.StandardOutput.ReadToEnd();
            return p.WaitForExit(5000) && p.ExitCode == 0 && text.Length > 0 ? text.TrimEnd('\r', '\n') : null;
        }
        catch (Exception) { return null; }
    }

    public static void Write(string text)
    {
        try
        {
            var p = Start("-NoProfile -NonInteractive -Command \"[Console]::InputEncoding=[Text.Encoding]::UTF8; Set-Clipboard -Value ([Console]::In.ReadToEnd())\"", redirectIn: true);
            p.StandardInput.Write(text);
            p.StandardInput.Close();
            p.WaitForExit(5000);
        }
        catch (Exception) { }
    }

    private static Process Start(string args, bool redirectIn) => Process.Start(new ProcessStartInfo("powershell.exe", args)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = redirectIn,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
    })!;
}
