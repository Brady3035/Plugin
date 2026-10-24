using System;
using System.Collections.Generic;

namespace Moonfin.Romm;

/// <summary>
/// The RomM platform slugs Moonfin has an emulator core for, with the short name the apps show
/// for each. Matching is exact on purpose: a guessed core for a system like Saturn would list
/// games that never boot, so any platform not listed here stays hidden.
/// </summary>
public static class RommPlatformCores
{
    private static readonly Dictionary<string, (string Core, string ShortName)> Platforms =
        new Dictionary<string, (string Core, string ShortName)>(StringComparer.OrdinalIgnoreCase)
        {
            ["nes"] = ("nes", "NES"),
            ["famicom"] = ("nes", "Famicom"),
            ["fds"] = ("nes", "Famicom Disk System"),
            ["snes"] = ("snes", "SNES"),
            ["sfam"] = ("snes", "Super Famicom"),
            ["gb"] = ("gb", "Game Boy"),
            ["gbc"] = ("gb", "Game Boy Color"),
            ["gba"] = ("gba", "GBA"),
            ["genesis"] = ("segaMD", "Genesis"),
            ["sms"] = ("segaMS", "Master System"),
            ["sg1000"] = ("segaMS", "SG-1000"),
            ["gamegear"] = ("segaGG", "Game Gear"),
            ["n64"] = ("n64", "N64"),
            ["nds"] = ("nds", "Nintendo DS"),
            ["psx"] = ("psx", "PlayStation"),
            ["psp"] = ("psp", "PSP"),
            ["arcade"] = ("arcade", "Arcade"),
            ["neogeoaes"] = ("arcade", "Neo Geo AES"),
            ["neogeomvs"] = ("arcade", "Neo Geo MVS"),
            ["tg16"] = ("pce", "TurboGrafx-16"),
            ["atari2600"] = ("atari2600", "Atari 2600"),
            ["atari7800"] = ("atari7800", "Atari 7800"),
            ["lynx"] = ("lynx", "Lynx"),
            ["wonderswan"] = ("ws", "WonderSwan"),
            ["wonderswan-color"] = ("ws", "WonderSwan Color"),
            ["neo-geo-pocket"] = ("ngp", "Neo Geo Pocket"),
            ["neo-geo-pocket-color"] = ("ngp", "Neo Geo Pocket Color"),
            ["virtualboy"] = ("vb", "Virtual Boy"),
        };

    public static string? CoreFor(string? slug) =>
        slug != null && Platforms.TryGetValue(slug.Trim(), out var platform) ? platform.Core : null;

    public static string? ShortNameFor(string? slug) =>
        slug != null && Platforms.TryGetValue(slug.Trim(), out var platform) ? platform.ShortName : null;
}
