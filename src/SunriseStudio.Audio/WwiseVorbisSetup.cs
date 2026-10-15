namespace SunriseStudio.Audio;

/** Rebuilds a standard Vorbis setup header from a Wwise setup. */
internal static class WwiseVorbisSetup
{
    /**
     * Rebuilds the six setup parts from the trimmed Wwise layout with external codebooks.
     * Ported from vgmstream's ww2ogg_generate_vorbis_setup (ISC), itself from ww2ogg (BSD).
     * @param setup Setup packet body, without its 2-byte size header.
     * @param channels Channel count from fmt.
     * @return The setup packet; truncation or an unknown codebook throws InvalidDataException.
     *         VorbisDecoder checks every other field.
     */
    public static byte[] Rebuild(byte[] setup, int channels)
    {
        var input = new VorbisBitReader(setup, 0, setup.Length);
        var output = new VorbisBitWriter(setup.Length * 4 + 4096);
        output.Write(5, 8);
        foreach (byte c in "vorbis"u8) output.Write(c, 8);

        uint bookCount = Copy(input, output, 8) + 1;
        for (uint i = 0; i < bookCount; i++) WwiseVorbisCodebooks.Rebuild((int)input.Read(10), output);

        output.Write(0, 6);                       // one time-domain transform
        output.Write(0, 16);                      // its unused value

        uint floorCount = Copy(input, output, 6) + 1;
        for (uint i = 0; i < floorCount; i++)
        {
            output.Write(1, 16);                  // floor type 1, the only one Wwise writes
            uint partitions = Copy(input, output, 5);
            var classes = new uint[partitions];
            uint maxClass = 0;
            for (int j = 0; j < partitions; j++) maxClass = Math.Max(maxClass, classes[j] = Copy(input, output, 4));
            var dimensions = new uint[maxClass + 1];
            for (uint j = 0; j <= maxClass; j++)
            {
                dimensions[j] = Copy(input, output, 3) + 1;
                uint subclasses = Copy(input, output, 2);
                if (subclasses != 0) Copy(input, output, 8);
                for (int k = 0; k < 1 << (int)subclasses; k++) Copy(input, output, 8);
            }
            Copy(input, output, 2);
            int rangeBits = (int)Copy(input, output, 4);
            foreach (uint c in classes)
                for (int k = 0; k < dimensions[c]; k++) Copy(input, output, rangeBits);
        }

        uint residueCount = Copy(input, output, 6) + 1;
        for (uint i = 0; i < residueCount; i++)
        {
            output.Write(input.Read(2), 16);
            Copy(input, output, 24);
            Copy(input, output, 24);
            Copy(input, output, 24);
            uint classifications = Copy(input, output, 6) + 1;
            Copy(input, output, 8);
            var cascade = new uint[classifications];
            for (int j = 0; j < classifications; j++)
            {
                uint low = Copy(input, output, 3);
                uint high = Copy(input, output, 1) != 0 ? Copy(input, output, 5) : 0;
                cascade[j] = high * 8 + low;
            }
            foreach (uint bits in cascade)
                for (int k = 0; k < 8; k++)
                    if ((bits & 1u << k) != 0) Copy(input, output, 8);
        }

        uint mappingCount = Copy(input, output, 6) + 1;
        for (uint i = 0; i < mappingCount; i++)
        {
            output.Write(0, 16);                  // mapping type 0, the only one
            uint submaps = Copy(input, output, 1) != 0 ? Copy(input, output, 4) + 1 : 1;
            if (Copy(input, output, 1) != 0)
            {
                uint steps = Copy(input, output, 8) + 1;
                int bits = WwiseVorbisCodebooks.ILog((uint)channels - 1);
                for (uint j = 0; j < steps * 2; j++) Copy(input, output, bits);
            }
            Copy(input, output, 2);
            if (submaps > 1)
                for (int j = 0; j < channels; j++) Copy(input, output, 4);
            for (uint j = 0; j < submaps * 3; j++) Copy(input, output, 8);
        }

        uint modeCount = Copy(input, output, 6) + 1;
        for (uint i = 0; i < modeCount; i++)
        {
            Copy(input, output, 1);
            output.Write(0, 16);                  // window type
            output.Write(0, 16);                  // transform type
            Copy(input, output, 8);
        }
        output.Write(1, 1);                       // framing bit
        output.PadToByte();
        return output.ToArray();
    }

    private static uint Copy(VorbisBitReader input, VorbisBitWriter output, int bits)
    {
        uint value = input.Read(bits);
        output.Write(value, bits);
        return value;
    }
}
