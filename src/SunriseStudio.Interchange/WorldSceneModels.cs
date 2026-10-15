using SunriseStudio.Projects;

namespace SunriseStudio.Interchange;

public sealed record WorldSceneExportOptions(StudioProject? Project = null, bool IncludeMaterials = true)
{
    /** Draw-time shader inputs by part, such as gear dyes and plates; a part with none uses its plain material. */
    public IReadOnlyDictionary<SunriseStudio.Scene.MeshInstance, BundleShaderInputs>? ShaderInputs { get; init; }
    /** Draw LOD by part where the scene holds none, such as SpeedTree parts; 0 is the most detailed. */
    public IReadOnlyDictionary<SunriseStudio.Scene.MeshInstance, int>? PartLods { get; init; }
    /** Per-record decorator inputs by part, in record order; a ground cell holds one per plant. */
    public IReadOnlyDictionary<SunriseStudio.Scene.MeshInstance, IReadOnlyList<WorldDecoratorInputs>>? DecoratorInputs { get; init; }
}
/**
 * What a decorator vertex shader reads per record besides its transform.
 * @param Record TEXCOORD7, record bytes 12 to 15, unpacked by quantization rows 4 and 5.
 * @param Occlusion BLENDINDICES0, the record's map AO bytes as unorm.
 */
public readonly record struct WorldDecoratorInputs(System.Numerics.Vector4 Record, System.Numerics.Vector4 Occlusion);
public sealed record WorldBuffer(string Path, long Length, string Sha256);
public sealed record WorldBufferView(int Buffer, long Offset, int Count, int Components, string ComponentType);
public sealed record WorldGeometry(int Id, uint ModelTag, int PartIndex, int LogicalPartIndex,
    WorldBufferView Positions, WorldBufferView Normals, WorldBufferView TexCoords, WorldBufferView Indices,
    WorldBufferView? Tangents, string Basis, WorldMotion? Motion)
{
    /** COLOR0 as UInt8 lanes; null when the part has none. */
    public WorldBufferView? Colors { get; init; }
    /** Lanes z and w of the UV stream, untransformed; null when it stores two lanes. */
    public WorldBufferView? TexCoordsZw { get; init; }
}
public sealed record WorldMotion(string Profile, WorldBufferView Colors, float[][] Constants, float[][] Rates);
public sealed record WorldMaterial(int Id, uint TagHash, int RenderStage, string Name,
    string? Path, string? Sha256, bool NeedsNativeBasis, string BlendMode)
{
    public IReadOnlyDictionary<uint, uint>? TextureOverrides { get; init; }
    /** The native draw culls clockwise triangles, the faces Blender sees as back faces. */
    public bool BackfaceCulling { get; init; }
}
public sealed record WorldMaterialDocument(SunriseStudio.Formats.MaterialDocument Native, BundleMaterialPreview Preview);
public sealed record WorldTexture(uint TagHash, string Path, string Sha256, int Width, int Height, int Format);
public sealed record WorldPart(int Geometry, int Material, uint ModelTag, int PartIndex, int LogicalPartIndex,
    uint ConfigTag, uint ResourceClass, string Name, float[] Transform, int Lod, int RenderStage,
    bool? InitiallyVisible, string? VisibilityReason, string Layer);
public sealed record WorldPrototype(int Id, IReadOnlyList<WorldPart> Parts, bool CollectionInstanceSafe);
public sealed record WorldInstanceTable(WorldBufferView Rows, int Stride)
{
    /** One row per placement: WorldDecoratorInputs Record then Occlusion; null when no placement has any. */
    public WorldBufferView? Inputs { get; init; }
}
public sealed record WorldMarker(string Name, uint TagHash, uint SourceTag, int SourceOffset, ulong PlacementIdentity,
    string Layer, uint BubbleHash, uint ActivityTag, int StateOrdinal, string StateName,
    float[] Position, float[] Rotation, float[] Extents);
public sealed record WorldSceneStatistics(int Instances, int Parts, int Geometries, int Prototypes,
    int Materials, int Textures, long Vertices, long Indices, long BufferBytes,
    IReadOnlyDictionary<string, double> Seconds);
