# Repository context

Before substantial work, read `docs/project-memory.md` and the applicable design/validation documents linked there. Check the current Git state; the memory records a historical checkpoint, not a substitute for verification.

For substantial investigation or implementation, use sub-agents for independent work that can run in parallel. Assign official-documentation research to a dedicated investigation agent, separate from implementation. Report contradictions or unsupported guarantees as they are found.

Use official primary documentation for Azure implementation decisions. Distinguish local Mock/Fake success from actual Azure validation. Keep secrets, Function keys, SAS tokens, and Durable management URLs out of logs and repository files.
