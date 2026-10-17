import SwiftUI

struct InstanceCreationChoiceView: View {
    let onCreated: () -> Void
    let onModrinth: () -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var custom = false

    var body: some View {
        if custom {
            InstanceCreationView(onCreated: onCreated)
        } else {
            VStack(alignment: .leading, spacing: 20) {
                Text("Новая сборка").font(.system(size: 30, weight: .bold))
                Text("Выберите, как создать сборку.").foregroundStyle(.secondary)
                choice("Своя сборка", detail: "Выберите Minecraft и загрузчик, затем добавьте свои моды.", symbol: "slider.horizontal.3") { custom = true }
                choice("Готовая с Modrinth", detail: "Найдите модпак и установите его вместе с модами и настройками.", symbol: "shippingbox.fill", action: onModrinth)
                HStack {
                    Spacer()
                    Button("Отмена") { dismiss() }.buttonStyle(.glass).keyboardShortcut(.cancelAction)
                }
            }
            .padding(28).frame(width: 520)
        }
    }

    private func choice(_ title: LocalizedStringKey, detail: LocalizedStringKey, symbol: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 16) {
                Image(systemName: symbol).font(.title2).foregroundStyle(Color.sakuraDeep).frame(width: 36)
                VStack(alignment: .leading, spacing: 5) {
                    Text(title).font(.headline)
                    Text(detail).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
                Image(systemName: "chevron.right").foregroundStyle(.secondary)
            }
            .padding(18).frame(maxWidth: .infinity, alignment: .leading).contentShape(Rectangle())
        }
        .buttonStyle(.plain).instanceSurface()
    }
}
