output "function_app_name" {
  value = azurerm_linux_function_app.poc.name
}

output "workflow_url" {
  value = "https://${azurerm_linux_function_app.poc.default_hostname}/api/workflows"
}

output "storage_account_name" {
  value = azurerm_storage_account.poc.name
}

output "blob_endpoint" {
  value = azurerm_storage_account.poc.primary_blob_endpoint
}

output "blob_container" {
  value = azurerm_storage_container.results.name
}

output "function_principal_id" {
  value = azurerm_linux_function_app.poc.identity[0].principal_id
}

output "existing_references" {
  value = {
    batch_account_id         = var.batch_account_id
    batch_pool_id            = var.batch_pool_id
    gallery_image_version_id = var.gallery_image_version_id
  }
}

output "deployment" {
  description = "Non-secret values consumed by deployment/readiness scripts. Existing pool node count is unknown."
  value = {
    subscription_id            = var.subscription_id
    resource_group_name        = local.resource_group_name
    function_app_name          = azurerm_linux_function_app.poc.name
    function_app_url           = "https://${azurerm_linux_function_app.poc.default_hostname}"
    workflow_url               = "https://${azurerm_linux_function_app.poc.default_hostname}/api/workflows"
    storage_account_name       = azurerm_storage_account.poc.name
    blob_container_name        = azurerm_storage_container.results.name
    blob_endpoint              = azurerm_storage_account.poc.primary_blob_endpoint
    function_principal_id      = azurerm_linux_function_app.poc.identity[0].principal_id
    batch_mode                 = var.batch_mode
    batch_endpoint             = local.batch_endpoint
    batch_pool_id              = local.batch_pool_id
    batch_account_id           = local.batch_account_id
    batch_account_name         = local.batch_account_name
    batch_resource_group_name  = local.batch_resource_group_name
    batch_node_count           = var.batch_mode == "new" ? var.batch_node_count : null
    batch_node_type            = var.batch_mode == "new" ? var.batch_node_type : null
    batch_dedicated_node_count = var.batch_mode == "new" ? azurerm_batch_pool.poc[0].fixed_scale[0].target_dedicated_nodes : null
    batch_spot_node_count      = var.batch_mode == "new" ? azurerm_batch_pool.poc[0].fixed_scale[0].target_low_priority_nodes : null
    network_mode               = "private-function-vm"
    validation_vm_name         = azurerm_linux_virtual_machine.validation.name
    validation_vm_id           = azurerm_linux_virtual_machine.validation.id
    validation_vm_private_ip   = azurerm_network_interface.validation_vm.private_ip_address
    validation_vm_hostname     = azurerm_linux_virtual_machine.validation.computer_name
    validation_vm_principal_id = azurerm_linux_virtual_machine.validation.identity[0].principal_id
    vm_control_container_name  = azurerm_storage_container.vm_control.name
    function_private_ip        = azurerm_private_endpoint.functions.private_service_connection[0].private_ip_address
  }
}
