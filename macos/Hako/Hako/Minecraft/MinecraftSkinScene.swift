//
//  MinecraftSkinScene.swift
//  Hako
//

import AppKit
import Metal
import RealityKit
import simd

/// Сцена просмотра: пиксель — 1/16 метра, центр персонажа — начало координат.
@MainActor
final class MinecraftSkinScene {
    let root = Entity()
    let camera = PerspectiveCamera()
    var isWalking = false

    private var limbs: [Entity] = []
    private var phase: Float = 0
    private var revision = 0

    init() {
        camera.camera.fieldOfViewInDegrees = 38
        camera.camera.near = 0.01
        camera.camera.far = 100
        rotate(yaw: 30, pitch: 21)
    }

    func display(_ skin: MinecraftSkin) async throws {
        revision += 1
        let requestedRevision = revision
        let texture = try await TextureResource(
            image: skin.image, options: .init(semantic: .color, mipmapsMode: .none)
        )
        try Task.checkCancellation()
        guard requestedRevision == revision else { return }

        let sampler = MTLSamplerDescriptor()
        sampler.minFilter = .nearest
        sampler.magFilter = .nearest
        sampler.mipFilter = .notMipmapped
        sampler.sAddressMode = .clampToEdge
        sampler.tAddressMode = .clampToEdge
        let materials: [any Material] = [1.0, 0.82, 0.9, 0.82, 1.0, 0.7].map { brightness in
            var material = UnlitMaterial(applyPostProcessToneMap: false)
            material.color = .init(
                tint: NSColor(white: brightness, alpha: 1),
                texture: .init(texture, sampler: .init(sampler))
            )
            material.blending = .transparent(opacity: 1.0)
            material.opacityThreshold = 0.5
            material.faceCulling = .none
            return material
        }

        let width: Float = skin.variant == .slim ? 3 : 4
        let armX: Float = 4 + width / 2
        let parts: [(SIMD3<Float>, SIMD3<Float>, SIMD3<Float>, SIMD2<Float>, SIMD2<Float>)] = [
            (.init(8, 8, 8), .init(0, 12, 0), .zero, .init(0, 0), .init(32, 0)),
            (.init(8, 12, 4), .init(0, 2, 0), .zero, .init(16, 16), .init(16, 32)),
            (.init(width, 12, 4), .init(-armX, 8, 0), .init(0, -6, 0), .init(40, 16), .init(40, 32)),
            (.init(width, 12, 4), .init(armX, 8, 0), .init(0, -6, 0), .init(32, 48), .init(48, 48)),
            (.init(4, 12, 4), .init(-2, -4, 0), .init(0, -6, 0), .init(0, 16), .init(0, 32)),
            (.init(4, 12, 4), .init(2, -4, 0), .init(0, -6, 0), .init(16, 48), .init(0, 48))
        ]
        var entities: [Entity] = []
        for (index, part) in parts.enumerated() {
            let (size, pivot, offset, baseUV, overlayUV) = part
            let entity = Entity()
            entity.position = pivot / 16
            let body = try Self.cuboid(size: size, uv: baseUV, inflation: 0, materials: materials)
            body.position = offset / 16
            entity.addChild(body)
            let overlay = try Self.cuboid(
                size: size, uv: overlayUV, inflation: index == 0 ? 0.5 : 0.25, materials: materials
            )
            overlay.position = offset / 16
            entity.addChild(overlay)
            entities.append(entity)
        }
        root.children.removeAll()
        for entity in entities { root.addChild(entity) }
        limbs = Array(entities.dropFirst(2))
        pose()
    }

    func rotate(yaw: Float, pitch: Float) {
        root.orientation = simd_quatf(angle: pitch * .pi / 180, axis: .init(1, 0, 0))
            * simd_quatf(angle: yaw * .pi / 180, axis: .init(0, 1, 0))
    }

