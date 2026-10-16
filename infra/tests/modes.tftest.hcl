mock_provider "azurerm" {
  mock_resource "azurerm_storage_account" {
    defaults = {
      id                    = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/poc-test/providers/Microsoft.Storage/storageAccounts/poctest001"
      primary_blob_endpoint = "https://poctest001.blob.core.windows.net/"
    }
  }
  mock_resource "azurerm_service_plan" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/poc-test/providers/Microsoft.Web/serverFarms/poc-test-plan" }
  }
  mock_resource "azurerm_log_analytics_workspace" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/poc-test/providers/Microsoft.OperationalInsights/workspaces/poc-test-logs" }
  }
  mock_resource "azurerm_linux_function_app" {
    defaults = {
      default_hostname = "poc-test-func.azurewebsites.net"
      identity         = { principal_id = "11111111-1111-1111-1111-111111111111", tenant_id = "22222222-2222-2222-2222-222222222222" }
    }
  }
  mock_data "azurerm_resource_group" {
    defaults = { name = "shared-rg", location = "japaneast" }
  }
  mock_resource "azurerm_batch_account" {
    defaults = {
      id               = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/poc-test/providers/Microsoft.Batch/batchAccounts/poctestbatch"
      account_endpoint = "poctestbatch.japaneast.batch.azure.com"
    }
  }
}

mock_provider "azurerm" {
  alias = "gallery"
  mock_data "azurerm_shared_image_version" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/image-rg/providers/Microsoft.Compute/galleries/gallery/images/windows/versions/1.0.0"
    }
  }
}

variables {
  subscription_id     = "00000000-0000-0000-0000-000000000000"
  resource_group_name = "shared-rg"
  name_suffix         = "test001"
  batch_account_id    = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/batch-rg/providers/Microsoft.Batch/batchAccounts/sharedbatch"
  batch_endpoint      = "https://sharedbatch.japaneast.batch.azure.com"
  batch_pool_id       = "shared-windows"
}

run "existing_preserves_shared_resources" {
  command = apply
  assert {
    condition     = length(azurerm_batch_account.poc) == 0 && length(azurerm_batch_pool.poc) == 0 && length(azurerm_resource_group.poc) == 0
    error_message = "Existing mode must never manage shared Batch or RG resources."
  }
  assert {
    condition     = output.deployment.batch_account_name == "sharedbatch" && output.deployment.batch_resource_group_name == "batch-rg" && output.deployment.batch_node_count == null
    error_message = "Existing deployment metadata must parse the account ID without lookups."
  }
  assert {
    condition     = azurerm_linux_function_app.poc.app_settings.BATCH_POOL_ID == "shared-windows" && azurerm_role_assignment.batch.scope == var.batch_account_id
    error_message = "Existing app configuration and RBAC must use the supplied account/pool."
  }
  assert {
    condition     = azurerm_linux_function_app.poc.storage_account_name == azurerm_storage_account.poc.name && azurerm_linux_function_app.poc.storage_uses_managed_identity == true && !contains(keys(azurerm_linux_function_app.poc.app_settings), "AzureWebJobsStorage__accountName")
    error_message = "Host storage account name must come only from the provider-managed identity attributes, not app_settings."
  }
}

run "gallery_id_rejected_in_marketplace_mode" {
  command = plan
  variables {
    batch_mode               = "new"
    batch_account_id         = null
    batch_endpoint           = null
    batch_pool_id            = null
    batch_image_source       = "marketplace"
    batch_node_agent_sku_id  = "batch.node.windows amd64"
    marketplace_image        = { publisher = "MicrosoftWindowsServer", offer = "WindowsServer", sku = "2022-datacenter", version = "20348.2527.240607" }
    gallery_image_version_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/image-rg/providers/Microsoft.Compute/galleries/gallery/images/windows/versions/1.0.0"
  }
  expect_failures = [var.gallery_image_version_id]
}

run "gallery_id_rejected_in_existing_mode" {
  command = plan
  variables {
    gallery_image_version_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/image-rg/providers/Microsoft.Compute/galleries/gallery/images/windows/versions/1.0.0"
  }
  expect_failures = [var.gallery_image_version_id]
}

