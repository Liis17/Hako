using System.Xml.Linq;

namespace Hako.Tests;

[TestClass]
public sealed class InstanceIconGlyphsTests
{
    [TestMethod]
    public void BuiltInIconsKeepTheMacSymbolNamesStoredInProfiles()
    {
        // instances.json shares iconSymbol values with macOS (InstanceIconPicker in InstanceComponents.swift).
        CollectionAssert.AreEqual(
            new[] { "shippingbox.fill", "cube.fill", "leaf.fill", "flame.fill", "bolt.fill", "moon.stars.fill" },
            Hako.InstanceIconGlyphs.BuiltIn.Select(icon => icon.Symbol).ToArray());
        Assert.AreEqual(Hako.InstanceIconGlyphs.BuiltIn.Count, Hako.InstanceIconGlyphs.BuiltIn.Select(icon => icon.Glyph).Distinct().Count());
        Assert.AreEqual("shippingbox.fill", Hako.InstanceIconGlyphs.DefaultSymbol);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    [DataRow("star.fill")]
    public void CustomPicturesAndUnknownSymbolsFallBackToTheBox(string? symbol)
    {
        Assert.AreEqual(Hako.InstanceIconGlyphs.Glyph("shippingbox.fill"), Hako.InstanceIconGlyphs.Glyph(symbol));
        Assert.AreNotEqual(Hako.InstanceIconGlyphs.Glyph("shippingbox.fill"), Hako.InstanceIconGlyphs.Glyph("flame.fill"));
    }

    [TestMethod]
    public void IconTitlesHaveRussianAndEnglishText()
    {
        foreach (var language in new[] { "ru-RU", "en-US" })
        {
            var names = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Resources", language, "Resources.resw"))
                .Root!.Elements("data").Select(element => (string?)element.Attribute("name")).ToHashSet();
            foreach (var icon in Hako.InstanceIconGlyphs.BuiltIn)
            {
                Assert.IsTrue(names.Contains(icon.TitleKey), $"{language}: {icon.TitleKey}");
            }
        }
    }
}