    func fit(in size: CGSize) {
        guard size.width > 0, size.height > 0 else { return }
        // Сфера покрывает тело, одежду и взмах конечностей при любом ракурсе.
        let vertical = Float(38) * .pi / 360
        let horizontal = atan(tan(vertical) * Float(size.width / size.height))
        camera.position = .init(0, 0, 1.25 / sin(min(vertical, horizontal)))
    }

    func update(deltaTime: TimeInterval) {
        guard isWalking else { return }
        phase = (phase + Float(deltaTime) * 2 * .pi / 1.5).truncatingRemainder(dividingBy: 2 * .pi)
        pose()
    }

    func rest() {
        phase = 0
        pose()
    }

    private func pose() {
        for (index, limb) in limbs.enumerated() {
            let amplitude: Float = index < 2 ? 18 : 20
            let direction: Float = index == 0 || index == 3 ? -1 : 1
            limb.orientation = simd_quatf(
                angle: sin(phase) * amplitude * direction * .pi / 180, axis: .init(1, 0, 0)
            )
        }
    }

    private static func cuboid(
        size: SIMD3<Float>, uv: SIMD2<Float>, inflation: Float, materials: [any Material]
    ) throws -> ModelEntity {
        let half = (size / 2 + SIMD3(repeating: inflation)) / 16
        let x = half.x, y = half.y, z = half.z
        let w = size.x, h = size.y, d = size.z, u = uv.x, v = uv.y
        // Каждая грань: нижний левый → нижний правый → верхний правый → верхний левый.
        let faces: [([SIMD3<Float>], SIMD4<Float>)] = [
            ([.init(-x, -y, z), .init(x, -y, z), .init(x, y, z), .init(-x, y, z)], .init(u + d, v + d, w, h)),
            ([.init(x, -y, -z), .init(-x, -y, -z), .init(-x, y, -z), .init(x, y, -z)], .init(u + 2 * d + w, v + d, w, h)),
            ([.init(-x, -y, -z), .init(-x, -y, z), .init(-x, y, z), .init(-x, y, -z)], .init(u, v + d, d, h)),
            ([.init(x, -y, z), .init(x, -y, -z), .init(x, y, -z), .init(x, y, z)], .init(u + d + w, v + d, d, h)),
            ([.init(-x, y, z), .init(x, y, z), .init(x, y, -z), .init(-x, y, -z)], .init(u + d, v, w, d)),
            ([.init(-x, -y, -z), .init(x, -y, -z), .init(x, -y, z), .init(-x, -y, z)], .init(u + d + w, v, w, d))
        ]
        var positions: [SIMD3<Float>] = []
        var coordinates: [SIMD2<Float>] = []
        var normals: [SIMD3<Float>] = []
        var triangles: [UInt32] = []
        var materialIndices: [UInt32] = []
        for (index, face) in faces.enumerated() {
            let (vertices, rect) = face
            let start = UInt32(positions.count)
            positions += vertices
            let normal = simd_normalize(simd_cross(vertices[1] - vertices[0], vertices[2] - vertices[0]))
            normals += Array(repeating: normal, count: 4)
            // Не захватываем пиксели соседней грани на границе UV-прямоугольника.
            let inset: Float = 0.05
            let left = (rect.x + inset) / 64, right = (rect.x + rect.z - inset) / 64
            let top = 1 - (rect.y + inset) / 64, bottom = 1 - (rect.y + rect.w - inset) / 64
            coordinates += [.init(left, bottom), .init(right, bottom), .init(right, top), .init(left, top)]
            triangles += [start, start + 1, start + 2, start, start + 2, start + 3]
            materialIndices += [UInt32(index), UInt32(index)]
        }
        var descriptor = MeshDescriptor()
        descriptor.positions = .init(positions)
        descriptor.normals = .init(normals)
        descriptor.textureCoordinates = .init(coordinates)
        descriptor.primitives = .triangles(triangles)
        descriptor.materials = .perFace(materialIndices)
        return ModelEntity(mesh: try .generate(from: [descriptor]), materials: materials)
    }
}
