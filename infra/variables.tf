variable "subscription_id" {
  type = string
}
variable "existing_service_plan_id" {
  type        = string
  default     = null
  description = "Optional existing Linux App Service plan in the same subscription and region. Read-only reference; compute is shared with its existing apps."
  validation {
    condition = var.existing_service_plan_id == null ? true : (
      can(regex("(?i)^/subscriptions/[0-9a-f-]{36}/resourceGroups/[^/]+/providers/Microsoft.Web/serverfarms/[^/]+$", var.existing_service_plan_id)) &&
      try(lower(split("/", var.existing_service_plan_id)[2]) == lower(var.subscription_id), false)
    )
    error_message = "Use a full Microsoft.Web/serverfarms resource ID in the deployment subscription, or null to create a plan."
  }
}
variable "durable_backend" {
  type        = string
  default     = "azureStorage"
  description = "Durable persistence backend. Select DTS only in a separate environment; existing orchestration and entity state is not migrated."
  validation {
    condition     = contains(["azureStorage", "durableTaskScheduler"], var.durable_backend)
    error_message = "durable_backend must be azureStorage or durableTaskScheduler."
  }
}
variable "durable_observer_principal_id" {
  type        = string
  default     = null
  description = "Optional explicit Entra object ID granted read-only DTS data access on the selected task hub. Never inferred from deployment or VM operator identities."
  validation {
    condition = var.durable_observer_principal_id == null ? true : (
      var.durable_backend == "durableTaskScheduler" &&
      can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", var.durable_observer_principal_id))
    )
    error_message = "durable_observer_principal_id requires the DTS backend and an explicit Entra object ID in GUID format, or null to grant no observer access."
  }
}
variable "durable_task_hub_name" {
  type        = string
  default     = "PocBatchHub"
  description = "Task hub name shared by Functions configuration and the optional DTS resource."
  validation {
    condition     = can(regex("^[A-Za-z][A-Za-z0-9]{2,44}$", var.durable_task_hub_name))
    error_message = "Task hub name must be 3-45 alphanumeric characters beginning with a letter."
  }
}
variable "validation_operator_principal_id" {
  type        = string
  default     = null
  description = "Optional explicitly authorized operator object ID granted Blob Contributor only on the VM control container. Never inferred from backend identity."
  validation {
    condition     = var.validation_operator_principal_id == null ? true : can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", var.validation_operator_principal_id))
    error_message = "Use an Entra principal object ID in GUID format, or null for pre-provisioned access."
  }
}
variable "validation_vm_ssh_public_key" {
  type        = string
  description = "SSH public key only; private keys must never enter Terraform state. VM ingress remains denied."
  validation {
    condition     = can(regex("^ssh-(rsa|ed25519) [A-Za-z0-9+/=]+( .*)?$", trimspace(var.validation_vm_ssh_public_key)))
    error_message = "Supply an OpenSSH RSA or Ed25519 public key, never a private key."
  }
}
variable "validation_vm_admin_username" {
  type    = string
  default = "azureuser"
  validation {
    condition     = can(regex("^[a-z][a-z0-9_]{0,30}$", var.validation_vm_admin_username)) && !contains(["root", "admin", "administrator"], var.validation_vm_admin_username)
    error_message = "Use a non-reserved lowercase Linux username."
  }
}
variable "validation_vm_size" {
  type    = string
  default = "Standard_B2s"
}
variable "validation_vm_disk_controller_type" {
  type        = string
  default     = null
  description = "Optional OS disk controller type. Leave null for Azure defaults; NVMe-only VM sizes require NVMe."
  validation {
    condition     = var.validation_vm_disk_controller_type == null ? true : contains(["SCSI", "NVMe"], var.validation_vm_disk_controller_type)
    error_message = "Disk controller type must be null, SCSI, or NVMe."
  }
}
variable "validation_vnet_address_space" {
  type    = string
  default = "10.80.0.0/16"
  validation {
    condition     = can(cidrnetmask(var.validation_vnet_address_space))
    error_message = "Use an IPv4 VNet CIDR."
  }
}
variable "validation_vm_subnet_prefix" {
  type    = string
  default = "10.80.1.0/24"
  validation {
    condition     = can(cidrnetmask(var.validation_vm_subnet_prefix))
    error_message = "Use an IPv4 subnet CIDR within the validation VNet."
  }
}
variable "validation_private_endpoint_subnet_prefix" {
  type    = string
  default = "10.80.2.0/24"
  validation {
    condition     = can(cidrnetmask(var.validation_private_endpoint_subnet_prefix))
    error_message = "Use a separate IPv4 Private Endpoint subnet CIDR within the validation VNet."
  }
}
variable "functions_outbound_subnet_prefix" {
  type    = string
  default = "10.80.3.0/24"
  validation {
    condition     = can(cidrnetmask(var.functions_outbound_subnet_prefix))
    error_message = "Use a separate IPv4 Functions outbound subnet CIDR within the validation VNet."
  }
}
variable "batch_nodes_subnet_prefix" {
  type    = string
  default = "10.80.4.0/24"
  validation {
    condition     = can(cidrnetmask(var.batch_nodes_subnet_prefix))
    error_message = "Use a separate IPv4 Batch nodes subnet CIDR within the validation VNet."
  }
}
variable "resource_group_name" {
  type        = string
  description = "Existing RG by default; new poc- RG when create_resource_group=true."
  validation {
    condition     = !var.create_resource_group || startswith(var.resource_group_name, "poc-")
    error_message = "New RG name must start with poc-."
  }
}
variable "create_resource_group" {
  type    = bool
  default = false
}
variable "location" {
  type    = string
  default = null
  validation {
    condition     = !var.create_resource_group || try(length(trimspace(var.location)) > 0, false)
    error_message = "location is required when creating a new RG."
  }
}
variable "name_suffix" {
  type = string
  validation {
    condition     = can(regex("^[a-z0-9]{4,16}$", var.name_suffix))
    error_message = "Use 4-16 lowercase alphanumeric characters."
  }
}
variable "batch_mode" {
  type        = string
  default     = "existing"
  description = "existing reference only, or new isolated BatchService Windows pool. No UserSubscription or private-network mode."
  validation {
    condition     = contains(["existing", "new"], var.batch_mode)
    error_message = "batch_mode must be existing or new."
  }
}
variable "batch_account_id" {
  type    = string
  default = null
  validation {
    condition     = var.batch_mode == "existing" ? can(regex("(?i)^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft.Batch/batchAccounts/[^/]+$", var.batch_account_id)) : var.batch_account_id == null
    error_message = "Existing mode requires a complete Batch account ARM ID; new mode must leave it null."
  }
}
variable "batch_endpoint" {
  type    = string
  default = null
  validation {
    condition     = var.batch_mode == "existing" ? can(regex("^https://[^/]+/?$", var.batch_endpoint)) : var.batch_endpoint == null
    error_message = "Existing mode requires an HTTPS Batch endpoint; new mode must leave it null."
  }
}
variable "batch_pool_id" {
  type    = string
  default = null
  validation {
    condition     = var.batch_mode == "existing" ? can(regex("^[A-Za-z0-9_-]{1,64}$", var.batch_pool_id)) : var.batch_pool_id == null
    error_message = "Existing mode requires a valid Pool ID; new mode must leave it null."
  }
}
variable "gallery_image_version_id" {
  type        = string
  default     = null
  description = "Existing generalized Windows ACG version. New gallery mode reads and uses the version; never modifies it."
  validation {
    condition     = (var.batch_mode == "new" && var.batch_image_source == "gallery") ? (var.gallery_image_version_id != null && can(regex("(?i)^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft.Compute/galleries/[^/]+/images/[^/]+/versions/[0-9]+\\.[0-9]+\\.[0-9]+$", var.gallery_image_version_id))) : var.gallery_image_version_id == null
    error_message = "New gallery mode requires an exact existing ACG version ARM ID (not latest); existing and marketplace modes must leave it null."
  }
}
variable "batch_image_source" {
  type    = string
  default = "gallery"
  validation {
    condition     = contains(["gallery", "marketplace"], var.batch_image_source)
    error_message = "batch_image_source must be gallery or marketplace."
  }
}
variable "marketplace_image" {
  type        = object({ publisher = string, offer = string, sku = string, version = string })
  default     = null
  description = "Exact Batch-supported Windows image, with an explicit supported-images version."
  validation {
    condition     = var.batch_mode == "new" && var.batch_image_source == "marketplace" ? try(alltrue([for value in values(var.marketplace_image) : length(trimspace(value)) > 0]) && lower(var.marketplace_image.version) != "latest", false) : var.marketplace_image == null
    error_message = "Provide all marketplace fields with an explicit version only in new marketplace mode."
  }
}
variable "batch_node_agent_sku_id" {
  type    = string
  default = null
  validation {
    condition     = var.batch_mode == "existing" ? var.batch_node_agent_sku_id == null : can(regex("^batch\\.node\\.windows", var.batch_node_agent_sku_id))
    error_message = "New mode requires a matching Windows Batch node agent SKU; existing mode must leave it null."
  }
}
variable "batch_vm_size" {
  type    = string
  default = "Standard_D2s_v5"
}
variable "batch_node_count" {
  type        = number
  default     = 1
  description = "Total target node count for new pools, allocated entirely to batch_node_type. Ignored for existing pools."
  validation {
    condition     = var.batch_node_count >= 1 && var.batch_node_count <= 3 && floor(var.batch_node_count) == var.batch_node_count
    error_message = "Use 1-3 target nodes so deployment readiness can be checked."
  }
}
variable "batch_node_type" {
  type        = string
  default     = "dedicated"
  description = "New pools only: dedicated or spot (Batch low-priority nodes). No dedicated fallback for Spot. Existing pools are never resized."
  validation {
    condition     = contains(["dedicated", "spot"], var.batch_node_type)
    error_message = "batch_node_type must be dedicated or spot."
  }
}
variable "batch_scale_mode" {
  type        = string
  default     = "fixed"
  description = "New pools only: fixed target nodes or 0-to-batch_node_count autoscale formula. Ignored for existing pools."
  validation {
    condition     = contains(["fixed", "autoscale"], var.batch_scale_mode)
    error_message = "batch_scale_mode must be fixed or autoscale."
  }
}
variable "batch_autoscale_evaluation_interval" {
  type        = string
  default     = "PT5M"
  description = "New autoscale pools only: evaluation interval (minimum PT5M)."
  validation {
    condition     = contains(["PT5M", "PT10M", "PT15M", "PT30M"], var.batch_autoscale_evaluation_interval)
    error_message = "batch_autoscale_evaluation_interval must be PT5M, PT10M, PT15M, or PT30M."
  }
}
variable "batch_max_tasks_per_node" {
  type    = number
  default = 1
  validation {
    condition     = contains([1, 2, 3], var.batch_max_tasks_per_node)
    error_message = "Use 1-3 tasks per node; verify VM core limits separately."
  }
}
variable "batch_diagnostics_enabled" {
  type    = bool
  default = false
  validation {
    condition     = !var.batch_diagnostics_enabled || var.batch_mode == "new"
    error_message = "Only new PoC Batch accounts may have managed diagnostics."
  }
}
variable "batch_poll_seconds" {
  type    = number
  default = 10
  validation {
    condition     = var.batch_poll_seconds >= 1 && var.batch_poll_seconds <= 300 && floor(var.batch_poll_seconds) == var.batch_poll_seconds
    error_message = "Polling interval must be an integer from 1 to 300 seconds."
  }
}
variable "workflow_timeout_minutes" {
  type    = number
  default = 15
  validation {
    condition     = var.workflow_timeout_minutes >= 1 && var.workflow_timeout_minutes <= 1440 && floor(var.workflow_timeout_minutes) == var.workflow_timeout_minutes
    error_message = "Workflow timeout must be an integer from 1 to 1440 minutes."
  }
}

variable "storage_public_network_access_enabled" {
  type        = bool
  default     = true
  description = "Enable public network access for the storage account."
}

variable "enable_app_service" {
  type        = bool
  default     = false
  description = "Enable optional ASP.NET Core business App Service."
}

variable "app_service_sku" {
  type        = string
  default     = "B1"
  description = "SKU for optional App Service Plan (Linux)."
  validation {
    condition     = contains(["B1", "B2", "B3", "S1", "S2", "S3", "P1v2", "P2v2", "P3v2", "P1v3", "P2v3", "P3v3"], var.app_service_sku)
    error_message = "app_service_sku must be a valid supported App Service plan SKU."
  }
}

variable "app_service_outbound_subnet_prefix" {
  type        = string
  default     = "10.80.5.0/24"
  description = "Subnet prefix for optional App Service outbound VNet integration."
  validation {
    condition     = can(cidrnetmask(var.app_service_outbound_subnet_prefix))
    error_message = "Use a separate IPv4 App Service outbound subnet CIDR within the validation VNet."
  }
}

