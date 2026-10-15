using System.Numerics;
using static SunriseStudio.Animation.AnimationIkSolver;

namespace SunriseStudio.Animation;

/**
 * Apply the native authored reference-frame program with neutral aim inputs.
 * Gameplay IK, correction overrides and graph inputs are not evaluated.
 */
public sealed class AnimationRigBinding
{
    private readonly SkeletonRig skeleton;
    private readonly HashSet<uint> clips;
    public uint ResourceTag { get; }
    public IReadOnlyList<AnimationRigControl> Controls { get; }
    public IReadOnlyList<AnimationControlGroup> ControlGroups { get; }
    public IReadOnlyList<int> IkControlIndices { get; }
    private readonly bool usePoleAxes;
    private readonly AnimationBoneCorrection[] corrections;
    public int BoneCorrectionCount => corrections.Length;

    /** Object-space aim direction for aim frames; +X is the rest pose. */
    public Vector3 AimDirection { get; set; } = Vector3.UnitX;

    /** Object-space look direction for aim frames; +X is the rest pose. */
    public Vector3 LookDirection { get; set; } = Vector3.UnitX;

    /** A binding retains the entity's exact skeleton, control program and linked clip set. */
    internal AnimationRigBinding(SkeletonRig skeleton, uint resourceTag, AnimationRigControl[] controls,
        HashSet<uint> clips, AnimationControlGroup[] groups, int[] ikControls, bool usePoleAxes,
        AnimationBoneCorrection[] corrections)
    {
        this.skeleton = skeleton;
        ResourceTag = resourceTag;
        Controls = Array.AsReadOnly(controls);
        this.clips = clips;
        ControlGroups = Array.AsReadOnly(groups);
        IkControlIndices = Array.AsReadOnly(ikControls);
        this.usePoleAxes = usePoleAxes;
        this.corrections = corrections;
    }

    /** Rows of the rig's retarget table, in authored order. */
    public IReadOnlyList<AnimationRetarget> Retargets { get; init; } = [];

    // Frame 0 of the rest layer; it goes over every sampled pose at full weight.
    private AnimationControlSample[] rest = [];

    /** Bones the rest layer writes, in index order; empty without a rest layer. */
    public IReadOnlyList<int> RestBones { get; private set; } = [];

    /**
     * A copy whose sampled poses end with frame 0 of an override clip at full weight.
     * The clip need not be linked to the entity; channels ChannelMap drops are dropped.
     */
    public AnimationRigBinding WithRestLayer(AnimationControlClip clip)
    {
        if (clip.PoseMode != 2) throw new NotSupportedException("A rest layer must be an override clip.");
        if (CompatibleControlCount(clip) == 0)
            throw new NotSupportedException("Rest layer and rig have no matching leading control group.");
        var samples = SampleClip(clip, 0);
        return new AnimationRigBinding(skeleton, ResourceTag, [.. Controls], clips, [.. ControlGroups],
            [.. IkControlIndices], usePoleAxes, corrections)
        {
            Retargets = Retargets,
            rest = samples,
            RestBones = samples.Select(sample => (int)Controls[sample.ControlIndex].BoneIndex).Distinct().Order().ToArray()
        };
    }

    /** Lays the rest layer over sampled values; explicit values from ApplyValues skip it. */
    private IReadOnlyList<BoneTransform> WithRest(IReadOnlyList<BoneTransform> values)
    {
        if (rest.Length == 0) return values;
        var result = values.ToArray();
        foreach (var sample in rest) result[sample.ControlIndex] = Blend(result[sample.ControlIndex], sample, 2, 1);
        return result;
    }

    /** Bind pose, with the rest layer's bones set as it places them over the control defaults. */
    public void ApplyBindPose(SkeletonPose pose)
    {
        if (!ReferenceEquals(pose.Rig, skeleton))
            throw new InvalidDataException("Animation pose belongs to a different skeleton.");
        pose.ResetToBindPose();
        if (rest.Length == 0) return;
        var output = Evaluate(WithRest(Controls.Select(control => control.DefaultValue).ToArray()));
        foreach (int bone in RestBones)
        {
            int parent = skeleton.Bones[bone].ParentIndex;
            pose.SetLocalTransform(bone, parent < 0 ? output[bone]
                : BoneTransform.FromMatrix(output[bone].Matrix * Inverse(output[parent]).Matrix));
        }
        pose.Evaluate();
    }

