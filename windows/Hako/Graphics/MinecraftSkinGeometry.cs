using System.Collections.Generic;
using System.Numerics;

namespace Hako.Graphics;

public readonly record struct MinecraftSkinVertex(Vector3 Position, Vector2 UV, float Shade);

public readonly record struct MinecraftSkinTriangle(
    MinecraftSkinVertex A,
    MinecraftSkinVertex B,
    MinecraftSkinVertex C,
    float Depth);

/// <summary>
/// Builds the Minecraft player mesh and walking pose without WinUI or graphics API dependencies.
/// Model space uses +Y up and units of one Minecraft pixel divided by 16. The camera looks from +Z toward the origin.
/// UV coordinates start at the atlas top-left; U increases right and V increases down.
/// </summary>
public static class MinecraftSkinGeometry
{
    public const float InitialYawRadians = 0.5235988f;
    public const float InitialPitchRadians = 0.36651915f;
    public const int OpaqueTriangleCapacity = 72;
    public const int TranslucentTriangleCapacity = 84;

    private const float PixelsToWorld = 1f / 16f;
    private const float UvInset = 0.05f;
    private const int TextureWidth = 64;
    private const int AtlasHeight = 96;

    /// <summary>
    /// Writes opaque skin triangles and back-to-front transparent layer triangles into caller-owned buffers.
    /// Yaw and pitch are radians around +Y and +X. Depth is model-space Z after rotation; smaller values are farther away.
    /// </summary>
    public static void BuildFrame(
        bool slim,
        bool hasCape,
        float yawRadians,
        float pitchRadians,
        double animationElapsedSeconds,
        bool isWalking,
        Span<MinecraftSkinTriangle> opaqueTriangles,
        Span<MinecraftSkinTriangle> translucentTriangles,
        out int opaqueCount,
        out int translucentCount)
    {
        if (!float.IsFinite(yawRadians) || !float.IsFinite(pitchRadians) || !double.IsFinite(animationElapsedSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(yawRadians), "Поза должна содержать конечные значения.");
        }
        if (opaqueTriangles.Length < OpaqueTriangleCapacity || translucentTriangles.Length < TranslucentTriangleCapacity)
        {
            throw new ArgumentException("Буферы геометрии должны вмещать полный набор треугольников.");
        }

        var phase = isWalking ? (float)((animationElapsedSeconds % 1.5) * (2 * Math.PI / 1.5)) : 0f;
        var swing = MathF.Sin(phase);
        Span<Quaternion> limbRotations = stackalloc Quaternion[4];
        for (var index = 0; index < limbRotations.Length; index++)
        {
            var amplitude = index < 2 ? 18f : 20f;
            var direction = index is 0 or 3 ? -1f : 1f;
            limbRotations[index] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, swing * amplitude * direction * MathF.PI / 180f);
        }

