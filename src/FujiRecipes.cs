using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using static PathSafety;
using static FujiSettings;
using static FujiRecipes;

static class FujiRecipes
{
    internal static FujiRecipe ReadFujiRecipe(string path)
    {
        RejectRootLinks(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("Recipe export is too large.");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 };
        var document = new XmlDocument { XmlResolver = null };
        try { using (XmlReader reader = XmlReader.Create(path, settings)) document.Load(reader); }
        catch (XmlException ex) { throw new IOException("Invalid recipe XML.", ex); }
        XmlElement root = document.DocumentElement;
        if (root == null || root.Name != "GearBackupCameraSettings" || root.GetAttribute("format") != "1" ||
            root.GetAttribute("scope") != "recipes-and-shooting-settings" || RecipeText(root, "Model") != "X100VI")
            throw new IOException("Choose an X100VI XML settings export made by Gear Backup.");
        var recipe = new FujiRecipe { Model = "X100VI", Firmware = RecipeText(root, "Firmware"), Serial = RecipeText(root, "Serial"),
            Section = RecipeText(root, "Section"), Name = RecipeText(root, "RecipeName") };
        if (!System.Text.RegularExpressions.Regex.IsMatch(recipe.Section, "^C[1-7]$") && recipe.Section != "Current shooting settings")
            throw new IOException("Unknown recipe section.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (XmlNode node in root.SelectNodes("Setting"))
        {
            var element = (XmlElement)node;
            string name = element.GetAttribute("name");
            int index = Array.IndexOf(FujiSettingNames, name);
            if (index < 0 || !names.Add(name)) throw new IOException("Unknown or duplicate recipe setting: " + name);
            string expectedCode = "0x" + (recipe.Section == "Current shooting settings" ? FujiLiveCodes[index] : 0xD18E + index).ToString("X4");
            if (!element.GetAttribute("code").Equals(expectedCode, StringComparison.OrdinalIgnoreCase)) throw new IOException("Recipe setting code does not match its name.");
            var value = new FujiRecipeValue { Name = name, Code = expectedCode, Raw = element.GetAttribute("raw") };
            if (element.GetAttribute("supported") == "no") value.Display = "Unavailable";
            else
            {
                ushort raw;
                int number;
                if (value.Raw.Length != 4 || !ushort.TryParse(value.Raw.Substring(2, 2) + value.Raw.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out raw) ||
                    !int.TryParse(element.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ||
                    number != (FujiSigned(index) ? (int)(short)raw : raw) || element.GetAttribute("type") != (FujiSigned(index) ? "int16" : "uint16"))
                    throw new IOException("Inconsistent recipe value: " + name);
                value.Number = number;
                value.Display = FormatFujiRecipeValue(index, number);
            }
            recipe.Values.Add(value);
        }
        if (recipe.Values.Count != FujiSettingNames.Length) throw new IOException("Recipe export is missing settings.");
        recipe.Values.Sort((a, b) => Array.IndexOf(FujiSettingNames, a.Name).CompareTo(Array.IndexOf(FujiSettingNames, b.Name)));
        return recipe;
    }
    static string RecipeText(XmlElement root, string name)
    {
        XmlNodeList nodes = root.SelectNodes(name);
        if (nodes.Count > 1) throw new IOException("Duplicate recipe metadata: " + name);
        return nodes.Count == 0 ? "" : nodes[0].InnerText;
    }
    // Protocol encodings: https://github.com/ILFforever/fujifilm-ptp-recipes/blob/main/docs/properties.md
    // Unknown values remain explicit protocol values, rather than being guessed or clamped.
    internal static string FormatFujiRecipeValue(int index, int number)
    {
        string text = number.ToString(CultureInfo.InvariantCulture);
        if (FujiSigned(index) && number == -32768 || !FujiSigned(index) && number == 65535) return "Default / unavailable";
        if (index == 4)
        {
            string[] films = { "PROVIA / Standard", "Velvia / Vivid", "ASTIA / Soft", "PRO Neg. Hi", "PRO Neg. Std", "Monochrome",
                "Monochrome + Yellow", "Monochrome + Red", "Monochrome + Green", "Sepia", "Classic Chrome", "ACROS", "ACROS + Yellow",
                "ACROS + Red", "ACROS + Green", "ETERNA / Cinema", "Classic Neg.", "ETERNA Bleach Bypass", "Nostalgic Neg.", "REALA ACE" };
            if (number >= 1 && number <= films.Length) return films[number - 1];
        }
        if (index == 2 && (number == 100 || number == 200 || number == 400)) return "DR" + number + "%";
        if (index == 2 && number == 0) return "Auto";
        if (index == 3)
        {
            if (number >= 0 && number <= 2) return new[] { "Off", "Weak", "Strong" }[number];
            if (number == 32768) return "Auto";
        }
        if (index == 7 && number >= 1 && number <= 6) return new[] { "Off", "Weak · Small", "Strong · Small", "Weak · Large", "Strong · Large", "Off / default" }[number - 1];
        if (index >= 8 && index <= 10 && number >= 1 && number <= 3) return new[] { "Off", "Weak", "Strong" }[number - 1];
        if (index == 11)
        {
            int[] codes = { 2, 32800, 32801, 4, 6, 8, 32769, 32770, 32771, 32774, 32775, 32776, 32777, 32778 };
            string[] labels = { "Auto", "Auto · White priority", "Auto · Ambience priority", "Daylight", "Incandescent", "Underwater", "Fluorescent 1", "Fluorescent 2", "Fluorescent 3", "Shade", "Color temperature", "Custom 1", "Custom 2", "Custom 3" };
            int position = Array.IndexOf(codes, number);
            if (position >= 0) return labels[position];
        }
        if (index == 14) return text + " K";
        if (index == 12 || index == 13) return SignedRecipeDial(number);
        if (index == 5 || index == 6 || index >= 15 && index <= 18 || index == 20) return SignedRecipeDial(number / 10m);
        if (index == 19)
        {
            int[] codes = { 32768, 28672, 16384, 12288, 8192, 4096, 0, 24576, 20480 };
            int position = Array.IndexOf(codes, number);
            if (position >= 0) return SignedRecipeDial(position - 4);
        }
        if (index == 22 && (number == 1 || number == 2)) return number == 1 ? "sRGB" : "Adobe RGB";
        return "Protocol value " + text;
    }
    static string SignedRecipeDial(decimal number) { return (number > 0 ? "+" : "") + number.ToString("0.#", CultureInfo.InvariantCulture); }
    internal static string FujiRecipeReport(FujiRecipe recipe)
    {
        var text = new StringBuilder();
        text.AppendLine(recipe.ToString()).AppendLine("Fujifilm " + recipe.Model + " · firmware " + recipe.Firmware).AppendLine();
        foreach (FujiRecipeValue value in recipe.Values) text.Append(value.Name).Append(": ").AppendLine(value.Display);
        return text.ToString();
    }
}

internal sealed class FujiRecipe
{
    public string Model, Firmware, Serial, Section, Name;
    public readonly List<FujiRecipeValue> Values = new List<FujiRecipeValue>();
    public override string ToString() { return Section + (string.IsNullOrEmpty(Name) ? "" : " · " + Name); }
}

internal sealed class FujiRecipeValue
{
    public string Name, Code, Raw, Display;
    public int? Number;
}
