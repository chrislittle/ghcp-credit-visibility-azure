# ── Optional Azure Container Registry (test convenience) ──────────
# Created only when create_acr = true. Lets you build the image in-cloud with
# `az acr build` (no local Docker) and pull it via the app's managed identity.
# Customer/prod: leave create_acr = false and point container_image at the
# customer's own registry (they already have one).
resource "azurerm_container_registry" "acr" {
  count               = var.create_acr ? 1 : 0
  name                = "acr${local.base_st}"
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location
  sku                 = "Basic"
  admin_enabled       = false
  tags                = var.tags
}

# Let the app identity pull images from the ACR.
resource "azurerm_role_assignment" "app_acr_pull" {
  count                = var.create_acr ? 1 : 0
  scope                = azurerm_container_registry.acr[0].id
  role_definition_name = "AcrPull"
  principal_id         = local.app_principal_id
}
