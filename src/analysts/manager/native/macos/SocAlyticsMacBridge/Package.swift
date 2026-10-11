// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "SocAlyticsMacBridge",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "SocAlyticsMacBridge", type: .dynamic, targets: ["SocAlyticsMacBridge"])
    ],
    targets: [
        .target(name: "SocAlyticsMacBridge")
    ]
)