run "new_marketplace_creates_dedicated_resources" {
  command = apply
  variables {
    batch_mode                = "new"
    batch_account_id          = null
    batch_endpoint            = null
    batch_pool_id             = null
    create_resource_group     = true
    resource_group_name       = "poc-test"
    location                  = "japaneast"
    batch_image_source        = "marketplace"
    batch_node_agent_sku_id   = "batch.node.windows amd64"
    marketplace_image         = { publisher = "MicrosoftWindowsServer", offer = "WindowsServer", sku = "2022-datacenter", version = "20348.2527.240607" }
    batch_diagnostics_enabled = true
    batch_node_count          = 2
    batch_max_tasks_per_node  = 3
  }
  assert {
    condition     = length(azurerm_batch_account.poc) == 1 && length(azurerm_batch_pool.poc) == 1 && length(azurerm_resource_group.poc) == 1 && length(data.azurerm_resource_group.existing) == 0
    error_message = "New mode must create isolated Batch resources and the explicitly requested RG."
  }
  assert {
    condition     = azurerm_batch_account.poc[0].pool_allocation_mode == "BatchService" && azurerm_batch_account.poc[0].allowed_authentication_modes == toset(["AAD"])
    error_message = "New Batch accounts must use BatchService and AAD only."
  }
  assert {
    condition     = azurerm_batch_pool.poc[0].target_node_communication_mode == "Simplified" && azurerm_batch_pool.poc[0].fixed_scale[0].target_dedicated_nodes == 2 && azurerm_batch_pool.poc[0].max_tasks_per_node == 3 && length(azurerm_monitor_diagnostic_setting.batch) == 1
    error_message = "Pool sizing, Simplified communication and opt-in diagnostics must be wired."
  }
  assert {
    condition     = azurerm_batch_pool.poc[0].storage_image_reference[0].version == "20348.2527.240607" && length(data.azurerm_shared_image_version.existing) == 0
    error_message = "Marketplace must use the exact supplied version without gallery access."
  }
  assert {
    condition     = output.deployment.batch_endpoint == "https://poctestbatch.japaneast.batch.azure.com" && output.deployment.batch_node_count == 2
    error_message = "New deployment metadata must expose the actual account endpoint and target count."
  }
}

run "new_gallery_uses_existing_version" {
  command = apply
  variables {
    batch_mode               = "new"
    batch_account_id         = null
    batch_endpoint           = null
    batch_pool_id            = null
    gallery_image_version_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/image-rg/providers/Microsoft.Compute/galleries/gallery/images/windows/versions/1.0.0"
    batch_node_agent_sku_id  = "batch.node.windows amd64"
  }
  assert {
    condition     = azurerm_batch_pool.poc[0].storage_image_reference[0].id == data.azurerm_shared_image_version.existing[0].id && data.azurerm_shared_image_version.existing[0].name == "1.0.0"
    error_message = "Gallery mode must read and use the real existing version, not an evidence-only variable."
  }
  assert {
    condition     = length(azurerm_resource_group.poc) == 0 && length(azurerm_monitor_diagnostic_setting.batch) == 0
    error_message = "Existing RG and disabled diagnostics must remain unmanaged."
  }
}

run "missing_existing_account_rejected" {
  command = plan
  variables { batch_account_id = null }
  expect_failures = [var.batch_account_id]
}

run "new_cannot_reuse_shared_account_id" {
  command = plan
  variables {
    batch_mode               = "new"
    batch_endpoint           = null
    batch_pool_id            = null
    batch_node_agent_sku_id  = "batch.node.windows amd64"
    gallery_image_version_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/image-rg/providers/Microsoft.Compute/galleries/gallery/images/windows/versions/1.0.0"
  }
  expect_failures = [var.batch_account_id]
}

run "marketplace_latest_rejected" {
  command = plan
  variables {
    batch_mode              = "new"
    batch_account_id        = null
    batch_endpoint          = null
    batch_pool_id           = null
    batch_image_source      = "marketplace"
    batch_node_agent_sku_id = "batch.node.windows amd64"
    marketplace_image       = { publisher = "MicrosoftWindowsServer", offer = "WindowsServer", sku = "2022-datacenter", version = "latest" }
  }
  expect_failures = [var.marketplace_image]
}

run "zero_nodes_rejected" {
  command = plan
  variables { batch_node_count = 0 }
  expect_failures = [var.batch_node_count]
}
