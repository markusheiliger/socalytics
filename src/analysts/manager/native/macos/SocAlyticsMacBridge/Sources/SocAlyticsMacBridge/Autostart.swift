import ServiceManagement

enum Autostart {
    static func register() -> Int32 {
        do {
            try SMAppService.mainApp.register()
            return BridgeStatus.ok
        } catch {
            return BridgeStatus.failed
        }
    }

    static func unregister() -> Int32 {
        do {
            try SMAppService.mainApp.unregister()
            return BridgeStatus.ok
        } catch {
            return BridgeStatus.failed
        }
    }

    static func status() -> Int32 {
        switch SMAppService.mainApp.status {
        case .notRegistered: return 0
        case .enabled: return 1
        case .requiresApproval: return 2
        case .notFound: return 3
        @unknown default: return BridgeStatus.failed
        }
    }
}
