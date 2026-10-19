# AzureRM 5.10 has no DTS resources. AzAPI uses the stable ARM contract.
# DTS is opt-in and must be deployed in a separate environment/state: neither
# orchestration history nor Durable Entity state is migrated from Azure Storage.
locals {
  use_durable_task_scheduler = var.durable_backend == "durableTaskScheduler"
  durable_app_settings = local.use_durable_task_scheduler ? {
    AzureFunctionsJobHost__extensions__durableTask__hubName                               = var.durable_task_hub_name
    AzureFunctionsJobHost__extensions__durableTask__storageProvider__type                 = "azureManaged"
    AzureFunctionsJobHost__extensions__durableTask__storageProvider__connectionStringName = "DURABLE_TASK_SCHEDULER_CONNECTION_STRING"
    DURABLE_TASK_SCHEDULER_CONNECTION_STRING                                              = "Endpoint=${azapi_resource.durable_scheduler[0].output.properties.endpoint};TaskHub=${var.durable_task_hub_name};Authentication=ManagedIdentity"
    } : (var.durable_task_hub_name == "PocBatchHub" ? {} : {
      AzureFunctionsJobHost__extensions__durableTask__hubName = var.durable_task_hub_name
  })
}

resource "azapi_resource" "durable_scheduler" {
  count     = local.use_durable_task_scheduler ? 1 : 0
  type      = "Microsoft.DurableTask/schedulers@2026-02-01"
  name      = "${local.prefix}-dts"
  parent_id = "/subscriptions/${var.subscription_id}/resourceGroups/${local.resource_group_name}"
  location  = local.location
  tags      = local.tags
  body = {
    properties = {
      sku                 = { name = "Consumption" }
      publicNetworkAccess = var.dts_public_network_access_enabled ? "Enabled" : "Disabled"
      ipAllowlist         = var.dts_public_network_access_enabled ? ["0.0.0.0/0"] : []
    }
  }
  response_export_values = ["properties.endpoint"]
  depends_on             = [azurerm_resource_group.poc]
}

resource "azurerm_private_dns_zone" "dts" {
  count               = local.use_durable_task_scheduler ? 1 : 0
  name                = "privatelink.durabletask.io"
  resource_group_name = local.resource_group_name
  tags                = local.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "dts" {
  count                = local.use_durable_task_scheduler ? 1 : 0
  name                 = "${local.prefix}-vnetlink-dts"
  private_dns_zone_id  = azurerm_private_dns_zone.dts[0].id
  virtual_network_id   = azurerm_virtual_network.validation.id
  registration_enabled = false
  tags                 = local.tags
}

resource "azurerm_private_endpoint" "durable_scheduler" {
  count               = local.use_durable_task_scheduler ? 1 : 0
  name                = "${local.prefix}-pe-dts-scheduler"
  location            = local.location
  resource_group_name = local.resource_group_name
  subnet_id           = azurerm_subnet.private_endpoints.id
  tags                = local.tags

  private_service_connection {
    name                           = "${local.prefix}-psc-dts-scheduler"
    private_connection_resource_id = azapi_resource.durable_scheduler[0].id
    subresource_names              = ["scheduler"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "dts-dns-zone-group"
    private_dns_zone_ids = [azurerm_private_dns_zone.dts[0].id]
  }
}

resource "azapi_resource" "durable_task_hub" {
  count     = local.use_durable_task_scheduler ? 1 : 0
  type      = "Microsoft.DurableTask/schedulers/taskHubs@2026-02-01"
  name      = var.durable_task_hub_name
  parent_id = azapi_resource.durable_scheduler[0].id
  body      = { properties = {} }
}

resource "azapi_resource" "durable_retention" {
  count     = local.use_durable_task_scheduler ? 1 : 0
  type      = "Microsoft.DurableTask/schedulers/retentionPolicies@2026-02-01"
  name      = "default"
  parent_id = azapi_resource.durable_scheduler[0].id
  body = {
    properties = {
      retentionPolicies = [{ retentionPeriodInDays = 30 }]
    }
  }
}

resource "azurerm_role_assignment" "durable_task_hub" {
  count                = local.use_durable_task_scheduler ? 1 : 0
  scope                = azapi_resource.durable_task_hub[0].id
  role_definition_name = "Durable Task Data Contributor"
  principal_id         = azurerm_linux_function_app.poc.identity[0].principal_id
}

# Independent observer access for data-plane evidence or dashboard reads. This
# identity is explicitly supplied and is not the Function or VM identity.
resource "azurerm_role_assignment" "durable_observer" {
  count                = local.use_durable_task_scheduler && var.durable_observer_principal_id != null ? 1 : 0
  scope                = azapi_resource.durable_task_hub[0].id
  role_definition_name = "Durable Task Data Reader"
  principal_id         = var.durable_observer_principal_id
}
