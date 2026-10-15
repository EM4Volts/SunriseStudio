using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** Local lights: the deferred_lights stage after the global lighting pass. */
public sealed partial class NativeSceneRenderer
{
    private NativeLocalLights? localLights;
    private readonly List<NativeLocalLight> lightList = [];
    // The light made from each Plan.LocalLights entry, with the row it was made from.
    private NativeLocalLight?[] lightRecords = [];
    private AuthoredLightRow?[] lightRecordRows = [];
    private readonly Dictionary<uint, NativeLightProgram> lightPrograms = [];

    /** Row +0x8C, the light's enable program; one copy per tag. */
    private NativeLightProgram? Program(AuthoredLightParameters? source)
    {
        if (source is null) return null;
        if (!lightPrograms.TryGetValue(source.TagHash, out var program))
            lightPrograms.Add(source.TagHash, program = new(source.TagHash, source.Bytecode.ToArray(), source.Constants.ToArray(),
                source.Defaults.ToArray()));
        return program;
    }

    /** Off leaves the lighting targets as the global lighting pass wrote them. */
    public bool LocalLightsEnabled { get; set; } = true;
    public bool LocalLightsActive { get; private set; }
    public string? LocalLightError { get; private set; }
    /** Lights drawn in the last frame. */
    public int LocalLightDrawCount => LocalLightsActive ? localLights?.LastDrawCount ?? 0 : 0;
    /** Last frame: lights outside the view, faded out, and off by their enable program. */
    public (int Culled, int Faded, int Disabled) LocalLightSkips => LocalLightsActive && localLights is { } lights
        ? (lights.LastCulledCount, lights.LastFadedCount, lights.LastDisabledCount) : default;
    /** Enable programs that read an input the viewer lacks; their lights draw. */
    public int UnevaluatedLightPrograms => localLights?.UnevaluatedPrograms.Count ?? 0;
    /** Stage-4 light rows the plan holds. */
    public int LocalLightCount => Plan.LocalLights.Count;
    /** Light materials the pass refused, with the reason. */
    public IReadOnlyDictionary<uint, string> RefusedLightMaterials =>
        localLights?.RefusedMaterials ?? new Dictionary<uint, string>();
    /** The viewer's bubble filter for lights, by the light table's bubble hash. */
    public Func<uint, bool>? LightFilter { get; set; }

    /** Adds the visible lights into the lighting targets; the global pass keeps a copy for its validation. */
    private void RenderLocalLights(NativeAmbientLightingInputs inputs, NativeAmbientLighting lighting, ViewerCamera camera)
    {
        LocalLightsActive = false;
        if (!LocalLightsEnabled || Plan.LocalLights.Count == 0 || lighting.Targets is not { } targets) return;
        try
        {
            localLights ??= new NativeLocalLights(renderer, Plan.Reader, states, Plan.Parameters,
                tag => textures.TryGetValue(tag, out var texture) ? texture.View : null);
            lightList.Clear();
            var filter = LightFilter;
            var rows = Plan.LocalLights;
            if (lightRecords.Length != rows.Count) { lightRecords = new NativeLocalLight?[rows.Count]; lightRecordRows = new AuthoredLightRow?[rows.Count]; }
            for (int i = 0; i < rows.Count; i++)
            {
                var (row, bubble) = rows[i];
                if (filter is not null && !filter(bubble)) continue;
                if (lightRecords[i] is not { } light || !ReferenceEquals(lightRecordRows[i], row) || light.BubbleHash != bubble)
                {
                    lightRecords[i] = light = new(row.MaterialTag, row.Transform, row.VolumeTransform, row.SourceVector60,
                        row.SourceVector70, row.Scale, bubble, Program(row.SkipParameters));
                    lightRecordRows[i] = row;
                }
                lightList.Add(light);
            }
            lighting.KeepValidationCopy();
            localLights.Render(lightList, inputs, targets, camera.Position, camera.NearPlane);
            LocalLightsActive = true;
            LocalLightError = null;
        }
        catch (Exception error) when (Recoverable(error) || error is InvalidOperationException or ArgumentException)
        {
            LocalLightError = error.Message;
            AddDiagnostic($"Local lights: {error.Message}");
        }
    }

    private void DisposeLocalLights()
    {
        localLights?.Dispose(); localLights = null;
    }
}
