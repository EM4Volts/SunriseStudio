using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Formats;

/** A weapon type is item data only: an identifier hash, one weapon-type trait and the display type line. */
public static class InvestmentWeaponType
{
    public const uint TraitTableTag = 0x8131932B, TraitCategoryTableTag = 0x8131932A;
    // Definition +164 holds FNV-1 of the type identifier, such as auto_rifle.
    private const int IdentifierField = 164, TraitsField = 224;
    private const uint TraitClass = 0x80802C50, ArrayMarker = 0x80809FBD;
    private const string TraitCategory = "weapon_type";

    /** `Auto Rifle` becomes `auto_rifle`, the form stock identifiers and trait names use. */
    public static string Identifier(string typeName)
    {
        var text = new StringBuilder();
        foreach (char c in typeName.Trim().ToLowerInvariant())
            text.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        string identifier = string.Join('_', text.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
        return identifier.Length != 0 ? identifier : throw new InvalidDataException("A weapon type needs a letter or digit.");
    }

    private static IReadOnlyList<ushort> ReadTraits(ReadOnlyMemory<byte> definition)
    {
        var reader = new TagReader(definition);
        var traits = reader.Array(TraitsField, TraitClass, 2);
        return Enumerable.Range(0, traits.Count).Select(index => reader.U16(traits.Data + index * 2)).ToArray();
    }

    /**
     * Sets the identifier and keeps exactly the weapon-type trait whose name matches it, or none.
     * @return The definition and whether a stock trait matched.
     */
    public static (byte[] Definition, bool StockTrait) SetType(ReadOnlyMemory<byte> definition, string typeName,
        ReadOnlyMemory<byte> traitTable, ReadOnlyMemory<byte> categoryTable)
    {
        string identifier = Identifier(typeName);
        var (first, last) = CategoryRange(categoryTable);
        var traitHashes = TraitHashes(traitTable);
        int match = traitHashes.ToList().IndexOf(InvestmentItemClone.Fnv1(TraitCategory + "." + identifier));
        if (match >= 0 && (match < first || match > last)) match = -1;
        var current = ReadTraits(definition);
        var kept = current.Where(trait => trait < first || trait > last).ToList();
        if (match >= 0) kept.Add((ushort)match);
        byte[] result = SetTraits(definition, kept);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(IdentifierField), InvestmentItemClone.Fnv1(identifier));
        return (result, match >= 0);
    }

    /** The trait list shrinks or grows in place when it fits, else moves to the end of the definition. */
    private static byte[] SetTraits(ReadOnlyMemory<byte> definition, IReadOnlyList<ushort> traits)
    {
        var reader = new TagReader(definition);
        var current = reader.Array(TraitsField, TraitClass, 2);
        byte[] result;
        int data;
        if (traits.Count <= current.Count && current.Count > 0)
        {
            result = definition.ToArray();
            data = current.Data;
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(data - 16), traits.Count);
        }
        else
        {
            int header = (definition.Length + 19) & ~15;
            result = new byte[header + 16 + traits.Count * 2];
            definition.Span.CopyTo(result);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(header - 4), ArrayMarker);
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(header), traits.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(header + 8), TraitClass);
            BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(TraitsField + 8), header - TraitsField - 8);
            if (BinaryPrimitives.ReadInt64LittleEndian(definition.Span) == definition.Length)
                BinaryPrimitives.WriteInt64LittleEndian(result, result.Length);
            data = header + 16;
        }
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(TraitsField), traits.Count);
        for (int index = 0; index < traits.Count; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(data + index * 2), traits[index]);
        return result;
    }

    private static IReadOnlyList<uint> TraitHashes(ReadOnlyMemory<byte> table)
    {
        var reader = new TagReader(table);
        var rows = reader.Array(8, stride: 8);
        return Enumerable.Range(0, rows.Count).Select(index => reader.U32(rows.Data + index * 8)).ToArray();
    }

    /** Category rows are {u32 name hash, u16 first trait, u16 last trait}. */
    private static (int First, int Last) CategoryRange(ReadOnlyMemory<byte> table)
    {
        var reader = new TagReader(table);
        var rows = reader.Array(8, stride: 8);
        uint hash = InvestmentItemClone.Fnv1(TraitCategory);
        for (int index = 0; index < rows.Count; index++)
            if (reader.U32(rows.Data + index * 8) == hash)
                return (reader.U16(rows.Data + index * 8 + 4), reader.U16(rows.Data + index * 8 + 6));
        throw new InvalidDataException("The trait categories have no weapon_type range.");
    }
}
