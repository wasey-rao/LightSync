using System.Globalization;

namespace LightSync.Ble;

public static class BleAddress
{
    public static string Format(ulong address) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((address >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    /// <summary>Accepts "BE:58:94:02:AF:91", "be-58-94-02-af-91" or "BE589402AF91".</summary>
    public static bool TryParse(string? text, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cleaned = text.Trim().Replace(":", "").Replace("-", "").Replace(" ", "");
        return cleaned.Length == 12 && ulong.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }
}
