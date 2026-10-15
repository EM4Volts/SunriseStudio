using ImGuiNET;
using SunriseStudio.Audio;

namespace SunriseStudio.Editor;

public sealed partial class AudioPage
{
    private readonly Dictionary<(uint Id, bool Switch), uint> gameSyncSelections = [];
    private readonly Dictionary<uint, float> parameterValues = [];
    private int previewLoops = 2, timelineVariation;
    private uint selectedPlaylist;
    private uint playlistStart;
    private uint playlistEntriesId;
    private AudioAnalysis? playlistEntriesAnalysis;
    private IReadOnlyList<AudioPlaylistEntry> playlistEntries = [];
    private bool initialDelay = true, effectVolumes = true, randomChoices;

    /** Authored choices regenerate a distinct composition instead of changing the loaded bank. */
    private void DrawCompositionOptions()
    {
        if (session?.Document.Bank is null || !ImGui.CollapsingHeader("Composition controls")) return;
        var active = session;
        if (active.Analysis is not { } analysis)
        {
            ImGui.BeginDisabled(IsBusy);
            if (ImGui.Button("Read music routing"))
                Begin("Reading music routing", async token => { await active.Analyze(token); return new(); });
            ImGui.EndDisabled(); return;
        }
        ImGui.BeginDisabled(IsBusy);
        ImGui.SetNextItemWidth(110); ImGui.SliderInt("Preview loops", ref previewLoops, 1, 16);
        ImGui.Checkbox("Initial delay", ref initialDelay); ImGui.SameLine();
        ImGui.Checkbox("Effect gain", ref effectVolumes);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Volume only; no filters or reverb.");
        ImGui.SameLine(); ImGui.Checkbox("Random variants", ref randomChoices);
        foreach (var group in analysis.GameSyncs)
        {
            var key = (group.Id, group.Switch);
            string current = gameSyncSelections.TryGetValue(key, out uint value)
                ? group.Choices.FirstOrDefault(choice => choice.Value == value)?.Name ?? value.ToString() : "Automatic";
            ImGui.PushID($"sync:{group.Id}:{group.Switch}"); ImGui.SetNextItemWidth(250);
            if (ImGui.BeginCombo(group.Name, current))
            {
                if (ImGui.Selectable("Automatic", !gameSyncSelections.ContainsKey(key))) gameSyncSelections.Remove(key);
                foreach (var choice in group.Choices)
                    if (ImGui.Selectable(choice.Name + "##" + choice.Value, gameSyncSelections.GetValueOrDefault(key) == choice.Value))
                        gameSyncSelections[key] = choice.Value;
                ImGui.EndCombo();
            }
            ImGui.PopID();
        }
        if (analysis.Parameters.Count > 0 && ImGui.TreeNode($"Game parameters ({analysis.Parameters.Count})###game-parameters"))
        {
            ImGui.BeginChild("audio-parameters", new(0, 140), ImGuiChildFlags.Borders);
            foreach (var parameter in analysis.Parameters)
            {
                ImGui.PushID(unchecked((int)parameter.Id));
                bool enabled = parameterValues.ContainsKey(parameter.Id);
                if (ImGui.Checkbox(parameter.Name, ref enabled))
                { if (enabled) parameterValues[parameter.Id] = parameter.Minimum; else parameterValues.Remove(parameter.Id); }
                if (enabled)
                {
                    float value = parameterValues[parameter.Id]; ImGui.SameLine(); ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("##value", ref value, parameter.Minimum, parameter.Maximum, "%.3f")) parameterValues[parameter.Id] = value;
                }
                ImGui.PopID();
            }
            ImGui.EndChild(); ImGui.TreePop();
        }
        if (ImGui.Button("Render choices"))
        {
            player.Stop(); loadedWave = ""; info = null; selectedComposition = -1;
            var choices = new AudioCompositionOptions { LoopCount = previewLoops, IncludeInitialDelay = initialDelay,
                IncludeEffectVolumes = effectVolumes, AllRandomChoices = randomChoices,
                GameSyncs = gameSyncSelections.Select(pair => new AudioGameSync(pair.Key.Switch, pair.Key.Id, pair.Value)).ToArray(),
                Variables = parameterValues.Select(pair => new AudioGameVariable(pair.Key, pair.Value)).ToArray() };
            Begin("Resolving audio choices", async token => { await active.GenerateCompositions(choices, token); return new(RefreshCompositions: true); });
        }
        DrawTransitions(analysis);
        if (analysis.Playlists.Count > 0)
        {
            ImGui.SeparatorText("MUSIC TIMELINE");
            if (!analysis.Playlists.ContainsKey(selectedPlaylist)) selectedPlaylist = DefaultPlaylist(analysis);
            ImGui.SetNextItemWidth(320);
            if (ImGui.BeginCombo("Playlist", analysis.Playlists[selectedPlaylist].Name))
            {
                foreach (var playlist in analysis.Playlists.Values)
                    if (ImGui.Selectable(playlist.Name + "##" + playlist.Id, selectedPlaylist == playlist.Id)) { selectedPlaylist = playlist.Id; playlistStart = 0; }
                ImGui.EndCombo();
            }
            if (playlistEntriesId != selectedPlaylist || !ReferenceEquals(playlistEntriesAnalysis, analysis))
            {
                playlistEntries = AudioPlaylistRouting.Entries(analysis, selectedPlaylist);
                playlistEntriesId = selectedPlaylist; playlistEntriesAnalysis = analysis; playlistStart = 0;
            }
            ImGui.SetNextItemWidth(320);
            if (ImGui.BeginCombo("Start at", playlistStart == 0 ? "Beginning" : playlistEntries.First(value => value.Id == playlistStart).Name))
            {
                if (ImGui.Selectable("Beginning", playlistStart == 0)) playlistStart = 0;
                foreach (var entry in playlistEntries)
                    if (ImGui.Selectable(entry.Name + "##" + entry.Id, entry.Id == playlistStart)) playlistStart = entry.Id;
                ImGui.EndCombo();
            }
            ImGui.SetNextItemWidth(110); ImGui.InputInt("Variation", ref timelineVariation);
            timelineVariation = Math.Clamp(timelineVariation, 0, 10000);
            if (ImGui.Button("Prepare next play")) PrepareTimeline(false);
            ImGui.SameLine(); if (ImGui.Button("Play next")) PrepareTimeline(true);
            ImGui.SameLine(); if (ImGui.Button("Reset history")) active.ResetPlaylistHistory();
            ImGui.TextDisabled($"Next play {active.PlaylistPlayCount(selectedPlaylist) + 1}");
            ImGui.SameLine(); ImGui.TextDisabled("Dry mix");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Dry mix: no effects.");
            if (active.Timeline is { } plan && ImGui.TreeNode($"Timeline ({plan.Segments.Count} segments)###timeline"))
            {
                ImGui.BeginChild("audio-timeline", new(0, 140), ImGuiChildFlags.Borders);
                foreach (var segment in plan.Segments)
                    ImGui.TextUnformatted($"{Time(segment.StartSeconds)}  {segment.Name}  {Time(segment.EndSeconds - segment.StartSeconds)}");
                if (active.TimelineRender is { } render)
                    ImGui.TextDisabled($"Overlap {render.OverlapFrames / 48000.0:F2} s / Clipped {render.ClippedSamples:N0} samples");
                ImGui.EndChild();
                if (plan.Notices.Count > 0 && ImGui.TreeNode($"Notes ({plan.Notices.Count})###timeline-notes"))
                {
                    StudioUi.Facts("timeline-notes", plan.Notices);
                    ImGui.TreePop();
                }
                DrawAutomation(plan); ImGui.TreePop();
            }
        }
        ImGui.EndDisabled();
    }

    private void PrepareTimeline(bool play)
    {
        var active = session!; uint playlist = selectedPlaylist, start = playlistStart; int loops = previewLoops, variation = timelineVariation;
        player.Stop(); loadedWave = ""; info = null;
        selectedMedia = selectedComposition = -1; unsupported = "";
        Begin("Rendering music timeline", async token =>
        {
            var rendered = await active.RenderTimeline(playlist, loops, variation, token, start);
            return new(Wave: rendered.Path, Info: PcmWave.ReadInfo(rendered.Path) with { Encoding = "Music timeline (dry)" }, Play: play,
                Pcm: PcmWave.Read(rendered.Path));
        });
    }

    /** Start on a renderable path while retaining unsupported playlists in the picker. */
    private uint DefaultPlaylist(AudioAnalysis analysis)
    {
        foreach (uint id in analysis.Playlists.Keys)
            try { _ = AudioTimeline.Build(analysis, id, previewLoops, timelineVariation); return id; }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException) { }
        return analysis.Playlists.Keys.First();
    }
}
