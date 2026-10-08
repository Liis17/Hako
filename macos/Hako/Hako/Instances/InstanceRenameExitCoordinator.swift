import AppKit
import Observation
import SwiftUI

struct PendingInstanceRename: Equatable {
    let instanceID: UUID
    let name: String
}

enum InstanceRenameExitRequest: Equatable {
    case closeWindow
    case terminateApplication
}

@MainActor @Observable final class InstanceRenameExitCoordinator {
    var pendingRename: PendingInstanceRename?
    var exitRequest: InstanceRenameExitRequest?
    @ObservationIgnored weak var window: NSWindow?

    private var allowNextWindowClose = false

    func requestExit(_ request: InstanceRenameExitRequest) {
        guard pendingRename != nil else { return }
        exitRequest = request
    }

    func resumeExit() {
        guard let exitRequest else { return }
        self.exitRequest = nil
        switch exitRequest {
        case .closeWindow:
            allowNextWindowClose = true
            window?.performClose(nil)
        case .terminateApplication:
            NSApp.terminate(nil)
        }
    }

    func cancelExit() {
        exitRequest = nil
    }

    func consumeWindowCloseApproval() -> Bool {
        guard allowNextWindowClose else { return false }
        allowNextWindowClose = false
        return true
    }
}

@MainActor final class HakoApplicationDelegate: NSObject, NSApplicationDelegate {
    weak var renameExit: InstanceRenameExitCoordinator?

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let renameExit, renameExit.pendingRename != nil else { return .terminateNow }
        renameExit.requestExit(.terminateApplication)
        return .terminateCancel
    }
}

struct InstanceRenameWindowCloseGuard: NSViewRepresentable {
    let coordinator: InstanceRenameExitCoordinator

    func makeNSView(context: Context) -> RenameWindowGuardView {
        RenameWindowGuardView(coordinator: coordinator)
    }

    func updateNSView(_ view: RenameWindowGuardView, context: Context) {
        view.coordinator = coordinator
        view.attachToWindow()
    }

    static func dismantleNSView(_ view: RenameWindowGuardView, coordinator: ()) {
        view.detachFromWindow()
    }
}

@MainActor final class RenameWindowGuardView: NSView {
    var coordinator: InstanceRenameExitCoordinator
    private weak var attachedWindow: NSWindow?
    private var delegateProxy: WindowCloseDelegateProxy?

    init(coordinator: InstanceRenameExitCoordinator) {
        self.coordinator = coordinator
        super.init(frame: .zero)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var intrinsicContentSize: NSSize { .zero }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        attachToWindow()
    }

    func attachToWindow() {
        guard let window else { return }
        if attachedWindow === window {
            coordinator.window = window
            if let delegateProxy, window.delegate !== delegateProxy {
                delegateProxy.original = window.delegate
                window.delegate = delegateProxy
            }
            return
        }
        detachFromWindow()
        attachedWindow = window
        coordinator.window = window
        let proxy = WindowCloseDelegateProxy(original: window.delegate, coordinator: coordinator)
        delegateProxy = proxy
        window.delegate = proxy
    }

    func detachFromWindow() {
        guard let attachedWindow else { return }
        if let delegateProxy, attachedWindow.delegate === delegateProxy {
            attachedWindow.delegate = delegateProxy.original
        }
        if coordinator.window === attachedWindow { coordinator.window = nil }
        self.attachedWindow = nil
        delegateProxy = nil
    }
}

@MainActor private final class WindowCloseDelegateProxy: NSObject, NSWindowDelegate {
    weak var original: (any NSWindowDelegate)?
    private let coordinator: InstanceRenameExitCoordinator

    init(original: (any NSWindowDelegate)?, coordinator: InstanceRenameExitCoordinator) {
        self.original = original
        self.coordinator = coordinator
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if let shouldClose = original?.windowShouldClose?(sender), !shouldClose { return false }
        if coordinator.consumeWindowCloseApproval() { return true }
        guard coordinator.pendingRename != nil else { return true }
        coordinator.requestExit(.closeWindow)
        return false
    }

    override func responds(to aSelector: Selector!) -> Bool {
        super.responds(to: aSelector) || (original?.responds(to: aSelector) ?? false)
    }

    override func forwardingTarget(for aSelector: Selector!) -> Any? {
        guard let original, original.responds(to: aSelector) else { return super.forwardingTarget(for: aSelector) }
        return original
    }
}
