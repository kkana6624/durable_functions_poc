variable "subscription_id" {
  type = string
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
    condition     = var.gallery_image_version_id == null ? !(var.batch_mode == "new" && var.batch_image_source == "gallery") : can(regex("(?i)^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft.Compute/galleries/[^/]+/images/[^/]+/versions/[0-9]+\\.[0-9]+\\.[0-9]+$", var.gallery_image_version_id))
    error_message = "New gallery mode requires an exact existing ACG version ARM ID (not latest)."
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
  type    = number
  default = 1
  validation {
    condition     = var.batch_node_count >= 1 && var.batch_node_count <= 3 && floor(var.batch_node_count) == var.batch_node_count
    error_message = "Use 1-3 dedicated nodes so deployment readiness can be checked."
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
