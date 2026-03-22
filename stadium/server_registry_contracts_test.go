package stadium

import (
	"context"
	"testing"
)

func TestBuildNewLocalServerIdentity(t *testing.T) {
	identity, err := BuildNewLocalServerIdentity("Alpha Node")
	if err != nil {
		t.Fatalf("BuildNewLocalServerIdentity returned error: %v", err)
	}
	if identity.LocalServerID == "" {
		t.Fatal("expected local server id")
	}
	if identity.PublicKeyFingerprint == "" {
		t.Fatal("expected public key fingerprint")
	}
	if identity.PublicKeyHex == "" || identity.PrivateKeyHex == "" {
		t.Fatal("expected key material")
	}
	if identity.RegistryStatus != "pending" {
		t.Fatalf("expected pending status, got %q", identity.RegistryStatus)
	}
}

func TestMockGlobalServerRegistrar_NameConflictSuggestions(t *testing.T) {
	registrar := NewMockGlobalServerRegistrar()

	first, err := registrar.RegisterServer(context.Background(), ServerRegistrationRequest{
		LocalServerID:        "srv_a",
		PublicKeyFingerprint: "fp_a",
		PreferredDisplayName: "My Home Server",
	})
	if err != nil {
		t.Fatalf("first registration error: %v", err)
	}
	if first.GlobalServerID == "" {
		t.Fatal("expected global id on first registration")
	}
	if first.NameConflict {
		t.Fatal("did not expect name conflict for first registration")
	}

	second, err := registrar.RegisterServer(context.Background(), ServerRegistrationRequest{
		LocalServerID:        "srv_b",
		PublicKeyFingerprint: "fp_b",
		PreferredDisplayName: "My Home Server",
	})
	if err != nil {
		t.Fatalf("second registration error: %v", err)
	}
	if !second.NameConflict {
		t.Fatal("expected name conflict for duplicate display name")
	}
	if len(second.Suggestions) == 0 {
		t.Fatal("expected suggestions for conflict")
	}
}
