resource "azurerm_batch_account" "poc" {
  count                         = var.batch_mode == "new" ? 1 : 0
  name                          = "poc${var.name_suffix}batch"
  resource_group_name           = local.resource_group_name
  location                      = local.location
  pool_allocation_mode          = "BatchService"
  allowed_authentication_modes  = ["AAD"]
  public_network_access_enabled = true
  tags                          = local.tags
}
data "azurerm_shared_image_version" "existing" {
  count               = var.batch_mode == "new" && var.batch_image_source == "gallery" ? 1 : 0
  provider            = azurerm.gallery
  name                = try(split("/", var.gallery_image_version_id)[12], "invalid")
  image_name          = try(split("/", var.gallery_image_version_id)[10], "invalid")
  gallery_name        = try(split("/", var.gallery_image_version_id)[8], "invalid")
  resource_group_name = try(split("/", var.gallery_image_version_id)[4], "invalid")
}
resource "azurerm_batch_pool" "poc" {
  count                          = var.batch_mode == "new" ? 1 : 0
  name                           = "${local.prefix}-pool"
  resource_group_name            = local.resource_group_name
  account_name                   = azurerm_batch_account.poc[0].name
  vm_size                        = var.batch_vm_size
  node_agent_sku_id              = var.batch_node_agent_sku_id
  max_tasks_per_node             = var.batch_max_tasks_per_node
  inter_node_communication       = "Disabled"
  target_node_communication_mode = "Simplified"
  fixed_scale {
    target_dedicated_nodes    = var.batch_node_type == "dedicated" ? var.batch_node_count : 0
    target_low_priority_nodes = var.batch_node_type == "spot" ? var.batch_node_count : 0
    resize_timeout            = "PT15M"
    node_deallocation_method  = "TaskCompletion"
  }
  storage_image_reference {
    id        = var.batch_image_source == "gallery" ? data.azurerm_shared_image_version.existing[0].id : null
    publisher = var.batch_image_source == "marketplace" ? var.marketplace_image.publisher : null
    offer     = var.batch_image_source == "marketplace" ? var.marketplace_image.offer : null
    sku       = var.batch_image_source == "marketplace" ? var.marketplace_image.sku : null
    version   = var.batch_image_source == "marketplace" ? var.marketplace_image.version : null
  }
}
resource "azurerm_monitor_diagnostic_setting" "batch" {
  count                      = var.batch_mode == "new" && var.batch_diagnostics_enabled ? 1 : 0
  name                       = "${local.prefix}-batch-diagnostics"
  target_resource_id         = azurerm_batch_account.poc[0].id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.poc.id
  enabled_log { category_group = "allLogs" }
  enabled_metric { category = "AllMetrics" }
}
