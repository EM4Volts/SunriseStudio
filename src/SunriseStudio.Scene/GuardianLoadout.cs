using System.Text.Json.Serialization;

namespace SunriseStudio.Scene;

/** One guardian's choices. Numbers are positions in the race/gender row's lists, so a row change keeps a valid pick. */
public sealed record GuardianLoadout
{
    /** Matches the default 36-byte header: arrangement 37, variants 5/0/1, dyes 746,769,582,840-842,787,711 on row 0. */
    public static GuardianLoadout Default { get; } = new();
    /** Row of table 0x81327D65, 0..5; row 0 is Human male. */
    public int RaceGender { get; init; }
    /** Position in the row +64 face preset list; row 0 position 2 is arrangement 37. */
    public int Preset { get; init; } = 2;
    /** Header +2, selector 15. */
    public int Variant2 { get; init; } = 5;
    /** Header +4, selector 19; 0 is the empty entry. */
    public int Variant4 { get; init; } = 1;
    /** Position in the row +192 list (assumed header +6). */
    public int Dye06 { get; init; } = 8;
    /** Row +224 list (assumed header +8). */
    public int Dye08 { get; init; } = 5;
    /** Row +256 list (assumed header +10). */
    public int Dye10 { get; init; } = 6;
    /** Row +288 triple list (assumed header +12, +14 and +16). */
    public int Dye12 { get; init; } = 20;
    /** Row +320 triple list (assumed header +18, +20 and +22; the game uses only +22). */
    public int Dye18 { get; init; } = 20;
    /** Row +352 list (assumed header +24). */
    public int Dye24 { get; init; }
    /** Item definition tags; 0 in an armor slot is the class's gear-preview placeholder. */
    public uint Helmet { get; init; }
    public uint Gauntlets { get; init; }
    public uint Chest { get; init; }
    public uint Legs { get; init; }
    public uint ClassArmor { get; init; }
    /** 0 is no weapon. */
    public uint Weapon { get; init; }
    /** False takes the helmet off; it is the only way to leave the helmet slot empty. */
    public bool ShowHelmet { get; init; } = true;

    /** The six dye positions in row list order: Dye06, Dye08, Dye10, Dye12, Dye18, Dye24. */
    [JsonIgnore] public int[] DyePositions => [Dye06, Dye08, Dye10, Dye12, Dye18, Dye24];

    /** Sets dye position list (0..5, in DyePositions order). */
    public GuardianLoadout WithDye(int list, int position) => list switch
    {
        0 => this with { Dye06 = position },
        1 => this with { Dye08 = position },
        2 => this with { Dye10 = position },
        3 => this with { Dye12 = position },
        4 => this with { Dye18 = position },
        5 => this with { Dye24 = position },
        _ => throw new ArgumentOutOfRangeException(nameof(list))
    };

    /** The item in armor slot 0..4: helmet, gauntlets, chest, legs, class item. */
    public uint Armor(int slot) => slot switch
    {
        0 => Helmet, 1 => Gauntlets, 2 => Chest, 3 => Legs, 4 => ClassArmor,
        _ => throw new ArgumentOutOfRangeException(nameof(slot))
    };

    /** Sets armor slot 0..4, in Armor order. */
    public GuardianLoadout WithArmor(int slot, uint item) => slot switch
    {
        0 => this with { Helmet = item },
        1 => this with { Gauntlets = item },
        2 => this with { Chest = item },
        3 => this with { Legs = item },
        4 => this with { ClassArmor = item },
        _ => throw new ArgumentOutOfRangeException(nameof(slot))
    };
}