    /** The scale of the first retarget row that shares a key with the clip; one when none does. */
    public float TranslationScale(AnimationControlClip clip)
    {
        foreach (var row in Retargets)
            if (row.Keys.Any(clip.RetargetKeys.Contains)) return row.TranslationScale;
        return 1;
    }

    /**
     * Samples on rig controls: each channel moves to the control ChannelMap gives, and dropped channels are left out.
     * A clip authored on another body plays its translations at this rig's size.
     */
    private AnimationControlSample[] SampleClip(AnimationControlClip clip, float seconds, bool loop = false)
    {
        int[] map = ChannelMap(clip);
        float scale = TranslationScale(clip);
        var samples = new List<AnimationControlSample>();
        foreach (var sample in clip.Sample(seconds, loop))
        {
            int control = (uint)sample.ControlIndex < (uint)map.Length ? map[sample.ControlIndex] : -1;
            if (control < 0) continue;
            samples.Add(sample with { ControlIndex = control,
                Translation = sample.HasTranslation && scale != 1 ? sample.Translation * scale : sample.Translation });
        }
        return [.. samples];
    }

    /**
     * The rig control each clip channel writes; -1 drops the channel. Channels of the leading groups whose names
     * equal the rig's write the same index. When none of the remaining clip groups is a rig group and their widths
     * fill the remaining controls, those channels write the remaining controls in bone order.
     */
    public int[] ChannelMap(AnimationControlClip clip)
    {
        int lead = CompatibleControlCount(clip);
        var map = Enumerable.Range(0, clip.ControlCount).Select(channel => channel < lead ? channel : -1).ToArray();
        int groups = 0;
        while (groups < Math.Min(clip.ControlGroups.Count, ControlGroups.Count)
            && clip.ControlGroups[groups].NameHash == ControlGroups[groups].NameHash) groups++;
        var trailing = clip.ControlGroups.Skip(groups).Where(group => group.ControlCount != 0).ToArray();
        var names = ControlGroups.Select(group => group.NameHash).ToHashSet();
        if (lead == 0 || trailing.Length == 0 || trailing.Any(group => names.Contains(group.NameHash))
            || trailing.Sum(group => group.ControlCount) != Controls.Count - lead)
            return map;
        var remaining = Controls.Where(control => control.Index >= lead).OrderBy(control => control.BoneIndex)
            .ThenBy(control => control.Index).Select(control => control.Index).ToArray();
        for (int k = 0; k < remaining.Length && lead + k < map.Length; k++) map[lead + k] = remaining[k];
        return map;
    }

    /** Clip modes that need another animation layer are rejected rather than treated as absolute poses. */
    public void Apply(AnimationControlClip clip, float seconds, SkeletonPose pose, bool loop = false)
    {
        if (!ReferenceEquals(pose.Rig, skeleton))
            throw new InvalidDataException("Clip, animation rig, and render skeleton are not linked.");
        ApplyControls(WithRest(SampleBaseControls(clip, seconds, loop)), pose);
    }

    /** A relative or override layer needs an explicit linked base clip before limb solving. */
    public void ApplyLayer(AnimationControlClip baseClip, float baseSeconds, AnimationControlClip layerClip,
        float layerSeconds, float weight, SkeletonPose pose, bool loop = false)
    {
        if (!ReferenceEquals(pose.Rig, skeleton))
            throw new InvalidDataException("Animation pose belongs to a different skeleton.");
        ApplyControls(WithRest(SampleLayerControls(baseClip, baseSeconds, layerClip, layerSeconds, weight, loop)), pose);
    }

