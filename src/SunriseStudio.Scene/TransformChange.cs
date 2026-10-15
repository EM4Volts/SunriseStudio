using System.Numerics;

namespace SunriseStudio.Scene;

/** A world transform about a pivot: turn, then scale on the world axes, then move. */
public readonly record struct TransformChange(Vector3 Move, Quaternion Turn, Vector3 Scale)
{
    public static TransformChange Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    public bool IsIdentity => Move == Vector3.Zero && Turn == Quaternion.Identity && Scale == Vector3.One;

    public bool UniformScale => Scale.X == Scale.Y && Scale.Y == Scale.Z;

    /** The pivot itself maps to exactly pivot + Move. */
    public Vector3 Apply(Vector3 point, Vector3 pivot) => pivot + Move + Scale * Vector3.Transform(point - pivot, Turn);

    /** The same change as a matrix for row vectors, so whole meshes move as Apply moves points. */
    public Matrix4x4 Matrix(Vector3 pivot) => Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateFromQuaternion(Turn)
        * Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateTranslation(pivot + Move);
}
