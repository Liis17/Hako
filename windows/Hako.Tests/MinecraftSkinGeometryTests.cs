using Hako.Graphics;
using System.Numerics;

namespace Hako.Tests;

[TestClass]
public sealed class MinecraftSkinGeometryTests
{
    [TestMethod]
    public void BuildFrame_UsesMinecraftFrontFaceUvLayoutAndExpectedTriangleCounts()
    {
        var frame = BuildFrame(slim: false, hasCape: false);

        Assert.AreEqual(72, frame.OpaqueCount);
        Assert.AreEqual(72, frame.TranslucentCount);
        Assert.AreEqual((8 + 0.05f) / 64, frame.Opaque[0].A.UV.X, 0.00001f);
        Assert.AreEqual((16 - 0.05f) / 96, frame.Opaque[0].A.UV.Y, 0.00001f);
    }

    [TestMethod]
    public void BuildFrame_SlimArmsAreNarrowerThanClassicArms()
    {
        var classic = BuildFrame(slim: false, hasCape: false);
        var slim = BuildFrame(slim: true, hasCape: false);

        Assert.AreEqual(-0.5f, classic.Opaque[24].A.Position.X, 0.00001f);
        Assert.AreEqual(-0.4375f, slim.Opaque[24].A.Position.X, 0.00001f);
    }

    [TestMethod]
    public void BuildFrame_WalkingChangesLimbPoseAndCapeAddsSortedGeometry()
    {
        var rest = BuildFrame(slim: false, hasCape: false);
        var walking = BuildFrame(slim: false, hasCape: true, animationElapsedSeconds: 0.375, isWalking: true);

        Assert.AreEqual(72, walking.OpaqueCount);
        Assert.AreEqual(84, walking.TranslucentCount);
        Assert.IsTrue(Vector3.Distance(rest.Opaque[24].A.Position, walking.Opaque[24].A.Position) > 0.001f);
        for (var index = 1; index < walking.TranslucentCount; index++)
        {
            Assert.IsTrue(walking.Translucent[index - 1].Depth <= walking.Translucent[index].Depth);
        }
    }

    [TestMethod]
    public void BuildFrame_YawRotatesTheModelAndStaticPoseDoesNotDependOnElapsedTime()
    {
        var initial = BuildFrame(slim: false, hasCape: false, animationElapsedSeconds: 0);
        var laterStatic = BuildFrame(slim: false, hasCape: false, animationElapsedSeconds: 1.1);
        var rotated = BuildFrame(slim: false, hasCape: false, yawRadians: 0.8f);

        Assert.AreEqual(initial.Opaque[0].A.Position, laterStatic.Opaque[0].A.Position);
        Assert.AreNotEqual(initial.Opaque[0].A.Position, rotated.Opaque[0].A.Position);
    }

    private static SkinFrame BuildFrame(
        bool slim,
        bool hasCape,
        float yawRadians = 0,
        float pitchRadians = 0,
        double animationElapsedSeconds = 0,
        bool isWalking = false)
    {
        var opaque = new MinecraftSkinTriangle[MinecraftSkinGeometry.OpaqueTriangleCapacity];
        var translucent = new MinecraftSkinTriangle[MinecraftSkinGeometry.TranslucentTriangleCapacity];
        MinecraftSkinGeometry.BuildFrame(
            slim,
            hasCape,
            yawRadians,
            pitchRadians,
            animationElapsedSeconds,
            isWalking,
            opaque,
            translucent,
            out var opaqueCount,
            out var translucentCount);
        return new(opaque, translucent, opaqueCount, translucentCount);
    }

    private sealed record SkinFrame(
        MinecraftSkinTriangle[] Opaque,
        MinecraftSkinTriangle[] Translucent,
        int OpaqueCount,
        int TranslucentCount);
}
