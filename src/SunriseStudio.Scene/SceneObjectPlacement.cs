using System.Numerics;

namespace SunriseStudio.Scene;

/** One identity is shared by a placed object's parts, even when their local transforms differ. */
public sealed class SceneObjectPlacement(uint sourceTag, int sourceIndex, Matrix4x4 worldTransform)
{
    public uint SourceTag { get; } = sourceTag;
    public int SourceIndex { get; } = sourceIndex;
    public int EntryIndex { get; init; } = -1;
    /** A placement update moves the identity in place, so selection keeps it. */
    public Matrix4x4 WorldTransform { get; internal set; } = worldTransform;
}
