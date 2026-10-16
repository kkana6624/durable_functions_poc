@{
    # Copy to poc.psd1 and replace all environment-specific values. No secrets here.
    Terraform = @{
        subscription_id = '00000000-0000-0000-0000-000000000000'
        resource_group_name = 'poc-durable-batch-rg'
        create_resource_group = $true
        location = 'japaneast'
        name_suffix = 'replaceunique001'
        batch_mode = 'new'
        batch_image_source = 'gallery'
        gallery_image_version_id = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/images-rg/providers/Microsoft.Compute/galleries/gallery/images/windows/versions/1.0.0'
        batch_node_agent_sku_id = 'batch.node.windows amd64'
        batch_vm_size = 'Standard_D2s_v5'
        batch_node_count = 1
        batch_max_tasks_per_node = 1
        batch_diagnostics_enabled = $true
        batch_poll_seconds = 10
        workflow_timeout_minutes = 15
        # Marketplace alternative: set batch_image_source='marketplace', omit gallery ID,
        # and set marketplace_image=@{publisher='...';offer='...';sku='...';version='...'}.
        # Pick an exact supported image version using Azure Batch supported-images API.
        # Existing Batch: use existing.example.psd1 instead.
    }
    Backend = @{
        Create = $true
        resource_group_name = 'poc-terraform-state-rg'
        location = 'japaneast'
        storage_account_name = 'pocreplaceuniquestate'
        container_name = 'tfstate'
        # Object ID (not application/client ID) of the authenticated user/service principal.
        deployer_principal_id = '00000000-0000-0000-0000-000000000000'
    }
    TimeoutSeconds = 1800
    # Optional absolute executable paths; otherwise PATH or repo .tools are used.
    # Tools = @{ DotNet='C:/.../dotnet.exe'; Terraform='C:/.../terraform.exe'; AzureCli='C:/.../az.cmd' }
}
