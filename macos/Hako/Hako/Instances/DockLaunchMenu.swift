import AppKit
import SwiftData

@MainActor struct DockLaunchMenu {
    let store: InstanceStore
    let quickLaunch: QuickLaunchCoordinator

    func makeMenu() -> NSMenu {
        let menu = NSMenu()
        menu.autoenablesItems = false
        menu.addItem(withTitle: String(appLocalized: "Запустить сборку"), action: nil, keyEquivalent: "").isEnabled = false
        do {
            let instances = try store.context.fetch(FetchDescriptor<GameInstance>()).filter { $0.state == .ready }
                .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
            if instances.isEmpty {
                menu.addItem(withTitle: String(appLocalized: "Нет установленных сборок"), action: nil, keyEquivalent: "").isEnabled = false
            }
            for instance in instances {
                let id = instance.id
                let enabled = !store.launchBusy.contains(id) && !store.contentBusy.contains(id) && !quickLaunch.preparingIDs.contains(id)
                let item = ClosureMenuItem(instance.name, systemImage: instance.iconSymbol.isEmpty ? "shippingbox.fill" : instance.iconSymbol, enabled: enabled) {
                    Task { _ = try? await quickLaunch.launch(instanceID: id) }
                }
                item.representedObject = id
                item.toolTip = String(appLocalized: "\(instance.versionID) · \(instance.loaderTitle)")
                if instance.iconSymbol.isEmpty,
                   let url = try? InstanceStorage.containedURL("icon.png", in: store.storage.directory(instance.folderName)),
                   let image = NSImage(contentsOf: url) { item.image = image }
                item.image?.size = NSSize(width: 16, height: 16)
                menu.addItem(item)
            }
        } catch {
            let item = menu.addItem(withTitle: String(appLocalized: "Не удалось загрузить сборки"), action: nil, keyEquivalent: "")
            item.isEnabled = false; item.toolTip = error.localizedDescription
        }
        return menu
    }
}
