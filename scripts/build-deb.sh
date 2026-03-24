#!/bin/bash
set -e

# Build script for creating a .deb package for bbs-server
# Usage: ./scripts/build-deb.sh [VERSION]
# Example: ./scripts/build-deb.sh 0.3.0

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Determine version
VERSION="${1:-0.3.0}"
VERSION_CLEAN=$(echo "$VERSION" | sed 's/^v//')
DEB_VERSION="$VERSION_CLEAN"

echo "Building deb package for bbs-server v$DEB_VERSION..."

# Clean previous builds
echo "Cleaning previous builds..."
rm -rf "$REPO_ROOT/build/deb"
mkdir -p "$REPO_ROOT/build/deb"

# Create package structure
echo "Creating package structure..."
PKGDIR="$REPO_ROOT/build/deb/bbs-server-$DEB_VERSION"
mkdir -p "$PKGDIR"/{DEBIAN,opt/bbs/bin,etc/bbs,var/lib/bbs/plugins/games,usr/lib/systemd/system}

# Build server binary
echo "Building bbs-server binary..."
cd "$REPO_ROOT"
go build -ldflags "-X main.buildVersion=v$DEB_VERSION" -o "$PKGDIR/opt/bbs/bin/bbs-server" ./cmd/bbs-server

# Copy templates
echo "Copying templates..."
mkdir -p "$PKGDIR/opt/bbs/templates"
cp -r "$REPO_ROOT/cmd/bbs-server/templates"/* "$PKGDIR/opt/bbs/templates/"

# Copy runtime plugin assets and optional counter example binary
echo "Copying plugin assets..."
if compgen -G "$REPO_ROOT/cmd/bbs-server/plugins/games/*" > /dev/null; then
	cp -r "$REPO_ROOT/cmd/bbs-server/plugins/games/"* "$PKGDIR/var/lib/bbs/plugins/games/"
else
	echo "Warning: no plugin assets found under cmd/bbs-server/plugins/games"
fi

echo "Building optional counter plugin example..."
go build -o "$PKGDIR/var/lib/bbs/plugins/games/counter-plugin" ./cmd/bbs-game-counter-plugin

# Copy documentation
echo "Copying documentation..."
mkdir -p "$PKGDIR/opt/bbs/docs"
cp -r "$REPO_ROOT/docs" "$PKGDIR/opt/bbs/docs/README"
cp "$REPO_ROOT/README.md" "$PKGDIR/opt/bbs/docs/README.md"
cp "$REPO_ROOT/CHANGELOG.md" "$PKGDIR/opt/bbs/CHANGELOG.md"

# Copy systemd service
echo "Installing systemd service..."
cp "$REPO_ROOT/debian/bbs-server.service" "$PKGDIR/usr/lib/systemd/system/"

# Create debian control files
echo "Creating DEBIAN control files..."
cp "$REPO_ROOT/debian/control" "$PKGDIR/DEBIAN/control"
sed -i "s/^Version: .*/Version: $DEB_VERSION/" "$PKGDIR/DEBIAN/control"

cp "$REPO_ROOT/debian/preinst" "$PKGDIR/DEBIAN/preinst"
cp "$REPO_ROOT/debian/postinst" "$PKGDIR/DEBIAN/postinst"
cp "$REPO_ROOT/debian/postrm" "$PKGDIR/DEBIAN/postrm"
cp "$REPO_ROOT/debian/copyright" "$PKGDIR/DEBIAN/copyright"

# Make scripts executable
chmod 0755 "$PKGDIR/DEBIAN/preinst"
chmod 0755 "$PKGDIR/DEBIAN/postinst"
chmod 0755 "$PKGDIR/DEBIAN/postrm"
chmod 0755 "$PKGDIR/opt/bbs/bin/bbs-server"

# Set permissions
echo "Setting file permissions..."
chmod 0755 "$PKGDIR/opt/bbs/bin"
chmod 0755 "$PKGDIR/opt/bbs/templates"
chmod 0755 "$PKGDIR/var/lib/bbs"
chmod 0755 "$PKGDIR/var/lib/bbs/plugins/games"
chmod 0755 "$PKGDIR/etc/bbs"

# Create .deb package
echo "Building .deb package..."
dpkg-deb --build "$PKGDIR" "$REPO_ROOT/build/deb/bbs-server_${DEB_VERSION}_amd64.deb"

echo ""
echo "✓ Package built successfully!"
echo "  Location: $REPO_ROOT/build/deb/bbs-server_${DEB_VERSION}_amd64.deb"
echo ""
echo "To install on a test VM:"
echo "  sudo dpkg -i bbs-server_${DEB_VERSION}_amd64.deb"
echo ""
echo "To start the service:"
echo "  sudo systemctl start bbs-server"
echo ""
echo "To view logs:"
echo "  sudo journalctl -u bbs-server -f"
