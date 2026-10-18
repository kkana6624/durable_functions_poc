@{
    # Copy to poc.psd1 and replace all environment-specific values. No secrets here.
    Terraform = @{
        subscription_id = '00000000-0000-0000-0000-000000000000'
        resource_group_name = 'your-poc-resource-group'
        create_resource_group = $true
        location = 'your-azure-region'
        name_suffix = 'replaceunique001'
        # Existing orchestration/entity state is not migrated when changing backend.
        # Use durable-task-scheduler.example.psd1 for a separate DTS environment.
        durable_backend = 'azureStorage'
        durable_task_hub_name = 'PocBatchHub'
        # Replace with your SSH PUBLIC key. No public IP/SSH ingress is enabled; use VM Run Command.
        validation_vm_ssh_public_key = 'ssh-ed25519 REPLACE_WITH_YOUR_PUBLIC_KEY poc-validation'
        validation_vm_admin_username = 'azureuser'
        validation_vm_size = 'Standard_B2s'
        # Optional operator Object ID (may equal Backend.deployer_principal_id).
        # Grants Blob Data Contributor on poc-vm-control only; replace this placeholder.
        validation_operator_principal_id = '00000000-0000-0000-0000-000000000000'
        batch_mode = 'new'
        batch_image_source = 'gallery'
        gallery_image_version_id = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/images-rg/providers/Microsoft.Compute/galleries/gallery/images/windows/versions/1.0.0'
        batch_node_agent_sku_id = 'batch.node.windows amd64'
        batch_vm_size = 'Standard_D2s_v5'
        # PoC uses Spot compute nodes; validation VM remains a regular VM.
        # Omit this setting or use 'dedicated' for the backward-compatible default.
        batch_node_type = 'spot'
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
        resource_group_name = 'your-state-resource-group'
        location = 'your-azure-region'
        storage_account_name = 'replacewithuniquestate'
        container_name = 'tfstate'
        # Object ID (not application/client ID) of the authenticated user/service principal.
        deployer_principal_id = '00000000-0000-0000-0000-000000000000'
    }
    TimeoutSeconds = 1800
    # Optional absolute executable paths; otherwise PATH or repo .tools are used.
    # Tools = @{ DotNet='C:/.../dotnet.exe'; Terraform='C:/.../terraform.exe'; AzureCli='C:/.../az.cmd' }
}
