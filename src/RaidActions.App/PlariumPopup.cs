using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace RaidActions.App;

/// <summary>
/// Plarium Play's own popups (2026-11-05: a marketing box about its games, the window dimmed under it, Esc did not close it, and every account switch
/// failed; Ehsan: "there's a modal on top of the Plarium app for marketing that needs to be closed, it might happen in future"). Its window is a web page
/// that UI Automation reads by name, so the popup's close button is found by its name and pressed through UI Automation (no pixel guessed, and it works
/// with the popup's top off the screen). Only a button that closes or puts off is pressed: never one that buys, plays, installs or opens anything.
/// </summary>
public static class PlariumPopup
{
    private static readonly Regex Dismiss = new(
        @"^(close|close (dialog|popup|modal|window)|[x×✕✖╳]|later|maybe later|remind me later|ask me later|not now|skip|no|no,? thanks|dismiss|cancel|got it|ok|okay)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Forbidden = new(
        @"buy|purchase|pay|price|\$|€|£|aud|usd|offer|deal|shop|store|play|install|download|get|claim|redeem|open|learn|more|visit|go to|subscribe|sign|rate|review|link|account|log ?out|exit|quit",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The button's name (or its id) only closes or puts the popup off.</summary>
    public static bool IsDismiss(string? name, string? id = null)
    {
        var n = (name ?? "").Trim();
        if (n.Length > 0) return Dismiss.IsMatch(n) && !Forbidden.IsMatch(n);
        // An icon button without a name: only its id says "close".
        var i = (id ?? "").Trim();
        return Regex.IsMatch(i, @"(^|[-_ ])close($|[-_ ])|^close", RegexOptions.IgnoreCase) && !Forbidden.IsMatch(i);
    }

    /// <summary>
    /// The popup's close button pressed: looked for in the page only (the window's own title bar has a "Close" that would close Plarium Play). Returns what
    /// was pressed, or null with every button the page has written out (to learn the popup's buttons).
    /// </summary>
    public static string? Close(IntPtr window, TextWriter output)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            // Chromium builds its page's tree when it is first asked: asked twice.
            var page = Page(root) ?? Page(root);
            if (page is null) { output.WriteLine("Plarium Play's page could not be read."); return null; }
            var buttons = page.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            var seen = new List<string>();
            foreach (AutomationElement b in buttons)
            {
                var (name, id) = (b.Current.Name, b.Current.AutomationId);
                seen.Add($"'{name}'{(id.Length > 0 ? $" ({id})" : "")}{(name.Trim().Length == 0 ? $" [{b.Current.ClassName}]" : "")}");
                if (!IsDismiss(name, id) || !b.Current.IsEnabled) continue;
                if (b.TryGetCurrentPattern(InvokePattern.Pattern, out var p))
                {
                    ((InvokePattern)p).Invoke();
                    return $"'{name}'{(id.Length > 0 ? $" ({id})" : "")}";
                }
            }
            // An icon × without a name (2026-11-05: the "RAID Digest" news article and the "Enjoying Raid? Rate" box): Plarium Play's close-button
            // component, the same in both (its class sc-1az45dy-0; the page's groups all measure as the whole page, so no box gives a corner).
            foreach (AutomationElement b in buttons)
            {
                if (b.Current.Name.Trim().Length > 0 || !b.Current.IsEnabled || !IsCloseComponent(b.Current.ClassName)) continue;
                if (b.TryGetCurrentPattern(InvokePattern.Pattern, out var p))
                {
                    ((InvokePattern)p).Invoke();
                    return $"× ({b.Current.ClassName})";
                }
            }
            // Else a dialog element's own corner × (the Digest is one: a ReactModal dialog, its × a nameless button at its top right).
            foreach (AutomationElement dialog in page.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)))
            {
                var box = dialog.Current.BoundingRectangle;
                foreach (AutomationElement b in dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                {
                    var r = b.Current.BoundingRectangle;
                    if (b.Current.Name.Trim().Length > 0 || !b.Current.IsEnabled
                        || !CornerClose((box.Left, box.Top, box.Width, box.Height), (r.Left, r.Top, r.Width, r.Height))) continue;
                    if (b.TryGetCurrentPattern(InvokePattern.Pattern, out var p))
                    {
                        ((InvokePattern)p).Invoke();
                        return "× (the nameless button at the top right of its dialog)";
                    }
                }
            }
            output.WriteLine($"Plarium Play's popup: no close button among its buttons: {string.Join(", ", seen)}.");
            return null;
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            output.WriteLine($"Plarium Play's page could not be read ({e.Message}).");
            return null;
        }
    }

    /// <summary>
    /// The first "Other Accounts" avatar in Plarium Play's open profile menu, pressed through UI Automation (2026-11-06: a nameless button of the component
    /// <c>sc-12k8j5c-6</c> under "Other Accounts"; the pixel search missed it while a "Security checkup" box slid in). False when it is not there.
    /// </summary>
    public static bool PressOtherAccount(IntPtr window, TextWriter output)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var page = Page(root) ?? Page(root);
            if (page is null) return false;
            foreach (AutomationElement b in page.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            {
                if (!IsOtherAccount(b.Current.ClassName) || !b.Current.IsEnabled || b.Current.BoundingRectangle.IsEmpty) continue;
                if (!b.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) continue;
                ((InvokePattern)p).Invoke();
                return true;
            }
            return false;
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            output.WriteLine($"Plarium Play's menu could not be read ({e.Message}).");
            return false;
        }
    }

    /// <summary>
    /// The "Other Accounts" avatars of the open profile menu with their profile names (2026-11-07, three accounts: the avatars have no name; hovering one
    /// shows its profile's name in place of the "Other Accounts" label, so each is hovered and the text that appears is read). A name not read: null.
    /// </summary>
    public static List<(AutomationElement Button, string? Name)> OtherAccounts(IntPtr window, Action<int, int> hover, TextWriter output)
    {
        var list = new List<(AutomationElement, string?)>();
        try
        {
            var root = AutomationElement.FromHandle(window);
            var page = Page(root) ?? Page(root);
            if (page is null) return list;
            HashSet<string> Texts() => [.. page.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().Select(t => t.Current.Name).Where(n => !string.IsNullOrWhiteSpace(n))];
            var avatars = page.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
                .Where(b => IsOtherAccount(b.Current.ClassName) && b.Current.IsEnabled && !b.Current.BoundingRectangle.IsEmpty).ToList();
            foreach (var b in avatars)
            {
                var before = Texts();
                var r = b.Current.BoundingRectangle;
                hover((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                string? name = null;
                for (var look = 0; look < 6 && name is null; look++)
                {
                    Thread.Sleep(400);
                    name = Texts().Except(before).FirstOrDefault();
                }
                list.Add((b, name));
            }
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            output.WriteLine($"Plarium Play's menu could not be read ({e.Message}).");
        }
        return list;
    }

    /// <summary>The profile signed in now: the name on Plarium Play's profile button (its component sc-137w4kd-0); null when not read.</summary>
    public static string? CurrentProfile(IntPtr window)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var page = Page(root) ?? Page(root);
            var group = page?.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group)).Cast<AutomationElement>()
                .FirstOrDefault(g => (g.Current.ClassName ?? "").Split(' ').Contains("sc-137w4kd-0"));
            return group?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))?.Current.Name is { Length: > 0 } n ? n : null;
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
    }

    /// <summary>Presses one avatar's button (from <see cref="OtherAccounts"/>).</summary>
    public static bool Press(AutomationElement button)
    {
        try
        {
            if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) return false;
            ((InvokePattern)p).Invoke();
            return true;
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; }
    }

    /// <summary>
    /// Which avatar to press for the account wanted: the one whose profile is known to be it (<paramref name="profiles"/>: profile name to player id, learnt on
    /// switches); else one whose profile is not known yet (it is then learnt); never one known to be another account. Null: none can be it.
    /// </summary>
    public static int? Choose(IReadOnlyList<string?> names, long want, IReadOnlyDictionary<string, long> profiles)
    {
        for (var i = 0; i < names.Count; i++) if (names[i] is { } n && profiles.TryGetValue(n, out var id) && id == want) return i;
        for (var i = 0; i < names.Count; i++) if (names[i] is not { } n || !profiles.ContainsKey(n)) return i;
        return null;
    }

    /// <summary>The "Other Accounts" avatar's component (a styled component's class, seen 2026-11-06).</summary>
    public static bool IsOtherAccount(string? className) =>
        (className ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("sc-12k8j5c-6");

    /// <summary>Plarium Play's close-button component (a styled component's class; seen on the × of every popup so far, 2026-11-05).</summary>
    public static bool IsCloseComponent(string? className) =>
        (className ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("sc-1az45dy-0");

    /// <summary>
    /// The button is a dialog's corner ×: small and square-ish (at most 80 px, under a tenth of the dialog's width), inside the dialog's top-right corner
    /// (its centre in the dialog's right fifth and top 150 px).
    /// </summary>
    public static bool CornerClose((double X, double Y, double W, double H) dialog, (double X, double Y, double W, double H) button)
    {
        if (dialog.W <= 0 || dialog.H <= 0 || button.W <= 0 || button.H <= 0) return false;
        if (button.W > 80 || button.H > 80 || button.W > dialog.W / 10 || button.W > 2 * button.H || button.H > 2 * button.W) return false;
        var (cx, cy) = (button.X + button.W / 2, button.Y + button.H / 2);
        return cx >= dialog.X + dialog.W * 0.8 && cx <= dialog.X + dialog.W && cy >= dialog.Y && cy <= dialog.Y + Math.Min(150, dialog.H / 3);
    }

    private static AutomationElement? Page(AutomationElement root) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
}