public sealed record WorldSceneManifest(string Format, int Version, string Name, uint SourceTag,
    string Coordinates, string MatrixConvention, IReadOnlyList<WorldBuffer> Buffers, IReadOnlyList<WorldGeometry> Geometry,
    IReadOnlyList<WorldMaterial> Materials, IReadOnlyList<WorldTexture> Textures,
    IReadOnlyList<WorldPrototype> Prototypes, IReadOnlyList<string> Names, WorldInstanceTable Instances,
    IReadOnlyList<WorldMarker> Markers,
    IReadOnlyList<BundleNotice> Notices, WorldSceneStatistics Statistics);
public sealed record WorldSceneExportResult(string ManifestPath, WorldSceneManifest Manifest);
/**
 * One placed light fitted to a point, spot or line light. Light values are the game's lighting units before exposure.
 * @param Direction Spot axis, or the line's axis; a point light keeps its authored -Z.
 * @param Intensity Radiant intensity along Direction, summed over Copies. The irradiance at d metres is
 *   Intensity / (d^2 + Radius^2) * (1 - (d / Range)^4)^2 times Color. A line gives intensity per metre of its length.
 * @param Range Blender's custom distance: the light ends there.
 * @param Radius Blender's soft falloff radius.
 * @param SpotAngle Full cone angle in degrees; zero for a point light, or a line whose points shine all around.
 * @param SpotBlend Soft fraction of the cone, as Blender's spot blend.
 * @param Length Line length centred on Position; zero for a point or spot light.
 * @param Copies Identical rows at this place; the game draws each one.
 */
public sealed record WorldLight(int Id, string Kind, uint TableTag, int Row, uint MaterialTag, uint PixelShaderTag,
    uint BubbleHash, string Layer, float[] Position, float[] Direction, float[] Color, float Intensity, float Range,
    float Radius, float SpotAngle, float SpotBlend, float Length, int Copies, bool Shadows, bool Enabled, bool Animated,
    string Fit)
{
    /** Intensity in every direction that a spot adds to its cone; Blender draws it as a point light. */
    public float Omni { get; init; }
    /** Evenly spaced Blender points a line becomes; zero for a point or spot light. */
    public int Points { get; init; }
    /** Axis a line's points shine along as spots; null when they shine all around. */
    public float[]? Aim { get; init; }
    /** Intensity a negative spot removes straight behind a spot, along -Direction; at most Omni. */
    public float Back { get; init; }
    /** Full cone angle of the negative back spot in degrees. */
    public float BackAngle { get; init; }
    /** Soft fraction of the back spot's cone. */
    public float BackBlend { get; init; }
}
/**
 * The time-of-day sun as the atmosphere preview draws it.
 * @param Direction Unit vector toward the sun that technique 37 shades with, the time-of-day sky direction.
 * @param Irradiance Irradiance on a surface facing the sun, times Color.
 */
public sealed record WorldSun(float[] Direction, float[] Color, float Irradiance, float TimeOfDay, uint CurveSetTag, uint BubbleHash)
{
    /** Unit vector toward the light the game's sun shadows are cast from; null in older files. */
    public float[]? ShadowDirection { get; init; }
}
/**
 * The sky as two equirectangular Radiance HDR images beside the lights file, in game units before exposure.
 * Column u looks along azimuth atan2(y, x) = (0.5 - u) 2 pi; row v, from the top, looks at height sin((0.5 - v) pi).
 * @param Path The sky the camera sees.
 * @param LightPath The light surfaces receive: its cosine-weighted irradiance over pi is the game's diffuse sky light.
 * @param Keyframe Lookup slice position the time of day picks.
 */
public sealed record WorldSky(string Path, string LightPath, float TimeOfDay, float Keyframe, uint PlacementListTag,
    uint LookupTag, uint BubbleHash);
/**
 * A box where the game replaces the sky light with a local cubemap: the space it lights as indoors or shaded.
 * @param Transform Box to world, row-major with row vectors; the box spans -Extents to Extents.
 * @param Extents Half sizes along the box axes.
 */
public sealed record WorldLightVolume(float[] Transform, float[] Extents, uint BubbleHash, uint PlacementSourceTag);
/** The lights file beside the world manifest. */
public sealed record WorldLightSet(string Format, int Version, string Name, string Units, IReadOnlyList<WorldLight> Lights,
    WorldSun? Sun, IReadOnlyList<BundleNotice> Notices)
{
    /** The map's sky light; null when the map places no usable atmosphere or the file predates it. */
    public WorldSky? Sky { get; init; }
    /** The map's local cubemap boxes; empty in files that predate them. */
    public IReadOnlyList<WorldLightVolume> Volumes { get; init; } = [];
}
