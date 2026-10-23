using Hako.Core.Minecraft;
using System.Text;

namespace Hako.Tests;

[TestClass]
public sealed class WorldMetadataTests
{
    [TestMethod]
    public void ReadsMetadataFieldsAndLeavesMissingValuesUnknown()
    {
        var fixture = WorldTestFixture.Create("Modern", "1.21.1", 3, 1_700_000_000_123);

        var metadata = WorldMetadata.Read(fixture.Compressed);

        Assert.AreEqual("Modern", metadata.Name);
        Assert.AreEqual("1.21.1", metadata.Version);
        Assert.AreEqual(3, metadata.GameType);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123), metadata.LastPlayed);

        var missing = WorldMetadata.Read(WorldTestFixture.Create(includeName: false, version: null,
            gameType: null, lastPlayed: null).Compressed);
        Assert.IsNull(missing.Name);
        Assert.IsNull(missing.Version);
        Assert.IsNull(missing.GameType);
        Assert.IsNull(missing.LastPlayed);
    }

    [TestMethod]
    public void ReadsStandardUtf8AndJavaModifiedUtf8NullAndSurrogatePairs()
    {
        const string unicode = "A\0🌸";
        var fixture = WorldTestFixture.Create(unicode);
        var utf8 = new UTF8Encoding(false, true).GetBytes(unicode);
        var standardNbt = ReplaceName(fixture, utf8);

        Assert.AreEqual(unicode, WorldMetadata.Read(WorldTestFixture.Gzip(standardNbt)).Name);
        Assert.AreEqual(unicode, WorldMetadata.Read(fixture.Compressed).Name);
    }

    [TestMethod]
    public void RenamingPreservesEveryPayloadByteOutsideLevelName()
    {
        var unknown = WorldTestFixture.ByteArrayTag("FutureBytes", [0, 1, 2, 0xfe, 0xff]);
        var fixture = WorldTestFixture.Create("Old", extra: unknown);
        var renamed = WorldMetadata.Renamed(fixture.Compressed, "New\0🌸");
        var newName = WorldTestFixture.ModifiedUtf8("New\0🌸");
        var renamedNbt = WorldTestFixture.Decompress(renamed);
        var before = fixture.Nbt.AsSpan(0, fixture.NameLengthOffset!.Value).ToArray();
        var after = fixture.Nbt.AsSpan(fixture.NameValueOffset!.Value + fixture.NameByteLength!.Value).ToArray();
        var newValueOffset = fixture.NameLengthOffset.Value + 2;

        CollectionAssert.AreEqual(before, renamedNbt.AsSpan(0, fixture.NameLengthOffset.Value).ToArray());
        Assert.AreEqual(newName.Length, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(
            renamedNbt.AsSpan(fixture.NameLengthOffset.Value, 2)));
        CollectionAssert.AreEqual(newName, renamedNbt.AsSpan(newValueOffset, newName.Length).ToArray());
        CollectionAssert.AreEqual(after, renamedNbt.AsSpan(newValueOffset + newName.Length).ToArray());
        Assert.AreEqual("New\0🌸", WorldMetadata.Read(renamed).Name);
    }

    [TestMethod]
    public void RenamingAddsMissingLevelNameInsideDataCompound()
    {
        var fixture = WorldTestFixture.Create(name: null, includeName: false,
            extra: WorldTestFixture.ByteArrayTag("future", [8, 7, 6]));

        var renamed = WorldMetadata.Renamed(fixture.Compressed, "Copied");

        Assert.AreEqual("Copied", WorldMetadata.Read(renamed).Name);
        StringAssert.Contains(Encoding.Latin1.GetString(WorldTestFixture.Decompress(renamed)), "LevelName");
    }

    [TestMethod]
    public void RejectsMalformedTagsLengthsNestingAndUtf8()
    {
        var negativeLength = WorldTestFixture.Create(extra: [7, 0, 1, (byte)'a', 255, 255, 255, 255]);
        var truncatedArray = WorldTestFixture.Create(extra: [12, 0, 1, (byte)'a', 0, 0, 0, 10]);
        var unsupportedTag = WorldTestFixture.Create(extra: [13, 0, 1, (byte)'a']);
        var deep = new List<byte>();
        for (var index = 0; index < 66; index++)
        {
            deep.Add(10);
            deep.AddRange([0, 1, (byte)'x']);
        }
        deep.AddRange(Enumerable.Repeat((byte)0, 66));
        var tooDeep = WorldTestFixture.Create(extra: deep.ToArray());
        var malformedTextFixture = WorldTestFixture.Create("x");
        var malformedText = ReplaceName(malformedTextFixture, [0xc1, 0x81]);

        Assert.AreEqual(WorldMetadataError.InvalidNbt, Assert.ThrowsExactly<WorldMetadataException>(
            () => WorldMetadata.Read(negativeLength.Compressed)).Error);
        Assert.AreEqual(WorldMetadataError.InvalidNbt, Assert.ThrowsExactly<WorldMetadataException>(
            () => WorldMetadata.Read(truncatedArray.Compressed)).Error);
        Assert.AreEqual(WorldMetadataError.InvalidNbt, Assert.ThrowsExactly<WorldMetadataException>(
            () => WorldMetadata.Read(unsupportedTag.Compressed)).Error);
        Assert.AreEqual(WorldMetadataError.NestingTooDeep, Assert.ThrowsExactly<WorldMetadataException>(
            () => WorldMetadata.Read(tooDeep.Compressed)).Error);
        Assert.AreEqual(WorldMetadataError.InvalidText, Assert.ThrowsExactly<WorldMetadataException>(
            () => WorldMetadata.Read(WorldTestFixture.Gzip(malformedText))).Error);
    }

    [TestMethod]
    public void EnforcesCompressedExpandedAndCancellationLimits()
    {
        var oversizedInput = new byte[WorldMetadata.MaximumBytes + 1];
        var expanded = WorldTestFixture.Gzip(new byte[WorldMetadata.MaximumBytes + 1]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.AreEqual(WorldMetadataError.CompressedDataTooLarge, Assert.ThrowsExactly<WorldMetadataException>(
            () => WorldMetadata.Read(oversizedInput)).Error);
        Assert.AreEqual(WorldMetadataError.DecompressedDataTooLarge, Assert.ThrowsExactly<WorldMetadataException>(
            () => WorldMetadata.Read(expanded)).Error);
        Assert.ThrowsExactly<OperationCanceledException>(() => WorldMetadata.Read(
            WorldTestFixture.Create().Compressed, cancelled.Token));
    }

    [TestMethod]
    public void RejectsTruncatedGzipInput()
    {
        var compressed = WorldTestFixture.Create().Compressed;
        var badChecksum = compressed.ToArray();
        badChecksum[^8] ^= 1;

        Assert.AreEqual(WorldMetadataError.InvalidGzip, Assert.ThrowsExactly<WorldMetadataException>(
            () => WorldMetadata.Read(compressed.AsSpan(0, compressed.Length - 8))).Error);
        Assert.AreEqual(WorldMetadataError.InvalidGzip, Assert.ThrowsExactly<WorldMetadataException>(
            () => WorldMetadata.Read(badChecksum)).Error);
    }

    private static byte[] ReplaceName(WorldMetadataFixture fixture, byte[] encodedName)
    {
        var lengthOffset = fixture.NameLengthOffset!.Value;
        var valueOffset = fixture.NameValueOffset!.Value;
        var oldLength = fixture.NameByteLength!.Value;
        var output = new byte[fixture.Nbt.Length - oldLength + encodedName.Length];
        fixture.Nbt.AsSpan(0, lengthOffset).CopyTo(output);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(lengthOffset, 2),
            checked((ushort)encodedName.Length));
        encodedName.CopyTo(output.AsSpan(lengthOffset + 2));
        fixture.Nbt.AsSpan(valueOffset + oldLength).CopyTo(output.AsSpan(lengthOffset + 2 + encodedName.Length));
        return output;
    }
}