    /** Relative previews use a chosen base clip or the rig's authored control defaults. */
    public void ApplyPreview(AnimationControlClip clip, float seconds, SkeletonPose pose,
        AnimationControlClip? baseClip = null, bool loop = false)
    {
        if (clip.PoseMode == 0) { Apply(clip, seconds, pose, loop); return; }
        if (!ReferenceEquals(pose.Rig, skeleton))
            throw new InvalidDataException("Animation pose belongs to a different skeleton.");
        var values = baseClip is null ? Controls.Select(control => control.DefaultValue).ToArray()
            : SampleBaseControls(baseClip, seconds, loop);
        ApplyControls(WithRest(BlendLayer(values, clip, seconds, 1, loop)), pose);
    }

    /** Missing layer channels and incompatible trailing groups retain their base control values. */
    public IReadOnlyList<BoneTransform> SampleLayerControls(AnimationControlClip baseClip, float baseSeconds,
        AnimationControlClip layerClip, float layerSeconds, float weight, bool loop = false)
        => BlendLayer(SampleBaseControls(baseClip, baseSeconds, loop), layerClip, layerSeconds, weight, loop);

    /** Evaluates a blend stack over the rig's authored control defaults, then solves the pose. */
    public void ApplyStack(IEnumerable<AnimationLayerEntry> entries, SkeletonPose pose)
    {
        if (!ReferenceEquals(pose.Rig, skeleton))
            throw new InvalidDataException("Animation pose belongs to a different skeleton.");
        ApplyControls(WithRest(SampleStack(entries)), pose);
    }

    /**
     * Runs prepared entries in order. The first sample's pose mode picks the blend; samples with
     * another mode are skipped. Samples blend into each other first, then into the result by weight.
     */
    public IReadOnlyList<BoneTransform> SampleStack(IEnumerable<AnimationLayerEntry> entries)
    {
        var values = Controls.Select(control => control.DefaultValue).ToArray();
        foreach (var entry in AnimationLayerStack.Prepare(entries))
        {
            if (entry.Weight <= AnimationLayerStack.Skip) continue;
            int mode = entry.Samples[0].Clip.PoseMode;
            var samples = entry.Samples.Where(sample => sample.Clip.PoseMode == mode).ToArray();
            var factors = AnimationLayerStack.SequentialFactors(samples.Select(sample => sample.Weight).ToArray());
            var blended = new AnimationControlSample?[Controls.Count];
            for (int i = 0; i < samples.Length; i++)
            {
                var clip = samples[i].Clip;
                ValidateClip(clip);
                foreach (var sample in SampleClip(clip, samples[i].Frame / 30))
                    blended[sample.ControlIndex] = Mix(blended[sample.ControlIndex], sample, factors[i]);
            }
            float weight = entry.Weight > AnimationLayerStack.Full ? 1 : entry.Weight;
            foreach (var sample in blended)
                if (sample is { } value) values[value.ControlIndex] = Blend(values[value.ControlIndex], value, mode, weight);
        }
        return Array.AsReadOnly(values);
    }

    /** A channel first authored by a later sample starts from that sample. */
    private static AnimationControlSample Mix(AnimationControlSample? previous, AnimationControlSample next, float factor)
    {
        if (previous is not { } prior) return next;
        return new(next.ControlIndex,
            !prior.HasScale ? next.Scale : !next.HasScale ? prior.Scale : prior.Scale + (next.Scale - prior.Scale) * factor,
            !prior.HasRotation ? next.Rotation : !next.HasRotation ? prior.Rotation
                : InterpolateRotation(prior.Rotation, next.Rotation, factor),
            !prior.HasTranslation ? next.Translation : !next.HasTranslation ? prior.Translation
                : prior.Translation + (next.Translation - prior.Translation) * factor,
            prior.HasScale || next.HasScale, prior.HasRotation || next.HasRotation,
            prior.HasTranslation || next.HasTranslation);
    }

