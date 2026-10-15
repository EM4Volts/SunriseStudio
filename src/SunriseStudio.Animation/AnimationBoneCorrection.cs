using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Animation;

/** Type-zero correction rows distribute an authored rotation around a selected axis. */
internal sealed record AnimationBoneCorrection(int Target, int Parent, int Source, int Reference,
    float Weight, BoneTransform Local, Quaternion FirstRotation, Quaternion SecondRotation,
    Quaternion ReferenceRotation, Vector3 Axis)
{
    /** Corrections run after limb solving and before inverse-bind palette construction. */
    internal void Apply(BoneTransform[] pose)
    {
        var relative = Quaternion.Normalize(Quaternion.Conjugate(pose[Reference].Rotation) * pose[Source].Rotation);
        var change = Quaternion.Normalize(ReferenceRotation * relative);
        var axis = Vector3.Normalize(Axis);
        var swing = AnimationIkSolver.FromTo(axis, Vector3.Normalize(Vector3.Transform(axis, change)), Vector3.UnitZ);
        var twist = Quaternion.Normalize(Quaternion.Conjugate(swing) * change);
        float amount = Parent == Reference ? Math.Clamp(1 - Weight, 0, 1) : Weight;
        if (twist.W < 0) twist = new(-twist.X, -twist.Y, -twist.Z, -twist.W);
        var blended = Quaternion.Normalize(new Quaternion(twist.X * amount, twist.Y * amount,
            twist.Z * amount, 1 + (twist.W - 1) * amount));
        var rotation = Quaternion.Normalize(SecondRotation * FirstRotation * blended);
        if (Parent != Reference) rotation = Quaternion.Conjugate(rotation);
        pose[Target] = AnimationIkSolver.Compose(AnimationIkSolver.Compose(pose[Parent], Local),
            new BoneTransform(rotation, Vector3.Zero, 1));
    }

    /** The owning animation component stores fixed 256-byte correction rows. */
    internal static AnimationBoneCorrection[] Read(PackageCatalog catalog, IEnumerable<uint> tags, int boneCount)
    {
        var owners = tags.Distinct().ToArray();
        if (owners.Length > 1)
            throw new NotSupportedException("Multiple animation components need explicit correction-owner selection.");
        var result = new List<AnimationBoneCorrection>();
        foreach (uint tag in owners)
        {
            var data = new AnimationControlReader.Data(catalog.Read(tag));
            int resource = data.Pointer(0x18);
            if (data.U32(resource - 4) != 0x808036CF)
                throw new NotSupportedException("Animation component has an unsupported bone-correction owner.");
            foreach (int row in data.Rows(resource + 0x208, 0x808096FF, 256))
            {
                if (data.U8(row) != 0)
                    throw new NotSupportedException("Animation bone-correction type is not decoded.");
                int target = data.U16(row + 2), parent = data.U16(row + 4);
                int source = data.U16(row + 6), reference = data.U16(row + 8);
                if (new[] { target, parent, source, reference }.Any(index => index >= boneCount))
                    throw new InvalidDataException("Bone correction refers outside its skeleton.");
                var local = new BoneTransform(Q(data, row + 16), V3(data, row + 32), data.F32(row + 44));
                SkeletonReader.CheckTransform(local);
                float weight = data.F32(row + 12);
                Vector3 axis = V3(data, row + 144);
                if (!float.IsFinite(weight) || weight < 0 || weight > 1
                    || !float.IsFinite(axis.LengthSquared()) || axis.LengthSquared() < 1e-8f)
                    throw new InvalidDataException("Bone correction has invalid axis or weight.");
                result.Add(new(target, parent, source, reference, weight, local,
                    Q(data, row + 48), Q(data, row + 80), Q(data, row + 112), axis));
            }
        }
        return result.ToArray();
    }

    private static Quaternion Q(AnimationControlReader.Data data, int offset)
    {
        var value = new Quaternion(data.F32(offset), data.F32(offset + 4),
            data.F32(offset + 8), data.F32(offset + 12));
        SkeletonReader.CheckTransform(new(value, Vector3.Zero, 1));
        return Quaternion.Normalize(value);
    }

    private static Vector3 V3(AnimationControlReader.Data data, int offset) => new(
        data.F32(offset), data.F32(offset + 4), data.F32(offset + 8));
}
