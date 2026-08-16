using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using CinDa.DaWatcha.Core;

namespace CinDa.DaWatcha.App;

public sealed class WindowCompletionDetector : ICompletionDetector
{
    public Task<bool> IsCompleteAsync(
        WatchItem watch, CancellationToken cancellationToken) =>
        Task.Run(() => Detect(watch, cancellationToken), cancellationToken);

    private static bool Detect(
        WatchItem watch, CancellationToken cancellationToken)
    {
        foreach (var handle in FindWindows(watch.Process.Pid))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var title = GetTitle(handle);
            if (MatchesTitle(title, watch.Completion.WindowTitlePattern))
                return true;
            if (watch.Completion.Method.Equals(
                    "title", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var root = AutomationElement.FromHandle(handle);
                if (ContainsCompletionText(
                        root, watch.Completion.Patterns, cancellationToken))
                    return true;
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
        }

        return false;
    }

    private static bool ContainsCompletionText(
        AutomationElement root,
        IReadOnlyCollection<string> patterns,
        CancellationToken cancellationToken)
    {
        var walker = TreeWalker.ControlViewWalker;
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        var inspected = 0;

        while (stack.Count > 0 && inspected++ < 4000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = stack.Pop();
            try
            {
                if (!element.Current.IsOffscreen &&
                    patterns.Any(pattern =>
                        element.Current.Name.Contains(
                            pattern, StringComparison.OrdinalIgnoreCase)))
                    return true;

                for (var child = walker.GetFirstChild(element);
                     child is not null;
                     child = walker.GetNextSibling(child))
                    stack.Push(child);
            }
            catch (ElementNotAvailableException) { }
        }

        return false;
    }

    private static bool MatchesTitle(string title, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;
        try
        {
            return Regex.IsMatch(title, pattern,
                RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            return title.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyList<nint> FindWindows(int pid)
    {
        var handles = new List<nint>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var ownerPid);
            if (ownerPid == pid && IsWindowVisible(handle))
                handles.Add(handle);
            return true;
        }, nint.Zero);
        return handles;
    }

    private static string GetTitle(nint handle)
    {
        var length = GetWindowTextLength(handle);
        var text = new StringBuilder(length + 1);
        _ = GetWindowText(handle, text, text.Capacity);
        return text.ToString();
    }

    private delegate bool EnumWindowsProc(nint handle, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(
        EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint handle, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint handle);
}