    /** Mode 1 offsets the value; modes 0 and 2 move it toward the sample. Unauthored parts stay. */
    private static BoneTransform Blend(BoneTransform original, AnimationControlSample sample, int mode, float weight)
    {
        Quaternion rotation = original.Rotation;
        Vector3 translation = original.Translation;
        float scale = original.Scale;
        if (mode == 1)
        {
            if (sample.HasScale) scale *= 1 + weight * (sample.Scale - 1);
            if (sample.HasRotation) rotation = Quaternion.Normalize(original.Rotation
                * InterpolateRotation(Quaternion.Identity, sample.Rotation, weight));
            if (sample.HasTranslation) translation += sample.Translation * weight;
        }
        else
        {
            if (sample.HasScale) scale += weight * (sample.Scale - scale);
            if (sample.HasRotation) rotation = InterpolateRotation(rotation, sample.Rotation, weight);
            if (sample.HasTranslation) translation += (sample.Translation - translation) * weight;
        }
        return new(rotation, translation, scale);
    }

    /** Unauthored channels retain their base values before IK runs. */
    private IReadOnlyList<BoneTransform> BlendLayer(BoneTransform[] values, AnimationControlClip layerClip,
        float layerSeconds, float weight, bool loop)
    {
        if (!float.IsFinite(weight) || weight < 0 || weight > 1)
            throw new ArgumentOutOfRangeException(nameof(weight), "Layer weight must be between zero and one.");
        ValidateClip(layerClip);
        if (layerClip.PoseMode is not (1 or 2))
            throw new NotSupportedException("Only relative and override clips can be used as a layer.");
        if (CompatibleControlCount(layerClip) == 0)
            throw new NotSupportedException("Layer and rig have no matching leading control group.");
        foreach (var sample in SampleClip(layerClip, layerSeconds, loop))
            values[sample.ControlIndex] = Blend(values[sample.ControlIndex], sample, layerClip.PoseMode, weight);
        return Array.AsReadOnly(values);
    }

    /** A base clip starts from the rig's authored defaults and must carry an absolute pose. */
    private BoneTransform[] SampleBaseControls(AnimationControlClip clip, float seconds, bool loop)
    {
        ValidateClip(clip);
        if (clip.PoseMode != 0)
            throw new NotSupportedException($"Clip pose mode {clip.PoseMode} requires animation-layer evaluation.");
        if (CompatibleControlCount(clip) == 0)
            throw new NotSupportedException("Clip and rig have no matching leading control group.");
        var values = Controls.Select(x => x.DefaultValue).ToArray();
        foreach (var sample in SampleClip(clip, seconds, loop))
        {
            var original = values[sample.ControlIndex];
            values[sample.ControlIndex] = new BoneTransform(
                sample.HasRotation ? sample.Rotation : original.Rotation,
                sample.HasTranslation ? sample.Translation : original.Translation,
                sample.HasScale ? sample.Scale : original.Scale);
        }
        return values;
    }

    /** Solves explicit control values, as a clip sample would supply them. */
    public void ApplyValues(IReadOnlyList<BoneTransform> values, SkeletonPose pose)
    {
        if (!ReferenceEquals(pose.Rig, skeleton))
            throw new InvalidDataException("Animation pose belongs to a different skeleton.");
        ApplyControls(values, pose);
    }

    /**
     * Finds the control values that place each bone at its object-space target before limb solving.
     * A chain bone is assumed to reach its target after the solve; corrected bones are overwritten.
     * @param targets Object transforms for every skeleton bone.
     * @return One value per control, in control order.
     */
    public BoneTransform[] Solve(IReadOnlyList<BoneTransform> targets)
    {
        if (targets.Count != skeleton.Bones.Count) throw new ArgumentException("Target count differs from the skeleton.");
        var output = skeleton.Bones.Select(x => x.BindObject).ToArray();
        var values = new BoneTransform[Controls.Count];
        foreach (var control in Controls)
        {
            int bone = control.BoneIndex;
            var target = targets[bone];
            SkeletonReader.CheckTransform(target);
            if (control.CompositionMode == 0)
                values[control.Index] = Compose(Inverse(Frame(control.RotationFrame, bone, output)), target);
            else
                values[control.Index] = new BoneTransform(Quaternion.Normalize(Quaternion.Multiply(
                    Quaternion.Conjugate(Rotation(control.RotationFrame, bone, output)), target.Rotation)),
                    target.Translation - Translation(control.TranslationFrame, bone, output), 1);
            output[bone] = target;
            // The limb solve moves the chain's middle bone back onto its target.
            if (control.ChainMode == 0) output[skeleton.Bones[bone].ParentIndex] = targets[skeleton.Bones[bone].ParentIndex];
            else if (control.ChainMode == 1) DistributeChain(control, output);
        }
        return values;
    }

