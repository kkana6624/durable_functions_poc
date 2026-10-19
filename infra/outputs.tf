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
    subscription_id                       = var.subscription_id
    resource_group_name                   = local.resource_group_name
    function_app_name                     = azurerm_linux_function_app.poc.name
    function_app_url                      = "https://${azurerm_linux_function_app.poc.default_hostname}"
    workflow_url                          = "https://${azurerm_linux_function_app.poc.default_hostname}/api/workflows"
    storage_account_name                  = azurerm_storage_account.poc.name
    blob_container_name                   = azurerm_storage_container.results.name
    blob_endpoint                         = azurerm_storage_account.poc.primary_blob_endpoint
    function_principal_id                 = azurerm_linux_function_app.poc.identity[0].principal_id
    service_plan_id                       = local.service_plan_id
    durable_backend                       = var.durable_backend
    task_hub_name                         = var.durable_task_hub_name
    durable_scheduler_id                  = local.use_durable_task_scheduler ? azapi_resource.durable_scheduler[0].id : null
    durable_scheduler_name                = local.use_durable_task_scheduler ? azapi_resource.durable_scheduler[0].name : null
    durable_scheduler_endpoint            = local.use_durable_task_scheduler ? azapi_resource.durable_scheduler[0].output.properties.endpoint : null
    durable_task_hub_id                   = local.use_durable_task_scheduler ? azapi_resource.durable_task_hub[0].id : null
    batch_mode                            = var.batch_mode
    batch_endpoint                        = local.batch_endpoint
    batch_pool_id                         = local.batch_pool_id
    batch_account_id                      = local.batch_account_id
    batch_account_name                    = local.batch_account_name
    batch_resource_group_name             = local.batch_resource_group_name
    batch_scale_mode                      = var.batch_mode == "new" ? var.batch_scale_mode : null
    batch_node_count                      = var.batch_mode == "new" ? (var.batch_scale_mode == "fixed" ? var.batch_node_count : 0) : null
    batch_max_node_count                  = var.batch_mode == "new" ? var.batch_node_count : null
    batch_node_type                       = var.batch_mode == "new" ? var.batch_node_type : null
    batch_dedicated_node_count            = var.batch_mode == "new" && var.batch_scale_mode == "fixed" ? azurerm_batch_pool.poc[0].fixed_scale[0].target_dedicated_nodes : null
    batch_spot_node_count                 = var.batch_mode == "new" && var.batch_scale_mode == "fixed" ? azurerm_batch_pool.poc[0].fixed_scale[0].target_low_priority_nodes : null
    network_mode                          = "private-function-vm"
    validation_vm_name                    = azurerm_linux_virtual_machine.validation.name
    validation_vm_id                      = azurerm_linux_virtual_machine.validation.id
    validation_vm_private_ip              = azurerm_network_interface.validation_vm.private_ip_address
    validation_vm_hostname                = azurerm_linux_virtual_machine.validation.computer_name
    validation_vm_principal_id            = azurerm_linux_virtual_machine.validation.identity[0].principal_id
    vm_control_container_name             = azurerm_storage_container.vm_control.name
    function_private_ip                   = azurerm_private_endpoint.functions.private_service_connection[0].private_ip_address
    batch_private_ip                      = var.batch_mode == "new" ? azurerm_private_endpoint.batch_account[0].private_service_connection[0].private_ip_address : null
    batch_node_management_private_ip      = var.batch_mode == "new" ? azurerm_private_endpoint.batch_node_management[0].private_service_connection[0].private_ip_address : null
    storage_public_network_access_enabled = var.storage_public_network_access_enabled
    storage_blob_private_endpoint_id      = azurerm_private_endpoint.storage_blob.id
    storage_blob_private_ip               = azurerm_private_endpoint.storage_blob.private_service_connection[0].private_ip_address
    storage_queue_private_endpoint_id     = azurerm_private_endpoint.storage_queue.id
    storage_queue_private_ip              = azurerm_private_endpoint.storage_queue.private_service_connection[0].private_ip_address
    storage_table_private_endpoint_id     = azurerm_private_endpoint.storage_table.id
    storage_table_private_ip              = azurerm_private_endpoint.storage_table.private_service_connection[0].private_ip_address
    workflow_auth_mode                    = var.workflow_auth_mode
    workflow_auth_allowed_backend_app_ids = var.workflow_auth_allowed_backend_app_ids
    enable_app_service                    = var.enable_app_service
    app_service_name                      = var.enable_app_service ? azurerm_linux_web_app.app_service[0].name : null
    app_service_url                       = var.enable_app_service ? "https://${azurerm_linux_web_app.app_service[0].default_hostname}" : null
    app_service_private_ip                = var.enable_app_service ? azurerm_private_endpoint.app_service_inbound[0].private_service_connection[0].private_ip_address : null
  }
}

output "storage_blob_private_endpoint_id" {
  value       = azurerm_private_endpoint.storage_blob.id
  description = "The ID of the Storage Blob Private Endpoint."
}

output "storage_blob_private_ip" {
  value       = azurerm_private_endpoint.storage_blob.private_service_connection[0].private_ip_address
  description = "The private IP address of the Storage Blob Private Endpoint."
}

output "storage_queue_private_endpoint_id" {
  value       = azurerm_private_endpoint.storage_queue.id
  description = "The ID of the Storage Queue Private Endpoint."
}

output "storage_queue_private_ip" {
  value       = azurerm_private_endpoint.storage_queue.private_service_connection[0].private_ip_address
  description = "The private IP address of the Storage Queue Private Endpoint."
}

output "storage_table_private_endpoint_id" {
  value       = azurerm_private_endpoint.storage_table.id
  description = "The ID of the Storage Table Private Endpoint."
}

output "storage_table_private_ip" {
  value       = azurerm_private_endpoint.storage_table.private_service_connection[0].private_ip_address
  description = "The private IP address of the Storage Table Private Endpoint."
}

output "workflow_auth_allowed_backend_app_ids" {
  value       = var.workflow_auth_allowed_backend_app_ids
  description = "Allowed backend application client IDs for EntraEasyAuth mode."
}

output "app_service_name" {
  value       = var.enable_app_service ? azurerm_linux_web_app.app_service[0].name : null
  description = "Name of the optional business App Service."
}

output "app_service_url" {
  value       = var.enable_app_service ? "https://${azurerm_linux_web_app.app_service[0].default_hostname}" : null
  description = "URL of the optional business App Service."
}

output "app_service_private_ip" {
  value       = var.enable_app_service ? azurerm_private_endpoint.app_service_inbound[0].private_service_connection[0].private_ip_address : null
  description = "Private IP address of the optional business App Service inbound Private Endpoint."
}

