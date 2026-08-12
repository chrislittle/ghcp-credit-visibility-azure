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
  description = "The identity object ID used for RBAC + SQL. system_assigned: the web app's system MI. user_assigned: the user-assigned identity."
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
  description = "The one-time grant that lets the app's identity APPLY EF MIGRATIONS (db_ddladmin) plus read/write. Required whenever the app's identity is not itself the SQL Entra admin — i.e. always in system_assigned, and in user_assigned whenever a human admin is named via sql_admin_group_name/object_id. deploy.ps1 keys off the leading 'Not required' to decide whether to run step 5."
  value = !local.app_needs_sql_grant ? "Not required — the user-assigned identity is the SQL Entra admin, so the app applies its EF Core migrations on startup (Database.Migrate) and builds/updates the schema automatically. No manual grant." : <<-EOT
    -- Connect to the ghcpvisibility DB as the Entra SQL admin, then run this ONCE.
    -- Grants the app's managed identity permission to APPLY EF MIGRATIONS (DDL) + read/write,
    -- so Database.Migrate() can create/update tables (incl. __EFMigrationsHistory) on each deploy.
    -- NOTE the principal is the IDENTITY that presents the token: the user-assigned identity when
    -- one is in use, otherwise the web app's own system-assigned identity.
    CREATE USER [${local.app_sql_principal_name}] FROM EXTERNAL PROVIDER;
    ALTER ROLE db_datareader ADD MEMBER [${local.app_sql_principal_name}];
    ALTER ROLE db_datawriter ADD MEMBER [${local.app_sql_principal_name}];
    ALTER ROLE db_ddladmin   ADD MEMBER [${local.app_sql_principal_name}];
  EOT
}

output "app_sql_principal_name" {
  description = "Database principal name for the app's managed identity — the UAMI's name when identity_mode=user_assigned, otherwise the web app's. deploy.ps1 grants THIS, not web_app_name, which would create a principal that never authenticates."
  value       = local.app_sql_principal_name
}
