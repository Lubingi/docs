using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveSubtitles.App.Audio;
using LiveSubtitles.App.ViewModels;
using LiveSubtitles.App.Views;
using LiveSubtitles.Core.Audio;
using LiveSubtitles.Core.Diagnostics;
using LiveSubtitles.Core.Transcript;

namespace LiveSubtitles.App.Services;

/// <summary>
/// `LiveSubtitles.exe --smoke-test result.txt`: used by CI on a real Windows machine. Opens every window and tab,
/// pushes sample subtitle lines through the overlay and history, fails on any XAML or data-binding error, and
/// checks the audio APIs (file decoding, process-loopback activation) as far as the machine allows.
/// No API key or network access is needed and nothing is sent anywhere.
/// </summary>
internal static class SmokeTest
{
    public static async Task<int> RunAsync(MainViewModel vm, MainWindow window, string resultPath, ILog log)
    {
        var report = new StringBuilder();
        var bindingErrors = new BindingErrorListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        bool ok = true;

        void Check(string name, Action action, bool required = true)
        {
            try { action(); report.AppendLine($"PASS {name}"); }
            catch (Exception ex)
            {
                report.AppendLine($"{(required ? "FAIL" : "WARN")} {name}: {ex.GetType().Name}: {ex.Message}");
                if (required) ok = false;
            }
        }

        async Task Pump(int ms = 150)
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(ms);
        }

        try
        {
            window.Show();
            await Pump(500);
            var tabs = (TabControl)window.FindName("Tabs");
            foreach (TabItem tab in tabs.Items)
            {
                Check($"tab {tab.Header}", () => { tabs.SelectedItem = tab; window.UpdateLayout(); });
                await Pump();
            }

            Check("source options", () =>
            {
                foreach (var o in vm.SourceOptions) vm.SelectedSource = o;
                vm.SelectedSource = vm.SourceOptions[0];
            });

            Check("transcript → overlay/history", () =>
            {
                var now = DateTimeOffset.UtcNow;
                vm.Transcript.Upsert(new TranscriptLine { SegmentId = 900001, StartedAt = now, SpeakerLabel = "Speaker 1", SpeakerId = 1, SpeakerColor = "#4FC3F7", Translation = "Can you hear me?", Original = "Beni duyabiliyor musun?" });
                vm.Transcript.Upsert(new TranscriptLine { SegmentId = 900002, StartedAt = now, SpeakerLabel = "?", SpeakerUncertain = true, SpeakerColor = "#BDBDBD", Translation = "Yes, loud and clear.", Original = "Ja, høyt og tydelig.", IsFinal = true });
                vm.Transcript.Upsert(new TranscriptLine { SegmentId = 900003, StartedAt = now, SpeakerLabel = "Anna", SpeakerColor = "#81C784", Original = "Sounds good.", SameLanguage = true, IsFinal = true });
            });
            await Pump(400);
            Check("overlay shows lines", () => { if (vm.Overlay.Lines.Count != 3) throw new Exception($"{vm.Overlay.Lines.Count} overlay lines"); });
            Check("history shows lines", () => { if (vm.History.Rows.Count != 3) throw new Exception($"{vm.History.Rows.Count} history rows"); });
            Check("overlay toggles", () => { vm.ToggleClickThrough(); vm.ToggleClickThrough(); vm.ToggleOverlay(); vm.ToggleOverlay(); });
            Check("settings apply", vm.ApplySettings);
            Check("dialogs load", () =>
            {
                foreach (var engine in Enum.GetValues<LiveSubtitles.Core.Translation.TranslationEngine>())
                {
                    var w = new ApiKeyWindow(engine, "model");
                    w.Show();
                    w.Close();
                }
                PromptWindow.CreateForSmokeTest().Close();
            });
            await Pump(300);
            Check("export text", () => { if (TranscriptExporter.ToSrt(vm.Transcript.Snapshot()).Length == 0) throw new Exception("empty srt"); });
            Check("clear history", () => vm.ClearHistoryCommand.Execute(null));

            // ---- audio stack (soft: CI machines may have no audio service/devices)
            Check("list output devices", () => report.AppendLine($"     {DeviceLoopbackSource.ListDevices().Count} output devices"), required: false);
            Check("list apps", () => report.AppendLine($"     {ProcessTree.ListCandidates().Count} candidate apps"), required: false);
            await CheckAsync("audio file decoding (Media Foundation)", async () =>
            {
                var wav = Path.Combine(Path.GetTempPath(), "livesubs-smoke.wav");
                var tone = Enumerable.Range(0, 48000).Select(i => (float)(0.2 * Math.Sin(i * 0.05))).ToArray();
                WavFile.WriteMono16(wav, tone, 48000);
                using var src = new AudioFileSource(wav, playToSpeakers: false, effect: new CallQualitySimulator(), log);
                int samples = 0;
                src.SamplesAvailable += (_, e) => Interlocked.Add(ref samples, e.Samples.Length);
                await src.StartAsync(CancellationToken.None);
                await Task.Delay(1500);
                src.Stop();
                if (samples < 20000) throw new Exception($"only {samples} samples decoded");
                report.AppendLine($"     decoded {samples} samples in real time");
            }, required: true);
            await CheckAsync("process loopback activation", async () =>
            {
                using var src = new ProcessLoopbackSource(Environment.ProcessId, "self", log);
                await src.StartAsync(CancellationToken.None);
                await Task.Delay(300);
            }, required: false);

            report.AppendLine(bindingErrors.Errors.Count == 0 ? "PASS no data-binding errors" : $"FAIL {bindingErrors.Errors.Count} data-binding errors:");
            foreach (var e in bindingErrors.Errors.Distinct().Take(30)) report.AppendLine("     " + e);
            if (bindingErrors.Errors.Count > 0) ok = false;
        }
        catch (Exception ex)
        {
            ok = false;
            report.AppendLine("FAIL unexpected: " + ex);
        }

        report.AppendLine(ok ? "SMOKE TEST PASSED" : "SMOKE TEST FAILED");
        File.WriteAllText(resultPath, report.ToString());
        return ok ? 0 : 1;

        async Task CheckAsync(string name, Func<Task> action, bool required)
        {
            try { await action(); report.AppendLine($"PASS {name}"); }
            catch (Exception ex)
            {
                report.AppendLine($"{(required ? "FAIL" : "WARN")} {name}: {ex.GetType().Name}: {ex.Message}");
                if (required) ok = false;
            }
        }
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message != null) lock (Errors) Errors.Add(message);
        }
    }
}
