# ── Entra app registration for Easy Auth + App Roles ─────────────
# Gated by var.enable_easy_auth so infra-only deploys work in tenants that
# restrict app-registration / service-principal creation (e.g. some hybrid/managed tenants).
locals {
  app_name          = "app-${local.base}"
  app_default_host  = "${local.app_name}.azurewebsites.net"
  auth_callback_uri = "https://${local.app_default_host}/.auth/login/aad/callback"
  easy_auth         = var.enable_easy_auth ? 1 : 0
}

# Identity of whoever runs `terraform apply` (user or CI service principal).
# Used to set app-registration OWNERSHIP so the app is always deletable/manageable
# by the creator — prevents orphaned, un-removable app registrations.
data "azuread_client_config" "current" {}

resource "azuread_application" "app" {
  count            = local.easy_auth
  display_name     = var.app_display_name
  sign_in_audience = "AzureADMyOrg"

  # ALWAYS assign owners. Without this the app is created ownerless and a
  # non-privileged creator cannot delete it later (403). Includes the deploying
  # principal plus any extra owners (e.g. a platform team) for shared cleanup.
  owners = distinct(compact(concat(
    [data.azuread_client_config.current.object_id],
    var.additional_app_owner_object_ids
  )))

  web {
    redirect_uris = [local.auth_callback_uri]
    implicit_grant {
      id_token_issuance_enabled = true
    }
  }

  app_role {
    allowed_member_types = ["User"]
    description          = "Full visibility across all users and cost centers."
    display_name         = "Admin"
    enabled              = true
    id                   = "1b19509b-32b1-4e9f-b71d-4992aa991967"
    value                = "Admin"
  }
  app_role {
    allowed_member_types = ["User"]
    description          = "Sees only their assigned cost center(s) / direct reports."
    display_name         = "Manager"
    enabled              = true
    id                   = "2c88a9ac-4d1c-4f80-9c2e-6b7a5d9f0abc"
    value                = "Manager"
  }
  app_role {
    allowed_member_types = ["User"]
    description          = "Read-only viewer (scope resolved by strategy)."
    display_name         = "Viewer"
    enabled              = true
    id                   = "3d99bafd-5e2d-4a91-8d3f-7c8b6eab1def"
    value                = "Viewer"
  }

  optional_claims {
    id_token {
      name = "groups"
    }
  }

  group_membership_claims = ["SecurityGroup"]
}

resource "azuread_service_principal" "sp" {
  count     = local.easy_auth
  client_id = azuread_application.app[0].client_id

  # Same ownership rationale as the application — keep it manageable/deletable.
  owners = distinct(compact(concat(
    [data.azuread_client_config.current.object_id],
    var.additional_app_owner_object_ids
  )))
}

resource "azuread_application_password" "secret" {
  count          = local.easy_auth
  application_id = azuread_application.app[0].id
  display_name   = "easy-auth"
  end_date       = timeadd(timestamp(), "4320h") # 180 days; rotate before expiry

  # end_date is derived from timestamp(), which advances every plan. Pin it to the
  # value computed at creation so Terraform doesn't try to rotate the secret on every apply.
  lifecycle {
    ignore_changes = [end_date]
  }
}

# Optional: grant a USER or GROUP the Admin app role at deploy time (e.g. yourself or an admins group).
resource "azuread_app_role_assignment" "admin_principal" {
  count               = var.enable_easy_auth && var.admin_principal_object_id != "" ? 1 : 0
  app_role_id         = "1b19509b-32b1-4e9f-b71d-4992aa991967"
  principal_object_id = var.admin_principal_object_id
  resource_object_id  = azuread_service_principal.sp[0].object_id
}
