import Cocoa

class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationWillFinishLaunching(_ notification: Notification) {
        NSAppleEventManager.shared().setEventHandler(
            self,
            andSelector: #selector(handleGetURL(_:withReplyEvent:)),
            forEventClass: AEEventClass(kInternetEventClass),
            andEventID: AEEventID(kAEGetURL)
        )
    }

    @objc func handleGetURL(_ event: NSAppleEventDescriptor, withReplyEvent reply: NSAppleEventDescriptor) {
        guard let urlString = event.paramDescriptor(forKeyword: keyDirectObject)?.stringValue else {
            NSApp.terminate(nil)
            return
        }

        guard urlString.hasPrefix("nxm://stardewvalley/") else {
            NSApp.terminate(nil)
            return
        }

        let home = FileManager.default.homeDirectoryForCurrentUser
        let queueDir = home.appendingPathComponent(".local/share/Moddy/nxm_queue")

        try? FileManager.default.createDirectory(at: queueDir, withIntermediateDirectories: true)

        let fileName = "\(Int(Date().timeIntervalSince1970)).nxmurl"
        let filePath = queueDir.appendingPathComponent(fileName)

        if let data = urlString.data(using: .utf8) {
            FileManager.default.createFile(atPath: filePath.path, contents: data)
        }

        NSApp.terminate(nil)
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Quit after a short delay if no URL event arrives
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) {
            NSApp.terminate(nil)
        }
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
