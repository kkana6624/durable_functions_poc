@{
    # Copy to a NEW environment file and invoke deploy.ps1 -Environment dts-poc.
    # Do not repoint the validated PoC: Azure Storage history/entities are not migrated.
    # Microsoft.DurableTask must be registered and the scheduler region must support Consumption.
    # Scheduler data access uses a public endpoint plus Entra identity in this PoC.
    # Function ingress remains private. Existing Batch resources remain reference-only.
    Terraform = @{
        subscription_id = '00000000-0000-0000-0000-000000000000'
        resource_group_name = 'your-dts-resource-group'
        create_resource_group = $true
        location = 'your-azure-region'
        name_suffix = 'replace-with-unique-suffix'
        # Optional read-only cross-RG plan reference when an additional B1 plan is unavailable.
        # Must be Linux in this subscription/region. Existing apps share CPU/memory.
        # existing_service_plan_id = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/existing-plan-rg/providers/Microsoft.Web/serverFarms/existing-linux-plan'
        durable_backend = 'durableTaskScheduler'
        durable_task_hub_name = 'your-task-hub'
        validation_vm_ssh_public_key = 'ssh-ed25519 REPLACE_WITH_YOUR_PUBLIC_KEY dts-validation'
        validation_vm_admin_username = 'azureuser'
        validation_vm_size = 'Standard_B2s'
        # For NVMe-only sizes, explicitly set validation_vm_disk_controller_type='NVMe'.
        validation_operator_principal_id = '00000000-0000-0000-0000-000000000000'
        batch_mode = 'existing'
        batch_account_id = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/existing-batch-rg/providers/Microsoft.Batch/batchAccounts/existing-account'
        batch_endpoint = 'https://your-batch-account.your-region.batch.azure.com'
        batch_pool_id = 'existing-windows-pool'
        batch_poll_seconds = 10
        workflow_timeout_minutes = 15
    }
    Backend = @{
        # Reuse the backend container with a distinct -Environment/state key.
        Create = $false
        resource_group_name = 'your-state-resource-group'
        storage_account_name = 'replacewithuniquestate'
        container_name = 'tfstate'
    }
    TimeoutSeconds = 1800
}
