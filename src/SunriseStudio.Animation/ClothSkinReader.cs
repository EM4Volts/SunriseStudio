using System.Numerics;

namespace SunriseStudio.Animation;

/** Cloth weights need a validated Havok palette before they can select skeleton bones. */
public sealed partial class ModelSkinReader
{
    /** This decodes the skeletal input to cloth; Havok simulation remains separate. */
    public IReadOnlyList<ModelSkinBinding> ReadClothInput(ClothModelReference source, SkeletonRig rig)
    {
        if (!rig.ClothModels.Contains(source))
            throw new InvalidDataException("Cloth model is not linked to the skeleton's entity.");
        return ReadCloth(source, rig);
    }

    /**
     * Skeletal input of cloth from another entity, such as a guardian's class item on the body rig.
     * The Havok palette must have the rig's bone count and each inverse bind must match its rig bone.
     */
    public IReadOnlyList<ModelSkinBinding> ReadExternalClothInput(ClothModelReference source, SkeletonRig rig) =>
        ReadCloth(source, rig);

    /** Reads declaration-18 cloth weights; every weighted bone must match a Havok inverse bind. */
    private IReadOnlyList<ModelSkinBinding> ReadCloth(ClothModelReference source, SkeletonRig rig)
    {
        if (!catalog.TryGetEntry(source.ModelTag, out var entry) || entry.Reference != 0x808073A5)
            throw new InvalidDataException("Cloth model has an unexpected class.");
        const uint declarationTag = 0x80B35E23;
        VerifyClothDeclaration(declarationTag, 0x80B35E21);
        var havok = new HavokSkinBinding(catalog, source.HavokAssetTag, rig);
        byte[] model = catalog.Read(source.ModelTag);
        var result = new List<ModelSkinBinding>();
        int meshIndex = 0;
        foreach (int mesh in Rows(model, 0x10, 0x80807378, 0x88))
        {
            uint positionTag = U32(model, mesh), weightTag = U32(model, mesh + 8);
            var (positions, _, positionStride) = ClothBuffer(positionTag);
            var (weights, weightPayload, weightStride) = ClothBuffer(weightTag);
            if (positionStride != 48 || weightStride != 8 || positions.Length / 48 != weights.Length / 8)
                throw new NotSupportedException("Cloth input buffers do not match declaration 18.");
            bool active = false;
            for (int stage = 0; stage < 23; stage++)
            {
                if (U16(model, mesh + 0x28 + stage * 2) == U16(model, mesh + 0x2A + stage * 2)) continue;
                active = true;
                if (I16(model, mesh + 0x58 + stage * 2) != 18)
                    throw new NotSupportedException("Cloth stage uses another vertex declaration.");
            }
            if (!active) throw new NotSupportedException("Cloth mesh has no active declared stage.");
            var influences = new VertexSkinWeights[weights.Length / 8];
            for (int i = 0; i < influences.Length; i++)
            {
                int p = i * 8;
                var value = new VertexSkinWeights(weights[p + 4], weights[p + 5], weights[p + 6], weights[p + 7],
                    new Vector4(weights[p], weights[p + 1], weights[p + 2], weights[p + 3]) / 255f);
                Validate(value, rig.Bones.Count);
                for (int lane = 0; lane < 4; lane++)
                    if (weights[p + lane] != 0 && !havok.VerifiedBones.Contains(weights[p + 4 + lane]))
                        throw new NotSupportedException("Cloth weight index has no matching authored inverse bind.");
                influences[i] = value;
            }
            result.Add(new(source.ModelTag, meshIndex++, positionTag, positionStride,
                Array.AsReadOnly(Rows(model, mesh + 0x18, 0x8080737E, 0x20)), Array.AsReadOnly(influences))
            {
                PaletteKind = SkinPaletteKind.ClothInput,
                WeightHeaderTag = weightTag,
                WeightPayloadTag = weightPayload,
                Cloth = new(source.ConfigTag, source.HavokAssetTag, havok.RawTag, declarationTag,
                    havok.PaletteSize, havok.MaximumError)
            });
        }
        return result.AsReadOnly();
    }

    /** Both buffer kinds use bounded type-32 headers and type-40 payloads. */
    private (byte[] Data, uint Payload, int Stride) ClothBuffer(uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var headerEntry) || headerEntry.Type != 32 || headerEntry.SubType != 4
            || !catalog.TryGetEntry(headerEntry.Reference, out var payload) || payload.Type != 40 || payload.SubType != 4)
            throw new InvalidDataException("Cloth stream has an unexpected package type.");
        byte[] header = catalog.Read(tag), bytes = catalog.Read(payload.TagHash);
        int stride = U16(header, 4);
        if (stride == 0 || U32(header, 0) != bytes.Length || bytes.Length % stride != 0
            || U32(header, 8) != 0xDEADBEEF)
            throw new InvalidDataException("Cloth stream size or marker is invalid.");
        return (bytes, payload.TagHash, stride);
    }

    /** Declaration 18 puts four UNORM weights and four UINT indices in slot 2. */
    private void VerifyClothDeclaration(uint declarationTag, uint poolTag)
    {
        if (!catalog.TryGetEntry(declarationTag, out var declaration) || declaration.Reference != 0x808072A9
            || !catalog.TryGetEntry(poolTag, out var poolEntry) || poolEntry.Reference != 0x808072AD)
            throw new InvalidDataException("Cloth declaration tables have unexpected classes.");
        var table = new AnimationControlReader.Data(catalog.Read(declarationTag));
        var pool = new AnimationControlReader.Data(catalog.Read(poolTag));
        int[] rows = table.Rows(8, 0x808072AC, 28).Where(row => table.U16(row) == 18).ToArray();
        if (rows.Length != 1) throw new NotSupportedException("Cloth declaration 18 is absent or duplicated.");
        int row = rows[0];
        var groups = pool.Rows(8, 0x808072AF, 16);
        byte[][] expected = [[0, 0, 4, 3, 0, 4, 6, 0, 4], [5, 0, 10], [1, 0, 5, 2, 0, 6]];
        for (int slot = 0; slot < 4; slot++)
        {
            int group = unchecked((int)table.U32(row + 8 + slot * 4));
            if (table.U8(row + 2 + slot) != 0 || (slot == 3 && group != -1))
                throw new NotSupportedException("Cloth declaration has unsupported instance data.");
            if (slot == 3) continue;
            if ((uint)group >= groups.Length) throw new InvalidDataException("Cloth declaration stream group is absent.");
            var elements = pool.Rows(groups[group], 0x808072B2, 3)
                .SelectMany(offset => new[] { pool.U8(offset), pool.U8(offset + 1), pool.U8(offset + 2) }).ToArray();
            if (!elements.SequenceEqual(expected[slot]))
                throw new NotSupportedException("Cloth declaration format differs from the verified input layout.");
        }
    }
}