    /** Control blending finishes before any pose or palette is changed. */
    private void ApplyControls(IReadOnlyList<BoneTransform> values, SkeletonPose pose)
    {
        var output = Evaluate(values).ToArray();
        AnimationIkSolver.Apply(skeleton, Controls, IkControlIndices, usePoleAxes, output);
        var clothInput = output.ToArray();
        foreach (var correction in corrections) correction.Apply(output);
        pose.SetObjectTransforms(output, clothInput);
    }

    /** A linked clip may come from a larger rig; its leading control groups decide what it writes. */
    private void ValidateClip(AnimationControlClip clip)
    {
        if (!clips.Contains(clip.TagHash))
            throw new InvalidDataException("Clip, animation rig, and render skeleton are not linked.");
    }

    /**
     * Counts the leading clip channels that write the same rig index: the clip's group widths over the leading
     * equal names. ChannelMap may place later channels too; the rig's own widths are not read.
     */
    public int CompatibleControlCount(AnimationControlClip clip)
    {
        int count = 0;
        for (int i = 0; i < Math.Min(clip.ControlGroups.Count, ControlGroups.Count); i++)
        {
            if (clip.ControlGroups[i].NameHash != ControlGroups[i].NameHash) break;
            count = checked(count + clip.ControlGroups[i].ControlCount);
        }
        return Math.Min(count, Controls.Count);
    }

    /** Controls run in authored order because their reference frames may read earlier outputs. */
    public IReadOnlyList<BoneTransform> Evaluate(IReadOnlyList<BoneTransform> values)
    {
        if (values.Count != Controls.Count) throw new ArgumentException("Animation control count differs from its rig.");
        var output = skeleton.Bones.Select(x => x.BindObject).ToArray();
        foreach (var control in Controls)
        {
            var value = values[control.Index];
            SkeletonReader.CheckTransform(value);
            int bone = control.BoneIndex;
            if (control.CompositionMode == 0)
                output[bone] = Compose(Frame(control.RotationFrame, bone, output), value);
            else
                output[bone] = new BoneTransform(
                    Quaternion.Normalize(Quaternion.Multiply(Rotation(control.RotationFrame, bone, output), value.Rotation)),
                    Translation(control.TranslationFrame, bone, output) + value.Translation, 1);
            if (control.ChainMode == 0)
            {
                int parent = skeleton.Bones[bone].ParentIndex;
                if (parent < 0) throw new InvalidDataException("Animation chain has no parent bone.");
                int grandparent = skeleton.Bones[parent].ParentIndex;
                if (grandparent >= 0)
                    output[parent] = Compose(output[grandparent],
                        Compose(Inverse(skeleton.Bones[grandparent].BindObject), skeleton.Bones[parent].BindObject));
            }
            else if (control.ChainMode == 1) DistributeChain(control, output);
        }
        return Array.AsReadOnly(output);
    }

    /** Distributed chains blend intermediate bones between bind-relative and target-relative poses. */
    private void DistributeChain(AnimationRigControl control, BoneTransform[] output)
    {
        int ancestor = control.ChainBoneIndex;
        if (ancestor < 0) throw new InvalidDataException("Animation distributed chain has no ancestor.");
        var chain = new List<int>();
        int bone = skeleton.Bones[control.BoneIndex].ParentIndex;
        while (bone != ancestor && bone >= 0)
        {
            if (chain.Count == 16) throw new NotSupportedException("Animation chain exceeds the native bounded depth.");
            chain.Add(bone);
            bone = skeleton.Bones[bone].ParentIndex;
        }
        if (bone != ancestor) throw new InvalidDataException("Animation chain ancestor is not in the skeleton hierarchy.");
        var relative = Compose(Inverse(output[ancestor]), output[control.BoneIndex]);
        var inverseBind = Inverse(skeleton.Bones[ancestor].BindObject);
        for (int i = chain.Count - 1, step = 1; i >= 0; i--, step++)
        {
            float amount = (float)step / (chain.Count + 1);
            var relativeBind = Compose(inverseBind, skeleton.Bones[chain[i]].BindObject);
            var distributed = Lerp(BoneTransform.Identity, relative, amount);
            output[chain[i]] = Compose(output[ancestor], Lerp(relativeBind, distributed, amount));
        }
    }

