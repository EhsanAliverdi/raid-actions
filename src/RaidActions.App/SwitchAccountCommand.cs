using System.Diagnostics;
using System.Runtime.InteropServices;
using RaidActions.Game.Memory;

namespace RaidActions.App;

/// <summary>
/// switch-account &lt;player id&gt;: the game on another account (raid-extractor plan/multi-account.md). Plarium Play switches only while Raid is closed, and
/// keeps every account signed in once (its "Add Account"), so no password is typed: Raid closed, Plarium Play's profile menu, the other account's avatar,
/// Raid started again, the player's id read back. The avatars are told apart by their profile names (hovered), learnt per account in plarium-profiles.json. A wrong account after the start is
/// tried once more, then refused.
/// </summary>
public static class SwitchAccountCommand
{
    // Plarium Play's menu, measured at 150% (2026-11-04) in its own logical pixels, from its window's left and bottom: the profile button, and the first of
    // "Other Accounts" (the menu grows upwards from the bottom).
    private const int ProfileX = 95, ProfileFromBottom = 42, OtherX = 49, OtherFromBottom = 92;

    public static async Task<int> RunAsync(string[] args, string outDir, TextWriter output)
    {
        if (args.Length < 2 || !long.TryParse(args[1], out var want)) { output.WriteLine("Usage: switch-account <player id>"); return Cli.Usage; }
        var first = InGame(outDir);
        if (first == want) { output.WriteLine($"The game is on account {want} already."); return Cli.Ok; }
        var profiles = LoadProfiles(outDir);
        // The profile signed in now is the account in the game: learnt (three accounts need to know which avatar is which; 2026-11-07).
        if (first is { } id0 && Process.GetProcessesByName("PlariumPlay").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) is { } pp0
            && PlariumPopup.CurrentProfile(pp0.MainWindowHandle) is { } current) Learn(outDir, profiles, current, id0, output);
        // Where each avatar leads, by the account the menu is opened on and the avatar's place (2026-11-10 13:20: no name read on hover, and the avatars'
        // order follows the account signed in, so "the next one" landed on the other two accounts three times).
        var places = LoadPlaces(outDir);
        var from = first;
        // Three tries: with three accounts a profile not known yet may be the wrong one (it is learnt, and the next try knows it).
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            CloseRaid(output);
            if (SwitchInPlariumPlay(output, want, profiles, attempt, from, places, out var picked, out var place) is { } problem) { output.WriteLine(problem); return Cli.Failed; }
            if (!await RestartGameCommand.StartAndWaitAsync(outDir, output, TimeSpan.FromMinutes(5))) return Cli.Failed;
            var now = InGame(outDir);
            output.WriteLine($"The game is on account {now?.ToString() ?? "(not read)"}.");
            // The name on the profile button now, not the text read when the avatar was hovered (2026-11-10 11:36: a banner's "EVENT" appeared on the hover
            // and was learnt as KaelStormVex's profile, its real name dropped).
            var signedIn = Process.GetProcessesByName("PlariumPlay").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) is { } ppNow
                ? PlariumPopup.CurrentProfile(ppNow.MainWindowHandle) : null;
            if ((signedIn ?? picked) is { } name && now is { } id) Learn(outDir, profiles, name, id, output);
            if (from is { } f && place is { } i && now is { } got && (!places.TryGetValue(PlaceKey(f, i), out var was) || was != got))
            {
                places[PlaceKey(f, i)] = got;
                output.WriteLine($"Plarium Play: from account {f}, avatar {i + 1} plays account {got} (learnt).");
                try { File.WriteAllText(PlacesPath(outDir), System.Text.Json.JsonSerializer.Serialize(places, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); }
                catch (IOException e) { output.WriteLine($"Plarium Play's avatar places not saved: {e.Message}"); }
            }
            if (now == want) return Cli.Ok;
            from = now;
        }
        output.WriteLine($"Account {want} was not reached.");
        return Cli.Failed;
    }

    /// <summary>
    /// The game on account <paramref name="want"/> before a run (Ehsan 2026-11-09: "if the game is not open it will open the correct account and apply").
    /// Raid closed: Plarium Play's profile already playing it starts the game; another known profile is switched first; a profile not known yet
    /// starts the game, and a wrong account is then switched. Raid open on another account: refused before any click (closing it could cut into
    /// what the person does), with what to do.
    /// </summary>
    public static async Task<int> EnsureAccountAsync(string outDir, TextWriter output, long want)
    {
        if (Process.GetProcessesByName("Raid").Length > 0)
        {
            var now = InGame(outDir);
            if (now == want) return Cli.Ok;
            output.WriteLine(now is { } other
                ? $"Raid is open on account {other}, not {want}: nothing was done. Close Raid and press Apply again (the bot then opens account {want}), or switch the account yourself."
                : "Raid is open but its account cannot be read yet: nothing was done. Wait until the village shows, then press Apply again.");
            return Cli.Refused;
        }
        var profiles = LoadProfiles(outDir);
        var current = Process.GetProcessesByName("PlariumPlay").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) is { } pp
            ? PlariumPopup.CurrentProfile(pp.MainWindowHandle) : null;
        long? knownId = current is not null && profiles.TryGetValue(current, out var id) ? id : null;
        if (knownId is { } k && k != want)
        {
            output.WriteLine($"Raid is closed and Plarium Play is on '{current}' (account {k}): switching to account {want} first.");
            return await RunAsync(["switch-account", want.ToString(System.Globalization.CultureInfo.InvariantCulture)], outDir, output);
        }
        output.WriteLine(knownId == want ? $"Raid is closed: starting it on '{current}' (account {want})." : "Raid is closed: starting it, then checking the account.");
        if (!await RestartGameCommand.StartAndWaitAsync(outDir, output, TimeSpan.FromMinutes(5))) return Cli.Failed;
        var started = InGame(outDir);
        if (current is not null && started is { } sid) Learn(outDir, profiles, current, sid, output);
        if (started == want) return Cli.Ok;
        output.WriteLine($"The game started on account {started?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(not read)"}: switching to {want}.");
        return await RunAsync(["switch-account", want.ToString(System.Globalization.CultureInfo.InvariantCulture)], outDir, output);
    }

    /// <summary>The player's id of the account in the game; null when the game is not running or not readable.</summary>
    public static long? InGame(string outDir)
    {
        var (game, _) = LiveGame.Open(GameCommands.LivePaths(outDir));
        if (game is null) return null;
        using (game) return game.UserId();
    }

    /// <summary>Plarium Play's profile names and the player id each one plays (learnt on switches; output, never committed).</summary>
    private static string ProfilesPath(string outDir) => Path.Combine(outDir, "plarium-profiles.json");

    private static string PlacesPath(string outDir) => Path.Combine(outDir, "plarium-avatar-places.json");

    internal static string PlaceKey(long from, int index) => $"{from}:{index}";

    private static Dictionary<string, long> LoadPlaces(string outDir)
    {
        try { return File.Exists(PlacesPath(outDir)) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(PlacesPath(outDir))) ?? [] : []; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return []; }
    }

    private static Dictionary<string, long> LoadProfiles(string outDir)
    {
        try { return File.Exists(ProfilesPath(outDir)) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(ProfilesPath(outDir))) ?? [] : []; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return []; }
    }

    private static void Learn(string outDir, Dictionary<string, long> profiles, string name, long id, TextWriter output)
    {
        if (profiles.TryGetValue(name, out var known) && known == id) return;
        // One profile per account: an old entry for this id under another name goes.
        foreach (var k in profiles.Where(kv => kv.Value == id && kv.Key != name).Select(kv => kv.Key).ToList()) profiles.Remove(k);
        profiles[name] = id;
        output.WriteLine($"Plarium Play: profile '{name}' plays account {id} (learnt).");
        try { File.WriteAllText(ProfilesPath(outDir), System.Text.Json.JsonSerializer.Serialize(profiles, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); }
        catch (IOException e) { output.WriteLine($"Plarium Play's profiles not saved: {e.Message}"); }
    }

    private static void CloseRaid(TextWriter output)
    {
        foreach (var p in Process.GetProcessesByName("Raid"))
        {
            output.WriteLine($"Closing Raid (PID {p.Id}) to switch accounts...");
            p.CloseMainWindow();
            if (!p.WaitForExit(30_000)) { p.Kill(); p.WaitForExit(10_000); }
        }
        Thread.Sleep(8000); // Plarium Play notices the game closed (it was 3 s; see SwitchInPlariumPlay)
    }

    private static string? SwitchInPlariumPlay(TextWriter output, long want, IReadOnlyDictionary<string, long> profiles, int attempt, long? from,
        IReadOnlyDictionary<string, long> places, out string? picked, out int? place)
    {
        picked = null;
        place = null;
        var pp = Process.GetProcessesByName("PlariumPlay").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (pp is null) return "Plarium Play's window is not open.";
        // No click reaches a disconnected or locked session and every pixel reads blank (2026-11-06 05:31: Remote Desktop left mid-switch, and "the game is
        // still running" was read for two minutes): refused, and the next bot hands the session to the console and tries again.
        if (Input.SessionGuard.WaitActive(TimeSpan.FromMinutes(3)) is { } session) return $"The account was not switched: {session}";
        var old = SetThreadDpiAwarenessContext(new IntPtr(-4)); // physical pixels, like the window's rectangle
        try
        {
            var h = pp.MainWindowHandle;
            if (IsIconic(h)) ShowWindow(h, 9);
            SetForegroundWindow(h);
            Thread.Sleep(1200);
            if (!GetWindowRect(h, out var r)) return "Plarium Play's window could not be measured.";
            // Its profile button is at the bottom of its sidebar: the window must be on the screen (2026-11-05: a 1162x540 Remote Desktop screen and a
            // 1080x720 window, the button under the screen's edge, and the switch failed). Moved to the top left and made the screen's size, then measured again.
            // Only its bottom left is used (the profile button and the menu that opens above it): a window taller than the screen (2026-11-05: 2160x1440 on a
            // 2324x1080 phone screen; it does not shrink below that) goes up until its bottom is on the screen, its title bar off the top.
            var (screenW, screenH) = (GetSystemMetrics(0), GetSystemMetrics(1));
            // A maximized window hangs its invisible frame over the screen's edges (2026-11-05: 1936x1056 at -8,-8 on a 1920x1080 console): that much is on it.
            var frame = (int)(16 * GetDpiForWindow(h) / 96.0);
            bool Off(Rect w) => w.L < -frame || w.B > screenH + frame || w.R > screenW + frame;
            if (Off(r))
            {
                var (w0, h0) = (Math.Min(r.R - r.L, screenW), Math.Min(r.B - r.T, screenH));
                MoveWindow(h, 0, 0, w0, h0, true);
                Thread.Sleep(800);
                if (!GetWindowRect(h, out r)) return "Plarium Play's window could not be measured.";
                if (r.B > screenH) MoveWindow(h, 0, screenH - (r.B - r.T), r.R - r.L, r.B - r.T, true);
                Thread.Sleep(1200);
                if (!GetWindowRect(h, out r)) return "Plarium Play's window could not be measured.";
                // Only its bottom left must be on the screen (2026-11-05: after a phone's Remote Desktop at 200% left, the window stayed 2160x1440 on the
                // PC's 1920x1080 screen, its right side off it, and the switch was refused).
                if (r.L < -frame || r.B > screenH + frame || r.L + (int)(400 * GetDpiForWindow(h) / 96.0) > screenW || r.B - (int)(260 * GetDpiForWindow(h) / 96.0) < 0)
                    return $"Plarium Play's window ({r.R - r.L}x{r.B - r.T}) does not fit the screen ({screenW}x{screenH}): the account was not switched.";
                output.WriteLine($"Plarium Play's window moved so its bottom is on the screen ({r.L},{r.T} {r.R - r.L}x{r.B - r.T}).");
            }
            var scale = GetDpiForWindow(h) / 96.0;
            var (otherX, otherY) = (r.L + (int)(OtherX * scale), r.B - (int)(OtherFromBottom * scale));
            // Plarium Play still counts the game as running for a while after it closed (2026-11-04: both tries failed): its menu then shows "You cannot
            // log out ... while games are running" (a plain grey box) where the other account's avatar is. Opened, looked at, closed and tried again.
            for (var look = 0; look < 12; look++)
            {
                if (Input.SessionGuard.WaitActive(TimeSpan.FromMinutes(3)) is { } lost) return $"The account was not switched: {lost}";
                // A Plarium Play dialog over the window (2026-11-04: "Enjoying Raid? Rate", the window dimmed under it) takes every click: Esc closes it,
                // and the window is looked at again (the empty sidebar between Settings and the profile is light when nothing covers it).
                for (var esc = 0; esc < 3 && Dimmed(r, scale); esc++)
                {
                    output.WriteLine("Plarium Play has a dialog open: Esc.");
                    keybd_event(0x1B, 0, 0, IntPtr.Zero);
                    keybd_event(0x1B, 0, 2, IntPtr.Zero);
                    Thread.Sleep(1200);
                }
                // A popup Esc does not close (2026-11-05: Plarium Play's marketing box): its close button, found by name and pressed (PlariumPopup).
                for (var tries = 0; tries < 3 && Dimmed(r, scale) && PlariumPopup.Close(h, output) is { } closed; tries++)
                {
                    output.WriteLine($"Plarium Play's popup closed with its {closed} button.");
                    Thread.Sleep(1500);
                }
                if (Dimmed(r, scale))
                {
                    // What was seen, to tell a real dialog from a sidebar that is dark here (2026-11-05: on a phone's Remote Desktop at 200%).
                    var (px, py) = (r.L + (int)(30 * scale), r.T + (r.B - r.T) * 2 / 3);
                    var dc = GetDC(IntPtr.Zero);
                    var c = GetPixel(dc, px, py);
                    ReleaseDC(IntPtr.Zero, dc);
                    return $"Plarium Play has a dialog open that neither Esc nor its close button closes (the sidebar at {px},{py} reads #{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}; window {r.L},{r.T} {r.R - r.L}x{r.B - r.T}, scale {scale}): the account was not switched.";
                }
                Click(r.L + (int)(ProfileX * scale), r.B - (int)(ProfileFromBottom * scale));
                Thread.Sleep(1800);
                // The other account's avatar, found on the screen (2026-11-04: on the PC's own monitor at 100% the menu's rows are not where they were at
                // 150% over Remote Desktop, and the fixed spot missed between the avatar and "+ Add Account").
                // The avatar's own button, through UI Automation (no pixel read: works under a window on top or a box sliding in).
                // Each avatar's profile name (hovered: the name shows in place of "Other Accounts"), and the one that plays the account wanted.
                var others = PlariumPopup.OtherAccounts(h, (x, y) => SetCursorPos(x, y), output);
                for (var wait = 0; wait < 4 && others.Count == 0; wait++)
                {
                    Thread.Sleep(1000);
                    others = PlariumPopup.OtherAccounts(h, (x, y) => SetCursorPos(x, y), output);
                }
                if (others.Count > 0)
                {
                    var names = others.Select(o => o.Name).ToList();
                    output.WriteLine($"Plarium Play's other accounts: {string.Join(", ", names.Select(n => n is null ? "(name not read)" : profiles.TryGetValue(n, out var pid) ? $"{n} ({pid})" : $"{n} (not known yet)"))}.");
                    Func<int, long?> ledTo = from is { } f ? i => places.TryGetValue(PlaceKey(f, i), out var a) ? a : null : _ => null;
                    if ((PlariumPopup.ChooseByPlace(names, want, profiles, ledTo) ?? PlariumPopup.Choose(names, want, profiles, attempt)) is not { } index)
                        return $"None of Plarium Play's other accounts plays {want}: the account was not switched.";
                    if (PlariumPopup.Press(others[index].Button))
                    {
                        picked = names[index];
                        place = index;
                        output.WriteLine($"Plarium Play: '{picked ?? "(name not read)"}' picked (its avatar button).");
                        Thread.Sleep(5000);
                        return null;
                    }
                }
                // The pixel search below presses the first avatar: only when it is the only other account (with more, it may be the wrong one).
                if (others.Count > 1) return "Plarium Play: the chosen avatar could not be pressed: the account was not switched.";
                // The menu can take a few seconds to draw (2026-11-06: a "Security checkup" box slid in above it and the first look found no avatar).
                var avatarAt = FindAvatar(r.L, r.B, scale);
                for (var again = 0; again < 5 && avatarAt is null; again++)
                {
                    Thread.Sleep(1000);
                    avatarAt = FindAvatar(r.L, r.B, scale);
                }
                if (avatarAt is { } avatar)
                {
                    Click(avatar.X, avatar.Y);
                    output.WriteLine($"Plarium Play: the other account picked (its avatar at {avatar.X},{avatar.Y}).");
                    Thread.Sleep(5000);
                    return null;
                }
                if (!PlainBox(otherX, otherY, (int)(14 * scale)))
                    return "Plarium Play's menu is open, but no other account's avatar was found in it: the account was not switched.";
                output.WriteLine("Plarium Play still counts the game as running; trying again in 10 s...");
                keybd_event(0x1B, 0, 0, IntPtr.Zero); // Esc closes the menu
                keybd_event(0x1B, 0, 2, IntPtr.Zero);
                Thread.Sleep(10_000);
                SetForegroundWindow(h);
                Thread.Sleep(800);
            }
            return "Plarium Play kept saying a game is running (two minutes): the account was not switched.";
        }
        finally { SetThreadDpiAwarenessContext(old); }
    }

    /// <summary>
    /// The first "Other Accounts" avatar in Plarium Play's open menu: in the menu's left column near the window's bottom, the lowest band of rows that
    /// are mostly coloured (an avatar is a filled circle; the rows of text around it are thin). Null when there is none.
    /// </summary>
    private static (int X, int Y)? FindAvatar(int left, int bottom, double scale)
    {
        var dc = GetDC(IntPtr.Zero);
        try
        {
            int x0 = left + (int)(26 * scale), x1 = left + (int)(96 * scale), step = Math.Max(2, (int)(2 * scale));
            bool Coloured(uint c)
            {
                int red = (int)(c & 0xFF), green = (int)((c >> 8) & 0xFF), blue = (int)((c >> 16) & 0xFF);
                return red + green + blue < 3 * 170 || Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) > 60;
            }
            var bands = new List<(int Top, int End, List<int> Xs)>();
            (int Top, int End, List<int> Xs)? band = null;
            for (var y = bottom - (int)(230 * scale); y <= bottom - (int)(20 * scale); y += step)
            {
                var xs = new List<int>();
                var cells = 0;
                for (var x = x0; x <= x1; x += step) { cells++; if (Coloured(GetPixel(dc, x, y))) xs.Add(x); }
                if (xs.Count * 100 >= cells * 35)
                {
                    band = band is { } b ? (b.Top, y, [.. b.Xs, .. xs]) : (y, y, xs);
                }
                else if (band is { } done)
                {
                    bands.Add(done);
                    band = null;
                }
            }
            if (band is { } last) bands.Add(last);
            var avatar = bands.LastOrDefault(b => b.End - b.Top >= 16 * scale && b.End - b.Top <= 60 * scale);
            if (avatar.Xs is null) return null;
            return ((int)avatar.Xs.Average(), (avatar.Top + avatar.End) / 2);
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    /// <summary>The pixel is dark: the light sidebar is under a dialog's dimming.</summary>
    /// <summary>
    /// A dialog dims the whole window: the sidebar's column, looked at in six places from a third of the way down to near the bottom, is dark in nearly all of
    /// them (2026-11-05: one point at two thirds was a sidebar icon at a 720-high window, #515153, and every switch from a phone's Remote Desktop failed).
    /// </summary>
    private static bool Dimmed(Rect r, double scale)
    {
        var x = r.L + (int)(30 * scale);
        var dark = 0;
        for (var i = 0; i < 6; i++)
            if (Dimmed(x, r.T + (r.B - r.T) * (33 + 10 * i) / 100)) dark++;
        return dark >= 5;
    }

    private static bool Dimmed(int x, int y)
    {
        var dc = GetDC(IntPtr.Zero);
        try
        {
            var c = GetPixel(dc, x, y);
            return (int)(c & 0xFF) + (int)((c >> 8) & 0xFF) + (int)((c >> 16) & 0xFF) < 3 * 150;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    /// <summary>The square around (x, y) is one flat light grey (the "cannot log out" box), not an avatar.</summary>
    private static bool PlainBox(int x, int y, int half)
    {
        var dc = GetDC(IntPtr.Zero);
        try
        {
            var flat = 0;
            var total = 0;
            for (var dy = -half; dy <= half; dy += 4)
                for (var dx = -half; dx <= half; dx += 4)
                {
                    var c = GetPixel(dc, x + dx, y + dy);
                    int red = (int)(c & 0xFF), green = (int)((c >> 8) & 0xFF), blue = (int)((c >> 16) & 0xFF);
                    total++;
                    if (red > 220 && green > 220 && blue > 220 && Math.Abs(red - green) < 8 && Math.Abs(green - blue) < 8) flat++;
                }
            return flat * 10 >= total * 9;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    private static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(200);
        mouse_event(2, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(80);
        mouse_event(4, 0, 0, 0, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr h, int x, int y, int w, int height, bool repaint);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] private static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
}
