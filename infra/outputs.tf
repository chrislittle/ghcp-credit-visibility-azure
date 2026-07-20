output "resource_group" {
  value = azurerm_resource_group.rg.name
}

output "web_app_name" {
  value = azurerm_linux_web_app.app.name
}

output "web_app_default_hostname" {
  description = "Private mode: reachable only from the private network. Public mode: internet-reachable (still Entra/Easy-Auth gated)."
  value       = azurerm_linux_web_app.app.default_hostname
}

output "entra_app_client_id" {
  value = one(azuread_application.app[*].client_id)
}

output "key_vault_name" {
  value = azurerm_key_vault.kv.name
}

output "sql_server_fqdn" {
  value = azurerm_mssql_server.sql.fully_qualified_domain_name
}

output "sql_server_name" {
  description = "Short server name (not the FQDN) — needed for az sql server/firewall-rule commands, e.g. the private-networking temporary-public-access escape hatch in deploy.ps1."
  value       = azurerm_mssql_server.sql.name
}

output "sql_database_name" {
  value = azurerm_mssql_database.db.name
}

output "app_principal_id" {
  description = "The identity object ID used for RBAC + SQL. system_assigned: the web app's system MI. user_assigned_selfadmin: the user-assigned identity."
  value       = local.app_principal_id
}

output "web_app_url" {
  description = "Browse here. Public mode: internet-reachable (Entra/Easy-Auth gated). Private mode: private network only."
  value       = "https://${azurerm_linux_web_app.app.default_hostname}"
}

output "acr_login_server" {
  description = "ACR login server (only when create_acr=true) — target for `az acr build`."
  value       = one(azurerm_container_registry.acr[*].login_server)
}

output "jumpbox_vm_name" {
  description = "Only present when enable_jumpbox = true. Connect via Azure Bastion (portal: VM -> Connect -> Bastion, or `az network bastion rdp` with bastion_sku = Standard)."
  value       = one(azurerm_windows_virtual_machine.jumpbox[*].name)
}

output "jumpbox_identity_client_id" {
  description = "Only present when enable_jumpbox = true. Client ID of the jump box's user-assigned identity — needed to disambiguate which identity to use in an IMDS token request (a VM's IMDS endpoint requires client_id/object_id/mi_res_id when more than a system-assigned identity could apply). Used by deploy.ps1's Azure Run Command PAT-set step."
  value       = one(azurerm_user_assigned_identity.jumpbox[*].client_id)
}

output "jumpbox_private_ip" {
  description = "Only present when enable_jumpbox = true. Private IP of the jump-box NIC (informational — Bastion doesn't require it to connect)."
  value       = one(azurerm_network_interface.jumpbox[*].private_ip_address)
}

output "bastion_name" {
  description = "Only present when enable_jumpbox = true."
  value       = one(azurerm_bastion_host.bastion[*].name)
}

output "jumpbox_admin_username" {
  description = "Only present when enable_jumpbox = true."
  value       = one(azurerm_windows_virtual_machine.jumpbox[*].admin_username)
}

output "jumpbox_admin_password" {
  description = "Only present when enable_jumpbox = true. Sensitive — retrieve with `terraform output -raw jumpbox_admin_password`. Auto-generated unless jumpbox_admin_password was set in terraform.tfvars."
  value       = local.create_jumpbox ? local.jumpbox_admin_password : null
  sensitive   = true
}

output "post_deploy_sql_grant" {
  description = "system_assigned: run this against the ghcpvisibility DB (as the Entra SQL admin) to grant the app MI the roles needed to APPLY EF MIGRATIONS on startup (db_ddladmin) plus read/write. user_assigned_selfadmin: not required (the UAMI is the SQL admin; the app applies migrations itself)."
  value       = local.use_uami ? "Not required — the user-assigned identity is the SQL Entra admin, so the app applies its EF Core migrations on startup (Database.Migrate) and builds/updates the schema automatically. No manual grant." : <<-EOT
    -- Connect to the ghcpvisibility DB as the Entra SQL admin, then run this ONCE.
    -- Grants the app's managed identity permission to APPLY EF MIGRATIONS (DDL) + read/write,
    -- so Database.Migrate() can create/update tables (incl. __EFMigrationsHistory) on each deploy.
    CREATE USER [${azurerm_linux_web_app.app.name}] FROM EXTERNAL PROVIDER;
    ALTER ROLE db_datareader ADD MEMBER [${azurerm_linux_web_app.app.name}];
    ALTER ROLE db_datawriter ADD MEMBER [${azurerm_linux_web_app.app.name}];
    ALTER ROLE db_ddladmin   ADD MEMBER [${azurerm_linux_web_app.app.name}];
  EOT
}
