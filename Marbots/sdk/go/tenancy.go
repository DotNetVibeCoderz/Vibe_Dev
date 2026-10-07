package marbots

import (
	"context"
	"net/http"
)

// TenantRole is a role inside a tenant, weakest first: Viewer, Operator, Admin, Owner.
type TenantRole string

const (
	RoleViewer   TenantRole = "Viewer"
	RoleOperator TenantRole = "Operator"
	RoleAdmin    TenantRole = "Admin"
	RoleOwner    TenantRole = "Owner"
)

// Tenant is a tenant in multi-tenant mode. "default" always exists.
type Tenant struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	Disabled  bool   `json:"disabled"`
	CreatedAt string `json:"createdAt"`
}

// APIKeyInfo is a tenant API key as listed; the key itself is only returned when created.
type APIKeyInfo struct {
	ID         string     `json:"id"`
	Tenant     string     `json:"tenant"`
	Name       string     `json:"name"`
	Role       TenantRole `json:"role"`
	Prefix     string     `json:"prefix"`
	CreatedAt  string     `json:"createdAt"`
	LastUsedAt string     `json:"lastUsedAt,omitempty"`
}

// NewAPIKey is a freshly created key; Key is shown only this once.
type NewAPIKey struct {
	ID     string     `json:"id"`
	Key    string     `json:"key"`
	Tenant string     `json:"tenant"`
	Role   TenantRole `json:"role"`
}

// TenantMember is an OIDC user's role in a tenant (matched by e-mail or subject).
type TenantMember struct {
	Tenant  string     `json:"tenant"`
	Subject string     `json:"subject"`
	Role    TenantRole `json:"role"`
}

// WhoAmI describes the caller.
type WhoAmI struct {
	Tenant        string     `json:"tenant"`
	Role          TenantRole `json:"role"`
	User          string     `json:"user,omitempty"`
	PlatformAdmin bool       `json:"platformAdmin"`
	MultiTenant   bool       `json:"multiTenant"`
	Tenants       []string   `json:"tenants"`
}

// TenancyAPI covers who-am-I, tenants (platform admins) and this tenant's keys and members (owners).
type TenancyAPI struct{ c *Client }

func (t *TenancyAPI) WhoAmI(ctx context.Context) (*WhoAmI, error) {
	var out WhoAmI
	return &out, t.c.do(ctx, http.MethodGet, "/api/v1/whoami", nil, &out)
}

func (t *TenancyAPI) ListTenants(ctx context.Context) ([]Tenant, error) {
	var out []Tenant
	return out, t.c.do(ctx, http.MethodGet, "/api/v1/tenants", nil, &out)
}

func (t *TenancyAPI) CreateTenant(ctx context.Context, id, name string) (*Tenant, error) {
	var out Tenant
	return &out, t.c.do(ctx, http.MethodPost, "/api/v1/tenants", map[string]any{"id": id, "name": name}, &out)
}

func (t *TenancyAPI) DisableTenant(ctx context.Context, id string) (*Tenant, error) {
	var out Tenant
	return &out, t.c.do(ctx, http.MethodPost, "/api/v1/tenants/"+esc(id)+"/disable", nil, &out)
}

func (t *TenancyAPI) EnableTenant(ctx context.Context, id string) (*Tenant, error) {
	var out Tenant
	return &out, t.c.do(ctx, http.MethodPost, "/api/v1/tenants/"+esc(id)+"/enable", nil, &out)
}

// CreateTenantKey creates a key in any tenant (platform admins). The plaintext key is returned once.
func (t *TenancyAPI) CreateTenantKey(ctx context.Context, tenant, name string, role TenantRole) (*NewAPIKey, error) {
	var out NewAPIKey
	return &out, t.c.do(ctx, http.MethodPost, "/api/v1/tenants/"+esc(tenant)+"/keys", map[string]any{"name": name, "role": role}, &out)
}

func (t *TenancyAPI) ListKeys(ctx context.Context) ([]APIKeyInfo, error) {
	var out []APIKeyInfo
	return out, t.c.do(ctx, http.MethodGet, "/api/v1/tenant/keys", nil, &out)
}

func (t *TenancyAPI) CreateKey(ctx context.Context, name string, role TenantRole) (*NewAPIKey, error) {
	var out NewAPIKey
	return &out, t.c.do(ctx, http.MethodPost, "/api/v1/tenant/keys", map[string]any{"name": name, "role": role}, &out)
}

func (t *TenancyAPI) RevokeKey(ctx context.Context, id string) error {
	return t.c.do(ctx, http.MethodDelete, "/api/v1/tenant/keys/"+esc(id), nil, nil)
}

func (t *TenancyAPI) ListMembers(ctx context.Context) ([]TenantMember, error) {
	var out []TenantMember
	return out, t.c.do(ctx, http.MethodGet, "/api/v1/tenant/members", nil, &out)
}

func (t *TenancyAPI) SetMember(ctx context.Context, subject string, role TenantRole) (*TenantMember, error) {
	var out TenantMember
	return &out, t.c.do(ctx, http.MethodPut, "/api/v1/tenant/members", map[string]any{"subject": subject, "role": role}, &out)
}

func (t *TenancyAPI) RemoveMember(ctx context.Context, subject string) error {
	return t.c.do(ctx, http.MethodDelete, "/api/v1/tenant/members/"+esc(subject), nil, nil)
}
