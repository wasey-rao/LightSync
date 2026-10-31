namespace LightSync.Ble;

public enum EffectKind { Fade, Jump, Blink }

public sealed record LightEffect(string Name, EffectKind Kind, byte Code, string[] Colors);

/// <summary>
/// Command set for ELK-BLEDOM controllers (the ones driven by the "Lotus Lantern" app).
/// Every command is a 9-byte frame: 7E 00 [command] [args x5] EF.
/// </summary>
public static class ElkProtocol
{
    public static readonly Guid ServiceUuid = Guid.Parse("0000fff0-0000-1000-8000-00805f9b34fb");
    public static readonly Guid WriteCharacteristicUuid = Guid.Parse("0000fff3-0000-1000-8000-00805f9b34fb");

    public static byte[] PowerOn() => [0x7E, 0x00, 0x04, 0xF0, 0x00, 0x01, 0xFF, 0x00, 0xEF];
    public static byte[] PowerOff() => [0x7E, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0x00, 0xEF];
    public static byte[] Color(byte r, byte g, byte b) => [0x7E, 0x00, 0x05, 0x03, r, g, b, 0x00, 0xEF];
    public static byte[] Brightness(int percent) => [0x7E, 0x00, 0x01, Percent(percent), 0x00, 0x00, 0x00, 0x00, 0xEF];
    public static byte[] Effect(byte code) => [0x7E, 0x00, 0x03, code, 0x03, 0x00, 0x00, 0x00, 0xEF];
    public static byte[] EffectSpeed(int percent) => [0x7E, 0x00, 0x02, Percent(percent), 0x00, 0x00, 0x00, 0x00, 0xEF];

    static byte Percent(int value) => (byte)Math.Clamp(value, 0, 100);

    const string R = "#FF3B3B", G = "#2BFF6A", B = "#3B7BFF", Y = "#FFE53B", C = "#2BF0FF", M = "#FF3BF5", W = "#FFFFFF";
    static readonly string[] All = [R, G, B, Y, C, M, W];

    public static readonly IReadOnlyList<LightEffect> Effects =
    [
        new("Rainbow fade", EffectKind.Fade, 0x8A, All),
        new("RGB fade", EffectKind.Fade, 0x89, [R, G, B]),
        new("Red / green", EffectKind.Fade, 0x92, [R, G]),
        new("Red / blue", EffectKind.Fade, 0x93, [R, B]),
        new("Green / blue", EffectKind.Fade, 0x94, [G, B]),
        new("Red pulse", EffectKind.Fade, 0x8B, [R, "#3A0000"]),
        new("Green pulse", EffectKind.Fade, 0x8C, [G, "#003A10"]),
        new("Blue pulse", EffectKind.Fade, 0x8D, [B, "#00103A"]),
        new("Yellow pulse", EffectKind.Fade, 0x8E, [Y, "#3A3000"]),
        new("Cyan pulse", EffectKind.Fade, 0x8F, [C, "#00343A"]),
        new("Magenta pulse", EffectKind.Fade, 0x90, [M, "#3A0038"]),
        new("White pulse", EffectKind.Fade, 0x91, [W, "#3A3A3A"]),

        new("Rainbow jump", EffectKind.Jump, 0x88, All),
        new("RGB jump", EffectKind.Jump, 0x87, [R, G, B]),

        new("Rainbow strobe", EffectKind.Blink, 0x95, All),
        new("Red strobe", EffectKind.Blink, 0x96, [R, "#000000"]),
        new("Green strobe", EffectKind.Blink, 0x97, [G, "#000000"]),
        new("Blue strobe", EffectKind.Blink, 0x98, [B, "#000000"]),
        new("Yellow strobe", EffectKind.Blink, 0x99, [Y, "#000000"]),
        new("Cyan strobe", EffectKind.Blink, 0x9A, [C, "#000000"]),
        new("Magenta strobe", EffectKind.Blink, 0x9B, [M, "#000000"]),
        new("White strobe", EffectKind.Blink, 0x9C, [W, "#000000"]),
    ];
}
