using System.Diagnostics;
using LiveSubtitles.App.Interop;
using NAudio.CoreAudioApi;

namespace LiveSubtitles.App.Audio;

public sealed record ProcessChoice(string Name, int RootPid, string Title, bool PlayingAudio)
{
    public override string ToString() => $"{(PlayingAudio ? "♪ " : "")}{Name}{(string.IsNullOrEmpty(Title) ? "" : $" — {Title}")}";
}

/// <summary>Process discovery helpers: finds the root process of an app (e.g. the main Discord.exe) so the
/// whole process tree, including the child process that actually renders audio, can be captured.</summary>
public static class ProcessTree
{
    public static readonly string[] DiscordNames = { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" };

    private readonly record struct Entry(int Pid, int ParentPid, string Name);

    private static List<Entry> Snapshot()
    {
        var list = new List<Entry>();
        var snap = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>() };
            if (!NativeMethods.Process32FirstW(snap, ref e)) return list;
            do
            {
                list.Add(new Entry((int)e.th32ProcessID, (int)e.th32ParentProcessID, Path.GetFileNameWithoutExtension(e.szExeFile)));
            } while (NativeMethods.Process32NextW(snap, ref e));
        }
        finally
        {
            NativeMethods.CloseHandle(snap);
        }
        return list;
    }

    /// <summary>Returns the root PID of the process tree for any of the given executable names, or null if not running.
    /// If several trees exist, the one with the most processes wins.</summary>
    public static (int Pid, string Name)? FindRoot(IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var all = Snapshot();
        var byPid = all.ToDictionary(e => e.Pid);
        var matches = all.Where(e => wanted.Contains(e.Name)).ToList();
        if (matches.Count == 0) return null;
        var roots = matches.Where(e => !(byPid.TryGetValue(e.ParentPid, out var parent) && parent.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        if (roots.Count == 0) roots = matches;
        var best = roots.OrderByDescending(r => CountDescendants(all, r.Pid)).First();
        return (best.Pid, best.Name);
    }

    private static int CountDescendants(List<Entry> all, int pid)
    {
        int count = 0;
        var stack = new Stack<int>();
        stack.Push(pid);
        while (stack.Count > 0 && count < 1000)
        {
            int p = stack.Pop();
            foreach (var c in all.Where(e => e.ParentPid == p && e.Pid != p)) { count++; stack.Push(c.Pid); }
        }
        return count;
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    /// <summary>Apps that can be captured in "Any app" mode, those currently playing audio first.</summary>
    public static List<ProcessChoice> ListCandidates()
    {
        var playing = new HashSet<int>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (dev)
                {
                    var sessions = dev.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var s = sessions[i];
                        if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive && !s.IsSystemSoundsSession)
                            playing.Add((int)s.GetProcessID);
                    }
                }
            }
        }
        catch { /* audio session enumeration is best-effort */ }

        var all = Snapshot();
        var byPid = all.ToDictionary(e => e.Pid);
        int self = Environment.ProcessId;
        var result = new Dictionary<string, ProcessChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                if (proc.Id == self || proc.SessionId == 0) continue;
                string title = "";
                try { title = proc.MainWindowTitle; } catch { }
                bool audio = playing.Contains(proc.Id);
                if (!audio && string.IsNullOrEmpty(title)) continue;
                string name = proc.ProcessName;
                var root = FindRootOf(byPid, proc.Id, name);
                if (result.TryGetValue(name, out var existing))
                {
                    result[name] = existing with { PlayingAudio = existing.PlayingAudio || audio, Title = existing.Title.Length > 0 ? existing.Title : title };
                }
                else result[name] = new ProcessChoice(name, root, title, audio);
            }
        }
        foreach (var pid in playing)
        {
            if (!byPid.TryGetValue(pid, out var e) || pid == self) continue;
            if (!result.ContainsKey(e.Name)) result[e.Name] = new ProcessChoice(e.Name, FindRootOf(byPid, pid, e.Name), "", true);
            else result[e.Name] = result[e.Name] with { PlayingAudio = true };
        }
        return result.Values.OrderByDescending(c => c.PlayingAudio).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int FindRootOf(Dictionary<int, Entry> byPid, int pid, string name)
    {
        int current = pid;
        for (int guard = 0; guard < 64; guard++)
        {
            if (!byPid.TryGetValue(current, out var e) || !byPid.TryGetValue(e.ParentPid, out var parent) || e.ParentPid == current) break;
            if (!parent.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) break;
            current = parent.Pid;
        }
        return current;
    }
}
