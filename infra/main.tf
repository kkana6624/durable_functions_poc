data "azurerm_resource_group" "existing" {
  count = var.create_resource_group ? 0 : 1
  name  = var.resource_group_name
}

moved {
  from = data.azurerm_resource_group.existing
  to   = data.azurerm_resource_group.existing[0]
}

resource "azurerm_resource_group" "poc" {
  count    = var.create_resource_group ? 1 : 0
  name     = var.resource_group_name
  location = var.location
  tags     = local.tags
}

locals {
  prefix                    = "poc-${var.name_suffix}"
  tags                      = { purpose = "durable-batch-feasibility", scope = "poc" }
  resource_group_name       = var.create_resource_group ? azurerm_resource_group.poc[0].name : data.azurerm_resource_group.existing[0].name
  location                  = var.create_resource_group ? azurerm_resource_group.poc[0].location : data.azurerm_resource_group.existing[0].location
  batch_account_id          = var.batch_mode == "new" ? azurerm_batch_account.poc[0].id : var.batch_account_id
  batch_endpoint            = var.batch_mode == "new" ? "https://${trimprefix(azurerm_batch_account.poc[0].account_endpoint, "https://")}" : var.batch_endpoint
  batch_pool_id             = var.batch_mode == "new" ? azurerm_batch_pool.poc[0].name : var.batch_pool_id
  batch_account_name        = var.batch_mode == "new" ? azurerm_batch_account.poc[0].name : try(split("/", var.batch_account_id)[8], null)
  batch_resource_group_name = var.batch_mode == "new" ? local.resource_group_name : try(split("/", var.batch_account_id)[4], null)
}

resource "azurerm_storage_account" "poc" {
  name                            = "poc${var.name_suffix}"
  resource_group_name             = local.resource_group_name
  location                        = local.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  shared_access_key_enabled       = false
  default_to_oauth_authentication = true
  allow_nested_items_to_be_public = false
  tags                            = local.tags
}

resource "azurerm_storage_container" "results" {
  name                  = "poc-results"
  storage_account_id    = azurerm_storage_account.poc.id
  container_access_type = "private"
}

resource "azurerm_service_plan" "poc" {
  name                = "${local.prefix}-plan"
  resource_group_name = local.resource_group_name
  location            = local.location
  os_type             = "Linux"
  sku_name            = "B1"
  tags                = local.tags
}

resource "azurerm_log_analytics_workspace" "poc" {
  name                = "${local.prefix}-logs"
  resource_group_name = local.resource_group_name
  location            = local.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = local.tags
}

resource "azurerm_application_insights" "poc" {
  name                = "${local.prefix}-insights"
  resource_group_name = local.resource_group_name
  location            = local.location
  workspace_id        = azurerm_log_analytics_workspace.poc.id
  application_type    = "web"
  tags                = local.tags
}

resource "azurerm_linux_function_app" "poc" {
  name                                           = "${local.prefix}-func"
  resource_group_name                            = local.resource_group_name
  location                                       = local.location
  service_plan_id                                = azurerm_service_plan.poc.id
  storage_account_name                           = azurerm_storage_account.poc.name
  storage_uses_managed_identity                  = true
  content_share_force_disabled                   = true
  builtin_logging_enabled                        = false
  functions_extension_version                    = "~4"
  https_only                                     = true
  ftp_publish_basic_authentication_enabled       = false
  webdeploy_publish_basic_authentication_enabled = false
  tags                                           = local.tags

  identity { type = "SystemAssigned" }

  site_config {
    always_on                              = true
    ftps_state                             = "Disabled"
    minimum_tls_version                    = "1.2"
    application_insights_connection_string = azurerm_application_insights.poc.connection_string
    application_stack {
      dotnet_version              = "10.0"
      use_dotnet_isolated_runtime = true
    }
  }

  app_settings = {
    FUNCTIONS_WORKER_RUNTIME        = "dotnet-isolated"
    AzureWebJobsStorage__credential = "managedidentity"
    BATCH_ENDPOINT                  = local.batch_endpoint
    BATCH_POOL_ID                   = local.batch_pool_id
    BLOB_ENDPOINT                   = azurerm_storage_account.poc.primary_blob_endpoint
    BLOB_CONTAINER                  = azurerm_storage_container.results.name
    BATCH_POLL_SECONDS              = tostring(var.batch_poll_seconds)
    WORKFLOW_TIMEOUT_MIN            = tostring(var.workflow_timeout_minutes)
    WEBSITE_RUN_FROM_PACKAGE        = "1"
  }
}

# The host requires Blob Data Owner; Durable additionally uses queues and tables.
# Scope is limited to this newly-created PoC storage account.
resource "azurerm_role_assignment" "storage" {
  for_each = toset([
    "Storage Blob Data Owner",
    "Storage Queue Data Contributor",
    "Storage Table Data Contributor",
  ])
  scope                = azurerm_storage_account.poc.id
  role_definition_name = each.value
  principal_id         = azurerm_linux_function_app.poc.identity[0].principal_id
}

# Job Submitter can manage jobs/tasks but cannot resize/delete shared pools.
# Azure built-in roles cannot enforce a job ID prefix; application guards do so.
resource "azurerm_role_assignment" "batch" {
  scope                = local.batch_account_id
  role_definition_name = "Azure Batch Job Submitter"
  principal_id         = azurerm_linux_function_app.poc.identity[0].principal_id
}
