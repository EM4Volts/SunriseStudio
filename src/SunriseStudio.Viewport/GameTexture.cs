using SunriseStudio.Packages;
using Vortice.DXGI;

namespace SunriseStudio.Viewport;

public sealed record GameTexture(TextureData Asset)
{
    public uint Tag => Asset.TagHash;
    public int Width => Asset.Width;
    public int Height => Asset.Height;
    public int Depth => Asset.Depth;
    public int ArraySize => Asset.ArraySize;
    public Format Format => (Format)Asset.Format;
    public bool IsSingleImage => !Asset.IsVolume && Asset.ArraySize == 1;

    public static GameTexture Read(PackageCatalog catalog, PackageEntry entry) =>
        new(new TextureDataReader(catalog).Read(entry.TagHash));

    public static (int RowPitch, int SliceBytes) GetPitch(Format format, int width, int height) =>
        TextureDataReader.GetPitch((int)format, width, height);
}