    private BoneTransform Current(int index, BoneTransform[] output) => index < 0 ? BoneTransform.Identity : output[index];

    /** Full-frame references preserve scale; separate translation references follow their own native rules. */
    private BoneTransform Frame(AnimationFrameReference reference, int target, BoneTransform[] output)
    {
        return reference.Kind switch
        {
            0 or 3 => Current(reference.BoneIndex, output),
            1 => reference.BoneIndex < 0 ? skeleton.Bones[target].BindObject
                : Compose(output[reference.BoneIndex], Compose(Inverse(skeleton.Bones[reference.BoneIndex].BindObject),
                    skeleton.Bones[target].BindObject)),
            2 => output[0],
            4 => Compose(output[0], skeleton.Bones[target].BindObject),
            5 => Lerp(Current(reference.BoneIndex, output), Current(reference.SecondBoneIndex, output), 0.5f),
            6 => new BoneTransform(AimRotation(reference, target, output),
                Translation(reference.TranslationFrame!, target, output), 1),
            _ => throw new NotSupportedException("Animation reference frame kind is unsupported.")
        };
    }

    /** Turns +X part way toward the source direction by the frame weight, keeping +Z as the pole. */
    private Quaternion AimRotation(AnimationFrameReference reference, int target, BoneTransform[] output)
    {
        float weight = Math.Clamp(reference.AimWeight, 0, 1);
        // Native normalizes only the target-driven direction before the blend.
        Vector3 source = reference.AimSource switch
        {
            0 => AimDirection,
            1 => LookDirection,
            _ => NormalizeOrZero((reference.AimTarget < 0 ? Vector3.Zero : output[reference.AimTarget].Translation)
                - output[target].Translation)
        };
        var direction = NormalizeOrZero(Vector3.UnitX + (source - Vector3.UnitX) * weight);
        return AnimationIkSolver.FromTo(Vector3.UnitX, direction, Vector3.UnitZ);
    }

    private Quaternion Rotation(AnimationFrameReference reference, int target, BoneTransform[] output) =>
        Frame(reference, target, output).Rotation;

    /** Native separate translation frames omit the referenced bone's scale in kind 1. */
    private Vector3 Translation(AnimationFrameReference reference, int target, BoneTransform[] output)
    {
        if (reference.Kind == 6) return Translation(reference.TranslationFrame!, target, output);
        if (reference.Kind != 1) return Frame(reference, target, output).Translation;
        if (reference.BoneIndex < 0) return skeleton.Bones[target].BindObject.Translation;
        var relative = Compose(Inverse(skeleton.Bones[reference.BoneIndex].BindObject), skeleton.Bones[target].BindObject);
        var current = output[reference.BoneIndex];
        return Vector3.Transform(relative.Translation, current.Rotation) + current.Translation;
    }

    private static BoneTransform Lerp(BoneTransform first, BoneTransform second, float amount)
    {
        return new BoneTransform(InterpolateRotation(first.Rotation, second.Rotation, amount),
            first.Translation + (second.Translation - first.Translation) * amount,
            first.Scale + (second.Scale - first.Scale) * amount);
    }

    private static Quaternion InterpolateRotation(Quaternion first, Quaternion second, float amount)
    {
        if (Quaternion.Dot(first, second) < 0)
            second = new Quaternion(-second.X, -second.Y, -second.Z, -second.W);
        return Quaternion.Normalize(new Quaternion(first.X + (second.X - first.X) * amount,
            first.Y + (second.Y - first.Y) * amount, first.Z + (second.Z - first.Z) * amount,
            first.W + (second.W - first.W) * amount));
    }
}
