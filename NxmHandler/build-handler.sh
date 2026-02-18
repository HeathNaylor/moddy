#!/bin/bash
# Builds the ModdyNxmHandler.app from the Swift source.
# Run this once to create the .app bundle that gets distributed with the mod.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
APP_PATH="$SCRIPT_DIR/ModdyNxmHandler.app"

# Remove old build
rm -rf "$APP_PATH"

# Create .app bundle structure
mkdir -p "$APP_PATH/Contents/MacOS"

# Compile Swift into the binary
swiftc -o "$APP_PATH/Contents/MacOS/nxm-handler" \
    -framework Cocoa \
    "$SCRIPT_DIR/nxm-handler.swift"

# Write Info.plist
cat > "$APP_PATH/Contents/Info.plist" << 'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>ModdyNxmHandler</string>
    <key>CFBundleIdentifier</key>
    <string>com.khgames.moddynxmhandler</string>
    <key>CFBundleVersion</key>
    <string>1.0.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>nxm-handler</string>
    <key>LSBackgroundOnly</key>
    <true/>
    <key>LSMinimumSystemVersion</key>
    <string>10.13</string>
    <key>CFBundleURLTypes</key>
    <array>
        <dict>
            <key>CFBundleURLName</key>
            <string>Nexus Mod Manager URL</string>
            <key>CFBundleURLSchemes</key>
            <array>
                <string>nxm</string>
            </array>
        </dict>
    </array>
</dict>
</plist>
PLIST

echo "Built $APP_PATH"