        var rootRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitchRadians)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawRadians);
        var capePitch = (20f + (isWalking ? swing * 3f : 0f)) * MathF.PI / 180f;
        var capeRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, capePitch);
        Span<Cube> parts = stackalloc Cube[6];
        CreateParts(slim, parts);
        Span<MinecraftSkinVertex> faceVertices = stackalloc MinecraftSkinVertex[4];

        opaqueCount = 0;
        translucentCount = 0;
        for (var index = 0; index < parts.Length; index++)
        {
            AppendCube(parts[index], 0, rootRotation, capeRotation, limbRotations, faceVertices,
                opaqueTriangles, translucentTriangles, ref opaqueCount, ref translucentCount);
            var overlayUV = index switch
            {
                0 => new Vector2(32, 0),
                1 => new Vector2(16, 32),
                2 => new Vector2(40, 32),
                3 => new Vector2(48, 48),
                4 => new Vector2(0, 32),
                _ => new Vector2(0, 48)
            };
            AppendCube(parts[index] with
            {
                UV = overlayUV,
                Inflation = index == 0 ? 0.5f : 0.25f
            }, 0, rootRotation, capeRotation, limbRotations, faceVertices,
                opaqueTriangles, translucentTriangles, ref opaqueCount, ref translucentCount);
        }

        if (hasCape)
        {
            var cape = new Cube(6, new Vector3(10, 16, 1), new Vector3(0, -8, 0), new Vector3(0, 8, -2.75f),
                Vector2.Zero, 0);
            AppendCube(cape, 64, rootRotation, capeRotation, limbRotations, faceVertices,
                opaqueTriangles, translucentTriangles, ref opaqueCount, ref translucentCount);
        }

        translucentTriangles[..translucentCount].Sort(TransparentTriangleComparer.Instance);
    }

    private static void CreateParts(bool slim, Span<Cube> parts)
    {
        var width = slim ? 3f : 4f;
        var armX = 4f + width / 2f;
        parts[0] = new Cube(0, new Vector3(8, 8, 8), Vector3.Zero, new Vector3(0, 12, 0), new Vector2(0, 0), 0);
        parts[1] = new Cube(1, new Vector3(8, 12, 4), Vector3.Zero, new Vector3(0, 2, 0), new Vector2(16, 16), 0);
        parts[2] = new Cube(2, new Vector3(width, 12, 4), new Vector3(0, -6, 0), new Vector3(-armX, 8, 0), new Vector2(40, 16), 0);
        parts[3] = new Cube(3, new Vector3(width, 12, 4), new Vector3(0, -6, 0), new Vector3(armX, 8, 0), new Vector2(32, 48), 0);
        parts[4] = new Cube(4, new Vector3(4, 12, 4), new Vector3(0, -6, 0), new Vector3(-2, -4, 0), new Vector2(0, 16), 0);
        parts[5] = new Cube(5, new Vector3(4, 12, 4), new Vector3(0, -6, 0), new Vector3(2, -4, 0), new Vector2(16, 48), 0);
    }

    private static void AppendCube(
        in Cube cube,
        int textureYOffset,
        Quaternion rootRotation,
        Quaternion capeRotation,
        Span<Quaternion> limbRotations,
        Span<MinecraftSkinVertex> faceVertices,
        Span<MinecraftSkinTriangle> opaqueTriangles,
        Span<MinecraftSkinTriangle> translucentTriangles,
        ref int opaqueCount,
        ref int translucentCount)
    {
        for (var face = 0; face < 6; face++)
        {
            CreateFaceVertices(cube, textureYOffset, face, rootRotation, capeRotation, limbRotations, faceVertices);
            var first = new MinecraftSkinTriangle(faceVertices[0], faceVertices[1], faceVertices[2],
                (faceVertices[0].Position.Z + faceVertices[1].Position.Z + faceVertices[2].Position.Z) / 3f);
            var second = new MinecraftSkinTriangle(faceVertices[0], faceVertices[2], faceVertices[3],
                (faceVertices[0].Position.Z + faceVertices[2].Position.Z + faceVertices[3].Position.Z) / 3f);

            if (textureYOffset != 0 || cube.Inflation > 0)
            {
                translucentTriangles[translucentCount++] = first;
                translucentTriangles[translucentCount++] = second;
            }
            else
            {
                opaqueTriangles[opaqueCount++] = first;
                opaqueTriangles[opaqueCount++] = second;
            }
        }
    }

    private static void CreateFaceVertices(
        in Cube cube,
        int textureYOffset,
        int face,
        Quaternion rootRotation,
        Quaternion capeRotation,
        Span<Quaternion> limbRotations,
        Span<MinecraftSkinVertex> result)
    {
        var half = cube.Size / 2f + new Vector3(cube.Inflation);
        var x = half.X;
        var y = half.Y;
        var z = half.Z;
        Vector3 p0;
        Vector3 p1;
        Vector3 p2;
        Vector3 p3;
        Vector4 uv;
        switch (face)
        {
            case 0:
                p0 = new Vector3(-x, -y, z); p1 = new Vector3(x, -y, z); p2 = new Vector3(x, y, z); p3 = new Vector3(-x, y, z);
                uv = new Vector4(cube.UV.X + cube.Size.Z, cube.UV.Y + cube.Size.Z, cube.Size.X, cube.Size.Y);
                break;
            case 1:
                p0 = new Vector3(x, -y, -z); p1 = new Vector3(-x, -y, -z); p2 = new Vector3(-x, y, -z); p3 = new Vector3(x, y, -z);
                uv = new Vector4(cube.UV.X + 2 * cube.Size.Z + cube.Size.X, cube.UV.Y + cube.Size.Z, cube.Size.X, cube.Size.Y);
                break;
            case 2:
                p0 = new Vector3(-x, -y, -z); p1 = new Vector3(-x, -y, z); p2 = new Vector3(-x, y, z); p3 = new Vector3(-x, y, -z);
                uv = new Vector4(cube.UV.X, cube.UV.Y + cube.Size.Z, cube.Size.Z, cube.Size.Y);
                break;
            case 3:
                p0 = new Vector3(x, -y, z); p1 = new Vector3(x, -y, -z); p2 = new Vector3(x, y, -z); p3 = new Vector3(x, y, z);
                uv = new Vector4(cube.UV.X + cube.Size.Z + cube.Size.X, cube.UV.Y + cube.Size.Z, cube.Size.Z, cube.Size.Y);
                break;
            case 4:
                p0 = new Vector3(-x, y, z); p1 = new Vector3(x, y, z); p2 = new Vector3(x, y, -z); p3 = new Vector3(-x, y, -z);
                uv = new Vector4(cube.UV.X + cube.Size.Z, cube.UV.Y, cube.Size.X, cube.Size.Z);
                break;
            default:
                p0 = new Vector3(-x, -y, -z); p1 = new Vector3(x, -y, -z); p2 = new Vector3(x, -y, z); p3 = new Vector3(-x, -y, z);
                uv = new Vector4(cube.UV.X + cube.Size.Z + cube.Size.X, cube.UV.Y, cube.Size.X, cube.Size.Z);
                break;
        }

        var left = (uv.X + UvInset) / TextureWidth;
        var right = (uv.X + uv.Z - UvInset) / TextureWidth;
        var top = (uv.Y + textureYOffset + UvInset) / AtlasHeight;
        var bottom = (uv.Y + textureYOffset + uv.W - UvInset) / AtlasHeight;
        result[0] = TransformVertex(p0, left, bottom, cube.Shade(face), cube, rootRotation, capeRotation, limbRotations);
        result[1] = TransformVertex(p1, right, bottom, cube.Shade(face), cube, rootRotation, capeRotation, limbRotations);
        result[2] = TransformVertex(p2, right, top, cube.Shade(face), cube, rootRotation, capeRotation, limbRotations);
        result[3] = TransformVertex(p3, left, top, cube.Shade(face), cube, rootRotation, capeRotation, limbRotations);
    }

    private static MinecraftSkinVertex TransformVertex(
        Vector3 position,
        float u,
        float v,
        float shade,
        in Cube cube,
        Quaternion rootRotation,
        Quaternion capeRotation,
        Span<Quaternion> limbRotations)
    {
        position += cube.Offset;
        if (cube.PartIndex is >= 2 and <= 5)
        {
            position = Vector3.Transform(position, limbRotations[cube.PartIndex - 2]);
        }
        else if (cube.PartIndex == 6)
        {
            position = Vector3.Transform(position, capeRotation);
        }
        position += cube.Pivot;
        position = Vector3.Transform(position, rootRotation) * PixelsToWorld;
        return new MinecraftSkinVertex(position, new Vector2(u, v), shade);
    }

    private readonly record struct Cube(
        int PartIndex,
        Vector3 Size,
        Vector3 Offset,
        Vector3 Pivot,
        Vector2 UV,
        float Inflation)
    {
        internal float Shade(int face) => face switch
        {
            0 => 1f,
            1 => 0.82f,
            2 => 0.9f,
            3 => 0.82f,
            4 => 1f,
            _ => 0.7f
        };
    }

    private sealed class TransparentTriangleComparer : IComparer<MinecraftSkinTriangle>
    {
        internal static readonly TransparentTriangleComparer Instance = new();

        public int Compare(MinecraftSkinTriangle left, MinecraftSkinTriangle right) => left.Depth.CompareTo(right.Depth);
    }
}
