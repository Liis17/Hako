import SwiftUI

struct JavaMemorySlider: View {
    @Binding var value: Int
    var inherited = false
    private let policy = JavaMemoryPolicy.current

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Максимальная память Java").font(.callout.weight(.medium))
                Spacer()
                Text("\(Double(value) / 1024, specifier: "%.2f") ГиБ").monospacedDigit()
            }
            Slider(value: Binding(get: { Double(value) }, set: { value = policy.normalize(Int($0)) }), in: 512...Double(policy.maximumMiB), step: 256)
                .tint(.sakuraDeep).disabled(inherited).accessibilityLabel("Максимальная память Java")
            Text("Предел Java heap. Общий расход процесса может быть выше. На Mac: \(Double(policy.physicalBytes) / 1073741824, specifier: "%.0f") ГиБ.")
                .font(.caption).foregroundStyle(.secondary)
            if policy.warns(value) {
                Label("Выбрано более 70% памяти. Java может повлиять на работу других приложений и системы.", systemImage: "exclamationmark.triangle")
                    .font(.caption).foregroundStyle(Color.shu).fixedSize(horizontal: false, vertical: true)
            }
        }.onAppear { if !inherited { value = policy.normalize(value) } }
    }
}
