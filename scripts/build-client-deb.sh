#!/bin/bash
set -e

# Build script for creating a .deb package for bbs-client
# Usage: ./scripts/build-client-deb.sh [VERSION]
# Example: ./scripts/build-client-deb.sh 0.1.0

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
CLIENT_ROOT="$REPO_ROOT/bbs-client"

# Determine version
VERSION="${1:-0.1.0}"
VERSION_CLEAN=$(echo "$VERSION" | sed 's/^v//')
DEB_VERSION="$VERSION_CLEAN"

# Check for dotnet CLI
if ! command -v dotnet >/dev/null 2>&1; then
    echo "Error: dotnet CLI not found. Please install .NET 8.0 SDK or later."
    exit 1
fi

echo "Building deb package for bbs-client v$DEB_VERSION..."

# Clean previous builds
echo "Cleaning previous builds..."
rm -rf "$REPO_ROOT/build/deb-client"
mkdir -p "$REPO_ROOT/build/deb-client"

# Create package structure
echo "Creating package structure..."
PKGDIR="$REPO_ROOT/build/deb-client/bbs-client-$DEB_VERSION"
mkdir -p "$PKGDIR"/{DEBIAN,opt/bbs-client,usr/bin,usr/share/applications,usr/share/icons/hicolor/256x256/apps}

# Build and publish client
echo "Building bbs-client application..."
cd "$CLIENT_ROOT"
dotnet publish -c Release -o "$PKGDIR/opt/bbs-client" src/Bbs.Client.App/Bbs.Client.App.csproj \
    --self-contained false

# Make the main DLL executable
chmod +x "$PKGDIR/opt/bbs-client/Bbs.Client.App.dll" || true

# Install launcher script
echo "Installing launcher script..."
cp "$CLIENT_ROOT/debian/bbs-client.launcher" "$PKGDIR/usr/bin/bbs-client"
chmod 0755 "$PKGDIR/usr/bin/bbs-client"

# Install desktop entry
echo "Installing desktop entry..."
cp "$CLIENT_ROOT/debian/bbs-client.desktop" "$PKGDIR/usr/share/applications/"

# Copy documentation
echo "Copying documentation..."
mkdir -p "$PKGDIR/opt/bbs-client/docs"
cp "$REPO_ROOT/README.md" "$PKGDIR/opt/bbs-client/docs/" || true
cp "$REPO_ROOT/CHANGELOG.md" "$PKGDIR/opt/bbs-client/docs/" || true
cp "$CLIENT_ROOT/README.md" "$PKGDIR/opt/bbs-client/docs/CLIENT_README.md" || true

# Create debian control files
echo "Creating DEBIAN control files..."
cp "$CLIENT_ROOT/debian/control" "$PKGDIR/DEBIAN/control"
sed -i "s/^Version: .*/Version: $DEB_VERSION/" "$PKGDIR/DEBIAN/control"

cp "$CLIENT_ROOT/debian/postinst" "$PKGDIR/DEBIAN/postinst"
cp "$CLIENT_ROOT/debian/postrm" "$PKGDIR/DEBIAN/postrm"
cp "$CLIENT_ROOT/debian/copyright" "$PKGDIR/DEBIAN/copyright"

# Make scripts executable
chmod 0755 "$PKGDIR/DEBIAN/postinst"
chmod 0755 "$PKGDIR/DEBIAN/postrm"

# Set permissions
echo "Setting file permissions..."
chmod 0755 "$PKGDIR/opt/bbs-client"
chmod 0755 "$PKGDIR/usr/bin"
chmod 0755 "$PKGDIR/usr/share/applications"

# Create .deb package
echo "Building .deb package..."
dpkg-deb --build "$PKGDIR" "$REPO_ROOT/build/deb-client/bbs-client_${DEB_VERSION}_amd64.deb"

echo ""
echo "✓ Package built successfully!"
echo "  Location: $REPO_ROOT/build/deb-client/bbs-client_${DEB_VERSION}_amd64.deb"
echo ""
echo "To install on a test VM:"
echo "  sudo dpkg -i bbs-client_${DEB_VERSION}_amd64.deb"
echo "  # Install dependencies if needed:"
echo "  sudo apt-get install -f"
echo ""
echo "To run the application:"
echo "  bbs-client"
echo "  # Or find it in your application menu"
echo ""
