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
        @"^(close|close (dialog|popup|modal|window)|[x×✕✖╳]|later|maybe later|remind me later|not now|skip|no|no,? thanks|dismiss|cancel|got it|ok|okay)$",
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
                seen.Add($"'{name}'{(id.Length > 0 ? $" ({id})" : "")}");
                if (!IsDismiss(name, id) || !b.Current.IsEnabled) continue;
                if (b.TryGetCurrentPattern(InvokePattern.Pattern, out var p))
                {
                    ((InvokePattern)p).Invoke();
                    return $"'{name}'{(id.Length > 0 ? $" ({id})" : "")}";
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

    private static AutomationElement? Page(AutomationElement root) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
}
