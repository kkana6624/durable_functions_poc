mock_provider "azurerm" {
  mock_resource "azurerm_storage_account" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/poc-state-test/providers/Microsoft.Storage/storageAccounts/pocstatetest001" }
  }
}

variables {
  subscription_id       = "00000000-0000-0000-0000-000000000000"
  resource_group_name   = "poc-state-test"
  location              = "japaneast"
  storage_account_name  = "pocstatetest001"
  deployer_principal_id = "11111111-1111-1111-1111-111111111111"
}

run "bootstrap_uses_aad_and_scoped_backend_access" {
  command = apply
  assert {
    condition     = azurerm_storage_account.state.shared_access_key_enabled == false && azurerm_storage_account.state.blob_properties[0].versioning_enabled
    error_message = "State storage must use AAD and blob versioning."
  }
  assert {
    condition     = azurerm_role_assignment.backend.role_definition_name == "Storage Blob Data Contributor" && endswith(azurerm_role_assignment.backend.scope, "/blobServices/default/containers/tfstate")
    error_message = "Backend data access must be scoped to the state container."
  }
  assert {
    condition     = output.backend_config.use_azuread_auth == true && output.backend_config.storage_account_name == "pocstatetest001"
    error_message = "Backend output must provide AAD configuration without keys."
  }
}
