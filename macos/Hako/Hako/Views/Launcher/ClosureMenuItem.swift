import AppKit

/// Пункт `NSMenu` с замыканием. Кнопки «⋯» открывают `NSMenu`, потому что `Menu` в стиле `.glass`
/// рисуется серой капсулой ниже соседних стеклянных кнопок.
final class ClosureMenuItem: NSMenuItem {
    private let handler: () -> Void

    init(_ title: String, systemImage: String? = nil, enabled: Bool = true, handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(run), keyEquivalent: "")
        target = self; isEnabled = enabled
        image = systemImage.flatMap { NSImage(systemSymbolName: $0, accessibilityDescription: nil) }
    }

    required init(coder: NSCoder) { fatalError("init(coder:) не поддерживается") }

    @objc private func run() { handler() }
}
