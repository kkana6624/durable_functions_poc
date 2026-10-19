resource "azurerm_batch_account" "poc" {
  count                         = var.batch_mode == "new" ? 1 : 0
  name                          = "poc${var.name_suffix}batch"
  resource_group_name           = local.resource_group_name
  location                      = local.location
  pool_allocation_mode          = "BatchService"
  allowed_authentication_modes  = ["AAD"]
  public_network_access_enabled = false
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
  network_configuration {
    subnet_id                        = azurerm_subnet.batch_nodes.id
    public_address_provisioning_type = "NoPublicIPAddresses"
  }
  dynamic "fixed_scale" {
    for_each = var.batch_scale_mode == "fixed" ? [1] : []
    content {
      target_dedicated_nodes    = var.batch_node_type == "dedicated" ? var.batch_node_count : 0
      target_low_priority_nodes = var.batch_node_type == "spot" ? var.batch_node_count : 0
      resize_timeout            = "PT15M"
      node_deallocation_method  = "TaskCompletion"
    }
  }
  dynamic "auto_scale" {
    for_each = var.batch_scale_mode == "autoscale" ? [1] : []
    content {
      evaluation_interval = var.batch_autoscale_evaluation_interval
      formula             = <<-EOT
        $sampleSeconds = 180 * TimeInterval_Second;
        $samples = $PendingTasks.GetSamplePercent($sampleSeconds);
        $tasks = $samples < 70 ? 0 : max(0, max($PendingTasks.GetSample($sampleSeconds)));
        $TargetDedicatedNodes = ${var.batch_node_type == "dedicated" ? "$tasks > 0 ? ${var.batch_node_count} : 0" : "0"};
        $TargetLowPriorityNodes = ${var.batch_node_type == "spot" ? "$tasks > 0 ? ${var.batch_node_count} : 0" : "0"};
        $NodeDeallocationOption = taskcompletion;
      EOT
    }
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

resource "azurerm_private_dns_zone" "batch" {
  count               = var.batch_mode == "new" ? 1 : 0
  name                = "privatelink.batch.azure.com"
  resource_group_name = local.resource_group_name
  tags                = local.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "batch" {
  count                = var.batch_mode == "new" ? 1 : 0
  name                 = "${local.prefix}-batch-dns"
  private_dns_zone_id  = azurerm_private_dns_zone.batch[0].id
  virtual_network_id   = azurerm_virtual_network.validation.id
  registration_enabled = false
  tags                 = local.tags
}

resource "azurerm_private_endpoint" "batch_account" {
  count               = var.batch_mode == "new" ? 1 : 0
  name                = "${local.prefix}-batch-account-pe"
  location            = local.location
  resource_group_name = local.resource_group_name
  subnet_id           = azurerm_subnet.private_endpoints.id
  tags                = local.tags
  private_service_connection {
    name                           = "batchAccount"
    private_connection_resource_id = azurerm_batch_account.poc[0].id
    subresource_names              = ["batchAccount"]
    is_manual_connection           = false
  }
  private_dns_zone_group {
    name                 = "batch"
    private_dns_zone_ids = [azurerm_private_dns_zone.batch[0].id]
  }
}

resource "azurerm_private_endpoint" "batch_node_management" {
  count               = var.batch_mode == "new" ? 1 : 0
  name                = "${local.prefix}-batch-node-pe"
  location            = local.location
  resource_group_name = local.resource_group_name
  subnet_id           = azurerm_subnet.private_endpoints.id
  tags                = local.tags
  private_service_connection {
    name                           = "nodeManagement"
    private_connection_resource_id = azurerm_batch_account.poc[0].id
    subresource_names              = ["nodeManagement"]
    is_manual_connection           = false
  }
  private_dns_zone_group {
    name                 = "batch"
    private_dns_zone_ids = [azurerm_private_dns_zone.batch[0].id]
  }
}

